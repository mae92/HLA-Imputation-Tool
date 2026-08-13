
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using HLAImputation.Models;

namespace HLAImputation.Services
{
    public sealed class ReferenceStore
    {
        private readonly string _csvPath;
        private readonly string _dbPath;

        private static readonly string[] FiveRaceCols = { "AFA", "API", "CAU", "HIS", "NAM" };
        private static readonly string[] AlleleCols = { "a", "c", "b", "drb345", "drb1", "dqa1", "dqb1", "dpa1", "dpb1" };

        private static readonly Dictionary<string, string> ColToLocus = new(StringComparer.OrdinalIgnoreCase)
        {
            { "a", "A" }, { "b", "B" }, { "c", "C" },
            { "drb1", "DRB1" }, { "drb345", "DRB345" },
            { "dqa1", "DQA1" }, { "dqb1", "DQB1" },
            { "dpa1", "DPA1" }, { "dpb1", "DPB1" }
        };

        public ReferenceStore(string csvPath, string dbPath)
        {
            _csvPath = csvPath;
            _dbPath = dbPath;
        }

        /// <summary>
        /// Build DB only if missing/invalid. Uses a temp DB then swaps into place.
        /// Progress:
        ///  - BuildDatabaseFromCsv drives refProgress 0-100 and dbProgress 0-99
        ///  - EnsureBuilt sets dbProgress to 100 only after the final swap succeeds.
        /// </summary>
        public void EnsureBuilt(Action<int>? refProgressCallback = null, Action<int>? dbProgressCallback = null)
        {
            if (IsDbValid(_dbPath))
            {
                Debug.WriteLine("ReferenceStore: DB already valid — skipping build.");
                refProgressCallback?.Invoke(100);
                dbProgressCallback?.Invoke(100);
                return;
            }

            Debug.WriteLine("ReferenceStore: DB missing/invalid — rebuilding.");

            var tmpPath = _dbPath + ".tmp";

            SafeDelete(tmpPath);

            BuildDatabaseFromCsv(tmpPath, refProgressCallback, dbProgressCallback);

            // IMPORTANT: Clear pooled connections before file operations.
            // This releases file handles held by the pool. [2](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection.clearallpools?view=msdata-sqlite-9.0.0)[3](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection?view=msdata-sqlite-9.0.0)
            SqliteConnection.ClearAllPools();

            // Now atomically swap: delete old, move tmp into place
            SafeDelete(_dbPath);

            SafeMoveWithRetry(tmpPath, _dbPath);

            // Only now is DB truly "done"
            dbProgressCallback?.Invoke(100);
        }

        /// <summary>
        /// Stronger validity check: file exists, has Haplotypes table, and has rows.
        /// Uses Pooling=False to avoid leaving pooled locks during validation.
        /// </summary>
        private static bool IsDbValid(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;

                var fi = new FileInfo(path);
                if (fi.Length < 1024) return false;

                var cs = $"Data Source={path};Pooling=False;";
                using var conn = new SqliteConnection(cs);
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Haplotypes';";
                    var result = cmd.ExecuteScalar();
                    if (result == null || result.ToString() != "Haplotypes") return false;
                }

                using (var cmd2 = conn.CreateCommand())
                {
                    cmd2.CommandText = "SELECT COUNT(*) FROM Haplotypes;";
                    var n = Convert.ToInt64(cmd2.ExecuteScalar());
                    return n > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Builds the database at targetDbPath (.tmp). Uses Pooling=False so the file handle releases ASAP.
        /// dbProgressCallback goes up to 99 here; 100 is set after the final swap in EnsureBuilt.
        /// </summary>
        private void BuildDatabaseFromCsv(string targetDbPath, Action<int>? refProgressCallback, Action<int>? dbProgressCallback)
        {
            if (!File.Exists(_csvPath))
                throw new FileNotFoundException("Reference CSV not found", _csvPath);

            Directory.CreateDirectory(Path.GetDirectoryName(targetDbPath)!);

            refProgressCallback?.Invoke(0);
            dbProgressCallback?.Invoke(0);

            long totalBytes = new FileInfo(_csvPath).Length;
            long processedBytes = 0;

            int lastRefPct = -1;
            int lastDbPct = -1;

            using var sr = new StreamReader(_csvPath);

            string? headerLine = sr.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
                throw new Exception("Reference CSV header missing.");

            processedBytes += headerLine.Length + 2;

            var header = SplitSmart(headerLine);
            var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Length; i++)
                colIndex[header[i].Trim()] = i;

            foreach (var ac in AlleleCols)
                if (!colIndex.ContainsKey(ac))
                    throw new Exception($"Reference CSV missing required allele column '{ac}'");

            int lastAllele = colIndex["dpb1"];
            var raceCols = header.Skip(lastAllele + 1).Select(h => h.Trim()).Where(h => h.Length > 0).ToList();

            // Pooling=False prevents pooled native handles from keeping the .tmp locked
            var connString = $"Data Source={targetDbPath};Pooling=False;";
            using var conn = new SqliteConnection(connString);
            conn.Open();

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = @"
PRAGMA journal_mode = MEMORY;
PRAGMA synchronous = OFF;
PRAGMA temp_store = MEMORY;";
                pragma.ExecuteNonQuery();
            }

            Report(dbProgressCallback, ref lastDbPct, 1);

            var allCols = new List<string>();
            allCols.AddRange(AlleleCols);
            allCols.Add("HighestOfAnyRace");
            allCols.Add("FiveRaceAverage");
            allCols.AddRange(raceCols);

            string createCols =
                string.Join(",\n", allCols.Select(c =>
                {
                    if (AlleleCols.Contains(c, StringComparer.OrdinalIgnoreCase))
                        return $"[{c}] TEXT";
                    return $"[{c}] REAL";
                }));

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"CREATE TABLE Haplotypes(\n{createCols}\n);";
                cmd.ExecuteNonQuery();
            }

            Report(dbProgressCallback, ref lastDbPct, 5);

            using var tx = conn.BeginTransaction();

            string colList = string.Join(",", allCols.Select(c => $"[{c}]"));
            string paramList = string.Join(",", allCols.Select(c => $"@{c}"));

            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = $"INSERT INTO Haplotypes({colList}) VALUES({paramList});";

            foreach (var c in allCols)
                insert.Parameters.Add(new SqliteParameter("@" + c, null));

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                processedBytes += line.Length + 2;

                int refPct = totalBytes <= 0 ? 0 : (int)(processedBytes * 100 / totalBytes);

                if (refPct != lastRefPct)
                {
                    lastRefPct = refPct;
                    refProgressCallback?.Invoke(refPct);

                    // Map import progress to 5..95 (but we will never set to 100 here)
                    int dbPct = 5 + (int)(refPct * 90.0 / 100.0);
                    if (dbPct != lastDbPct)
                    {
                        lastDbPct = dbPct;
                        dbProgressCallback?.Invoke(dbPct);
                    }
                }

                var parts = SplitSmart(line);
                if (parts.Length < header.Length) continue;

                foreach (var ac in AlleleCols)
                    insert.Parameters["@" + ac].Value = parts[colIndex[ac]].Trim();

                var freqs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var rc in raceCols)
                {
                    double f = ParseDoubleSafe(parts[colIndex[rc]]);
                    freqs[rc] = f;
                    insert.Parameters["@" + rc].Value = f;
                }

                double highest = freqs.Count == 0 ? 0.0 : freqs.Values.Max();
                insert.Parameters["@HighestOfAnyRace"].Value = highest;

                double sum5 = 0.0;
                int n5 = 0;
                foreach (var r5 in FiveRaceCols)
                {
                    if (freqs.ContainsKey(r5))
                    {
                        sum5 += freqs[r5];
                        n5++;
                    }
                }
                double avg5 = n5 == 0 ? 0.0 : (sum5 / n5);
                insert.Parameters["@FiveRaceAverage"].Value = avg5;

                insert.ExecuteNonQuery();
            }

            tx.Commit();

            refProgressCallback?.Invoke(100);

            // Indexes (95 -> 99)
            dbProgressCallback?.Invoke(95);

            using (var idx = conn.CreateCommand())
            {
                idx.CommandText = @"
CREATE INDEX idx_a ON Haplotypes(a);
CREATE INDEX idx_b ON Haplotypes(b);
CREATE INDEX idx_c ON Haplotypes(c);
CREATE INDEX idx_drb1 ON Haplotypes(drb1);
CREATE INDEX idx_drb345 ON Haplotypes(drb345);
CREATE INDEX idx_dqa1 ON Haplotypes(dqa1);
CREATE INDEX idx_dqb1 ON Haplotypes(dqb1);
CREATE INDEX idx_dpa1 ON Haplotypes(dpa1);
CREATE INDEX idx_dpb1 ON Haplotypes(dpb1);";
                idx.ExecuteNonQuery();
            }

            // Do NOT claim "100%" here; swap is still pending
            dbProgressCallback?.Invoke(99);

            // Ensure SQLite flushes everything before returning
            conn.Close();
        }


        public List<Haplotype> QueryTopHaplotypesStepwise(
    InputRecord input,
    List<string> orderedLoci,
    Dictionary<string, bool> useLocus,
    int topN,
    string ifNoRaceUse,
    string ifNoHapsUse,
    out string raceSourceUsed)
        {
            bool hasRace = !string.IsNullOrWhiteSpace(input.Race);

            // Primary column: race column if provided, else FiveRaceAverage (via ifNoRaceUse)
            string primaryCol = hasRace
                ? input.Race.Trim().ToUpperInvariant()
                : ifNoRaceUse;

            var haps = QueryStepwiseInternal(input, orderedLoci, useLocus, topN, primaryCol);

            if (haps.Count > 0)
            {
                raceSourceUsed = hasRace
                    ? "Race-specific (" + primaryCol + ")"
                    : "Five-race average (no race listed)";
                return haps;
            }

            // Fallback (only if different column)
            if (!primaryCol.Equals(ifNoHapsUse, StringComparison.OrdinalIgnoreCase))
            {
                var fallback = QueryStepwiseInternal(input, orderedLoci, useLocus, topN, ifNoHapsUse);

                if (fallback.Count > 0)
                {
                    raceSourceUsed = hasRace
                        ? "Highest of any race (no haplotypes for listed race)"
                        : "Highest of any race (no haplotypes for five-race average)";
                    return fallback;
                }
            }

            raceSourceUsed = "No haplotypes found (any strategy)";
            return haps;
        }

        private List<Haplotype> QueryStepwiseInternal(
            InputRecord input,
            List<string> orderedLoci,
            Dictionary<string, bool> useLocus,
            int topN,
            string freqCol)
        {
            // Build progressively tighter WHERE clauses
            // Start with just freqCol > 0 then add loci constraints one by one
            var where = new List<string> { $"[{freqCol}] > 0" };
            var parameters = new List<SqliteParameter>();
            int p = 0;

            // Map internal locus name -> column name in DB
            var locusToCol = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "A", "a" }, { "B", "b" }, { "C", "c" },
        { "DRB1", "drb1" }, { "DRB345", "drb345" },
        { "DQA1", "dqa1" }, { "DQB1", "dqb1" },
        { "DPA1", "dpa1" }, { "DPB1", "dpb1" }
    };

            foreach (var locus in orderedLoci)
            {
                if (!useLocus.ContainsKey(locus) || !useLocus[locus]) continue;
                if (!input.Loci.ContainsKey(locus)) continue;

                string col = locusToCol[locus];

                var a1 = AlleleUtils.Prefix(input.Loci[locus][0]);
                var a2 = AlleleUtils.Prefix(input.Loci[locus][1]);

                bool use1 = !AlleleUtils.IsMissing(a1);
                bool use2 = !AlleleUtils.IsMissing(a2);

                if (!use1 && !use2) continue;

                var parts = new List<string>();
                if (use1)
                {
                    string pn = "@p" + (p++);
                    parts.Add($"[{col}] LIKE {pn}");
                    parameters.Add(new SqliteParameter(pn, a1 + "%"));
                }
                if (use2)
                {
                    string pn = "@p" + (p++);
                    parts.Add($"[{col}] LIKE {pn}");
                    parameters.Add(new SqliteParameter(pn, a2 + "%"));
                }

                where.Add("(" + string.Join(" OR ", parts) + ")");
            }

            string sql =
        $@"
SELECT a,c,b,drb345,drb1,dqa1,dqb1,dpa1,dpb1,
       [{freqCol}] AS freq
FROM Haplotypes
WHERE {string.Join(" AND ", where)}
ORDER BY [{freqCol}] DESC
LIMIT @topN;
";

            using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False;");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddRange(parameters);
            cmd.Parameters.Add(new SqliteParameter("@topN", topN));

            var list = new List<Haplotype>();

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var alleles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "A", reader.GetString(0) },
            { "C", reader.GetString(1) },
            { "B", reader.GetString(2) },
            { "DRB345", reader.GetString(3) },
            { "DRB1", reader.GetString(4) },
            { "DQA1", reader.GetString(5) },
            { "DQB1", reader.GetString(6) },
            { "DPA1", reader.GetString(7) },
            { "DPB1", reader.GetString(8) }
        };

                double freq = reader.GetDouble(9);

                list.Add(new Haplotype
                {
                    RaceUsed = freqCol,
                    Frequency = freq,
                    Alleles = alleles
                });
            }

            return list;
        }


        private static void SafeDelete(string path)
        {
            if (!File.Exists(path)) return;

            // Clear pools first, then attempt delete with retries
            SqliteConnection.ClearAllPools(); // [2](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection.clearallpools?view=msdata-sqlite-9.0.0)[3](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection?view=msdata-sqlite-9.0.0)

            int retries = 10;
            int delay = 200;
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    File.Delete(path);
                    return;
                }
                catch (IOException)
                {
                    if (i == retries - 1) throw;
                    Thread.Sleep(delay);
                    delay += 100;
                }
            }
        }

        private static void SafeMoveWithRetry(string from, string to)
        {
            // Clear pools before moving (most important fix)
            SqliteConnection.ClearAllPools(); // [2](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection.clearallpools?view=msdata-sqlite-9.0.0)[3](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection?view=msdata-sqlite-9.0.0)

            int retries = 20;
            int delay = 250;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    File.Move(from, to);
                    return;
                }
                catch (IOException)
                {
                    if (i == retries - 1) throw;
                    Thread.Sleep(delay);
                    delay = Math.Min(delay + 150, 2000);
                }
            }
        }

        private static void Report(Action<int>? cb, ref int last, int value)
        {
            if (cb == null) return;
            if (value != last)
            {
                last = value;
                cb(value);
            }
        }

        private static double ParseDoubleSafe(string s)
        {
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v;
            return 0.0;
        }

        private static string[] SplitSmart(string line)
        {
            if (line.Contains('\t')) return line.Split('\t');
            return line.Split(',');
        }
    }
}
