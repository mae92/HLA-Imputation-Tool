
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HLAImputation.Models;

namespace HLAImputation.Services
{
    public static class CsvLoader
    {


        // ===========================================================
        // ✅ DRB345 NORMALIZATION (CsvLoader scope)
        // ===========================================================

        private static string NormalizeDRB345Allele(string allele, string locusHint = "")
        {
            if (string.IsNullOrWhiteSpace(allele))
                return "DRBX*NNNN";

            allele = allele.Trim();

            if (allele.EndsWith("N", StringComparison.OrdinalIgnoreCase))
                return "DRBX*NNNN";

            if (!allele.Contains("*"))
            {
                if (locusHint.Equals("DRB3", StringComparison.OrdinalIgnoreCase))
                    return "DRB3*" + allele;
                if (locusHint.Equals("DRB4", StringComparison.OrdinalIgnoreCase))
                    return "DRB4*" + allele;
                if (locusHint.Equals("DRB5", StringComparison.OrdinalIgnoreCase))
                    return "DRB5*" + allele;

                return "DRB4*" + allele; // safe default
            }

            return allele;
        }



        public static List<InputRecord> LoadInput(string path, Action<int>? progressCallback = null)
        {
            var list = new List<InputRecord>();

            if (!File.Exists(path))
                throw new FileNotFoundException("Input CSV not found", path);

            long totalBytes = new FileInfo(path).Length;
            long processedBytes = 0;
            int lastPct = -1;

            using var sr = new StreamReader(path);

            string? headerLine = sr.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
                return list;

            processedBytes += headerLine.Length + 2;

            var header = headerLine.Split(',');
            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Length; i++)
                col[header[i].Trim()] = i;

            string Get(string[] parts, string name)
                => col.ContainsKey(name) && col[name] < parts.Length ? parts[col[name]].Trim() : "";

            // ===========================================================
            // ✅ NEW: Ensure UNIQUE TxID values by appending .1, .2, ...
            // Only activates if a duplicate is encountered.
            // Example:
            //   first "ABC"  -> "ABC" (temporarily)
            //   second "ABC" -> retroactively change first to "ABC.1", current becomes "ABC.2"
            //   third "ABC"  -> "ABC.3"
            // ===========================================================
            var txCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var firstIndexByTx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            string? line;
            int rowCount = 0;

            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                processedBytes += line.Length + 2;
                rowCount++;

                // progress every ~500 rows
                if (progressCallback != null && rowCount % 500 == 0)
                {
                    int pct = totalBytes <= 0 ? 0 : (int)(processedBytes * 100 / totalBytes);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        progressCallback(pct);
                    }
                }

                var parts = line.Split(',');

                // Read raw TxID
                string baseTx = Get(parts, "TxID");

                // If TxID missing, create a deterministic placeholder
                if (string.IsNullOrWhiteSpace(baseTx))
                    baseTx = $"ROW{rowCount}";

                // Determine unique TxID, retroactively renaming first duplicate to ".1"
                string finalTx;

                if (!txCounts.ContainsKey(baseTx))
                {
                    txCounts[baseTx] = 1;
                    firstIndexByTx[baseTx] = list.Count; // index where this record will be added
                    finalTx = baseTx;                    // keep as-is for now
                }
                else
                {
                    txCounts[baseTx] += 1;
                    int k = txCounts[baseTx];

                    // When we see the SECOND occurrence, retroactively rename the first to ".1"
                    if (k == 2)
                    {
                        int firstIdx = firstIndexByTx[baseTx];
                        if (firstIdx >= 0 && firstIdx < list.Count)
                        {
                            // Only rename if it wasn't already renamed
                            if (string.Equals(list[firstIdx].TxID, baseTx, StringComparison.OrdinalIgnoreCase))
                                list[firstIdx].TxID = $"{baseTx}.1";
                        }
                    }

                    finalTx = $"{baseTx}.{k}";
                }

                var r = new InputRecord
                {
                    TxID = finalTx,
                    Race = Get(parts, "Race").ToUpperInvariant(),
                    PatType = Get(parts, "PatType"),
                };

                r.Loci["A"] = new[] { Get(parts, "a1"), Get(parts, "a2") };
                r.Loci["B"] = new[] { Get(parts, "b1"), Get(parts, "b2") };
                r.Loci["C"] = new[] { Get(parts, "c1"), Get(parts, "c2") };
                r.Loci["DRB1"] = new[] { Get(parts, "drb1"), Get(parts, "drb2") };
                r.Loci["DQB1"] = new[] { Get(parts, "dqb1"), Get(parts, "dqb2") };
                r.Loci["DQA1"] = new[] { Get(parts, "dqa1"), Get(parts, "dqa2") };
                r.Loci["DPB1"] = new[] { Get(parts, "dpb1"), Get(parts, "dpb2") };
                r.Loci["DPA1"] = new[] { Get(parts, "dpa1"), Get(parts, "dpa2") };

                // ✅ Build DRB345 from drb3/4/5 columns

                // ✅ Build DRB345 from drb3/4/5 columns while preserving the correct DRB3 / DRB4 / DRB5 prefix
                var drb345List = new List<(string Allele, string Prefix)>
{
    (Get(parts, "drb31"), "DRB3"),
    (Get(parts, "drb32"), "DRB3"),
    (Get(parts, "drb41"), "DRB4"),
    (Get(parts, "drb42"), "DRB4"),
    (Get(parts, "drb51"), "DRB5"),
    (Get(parts, "drb52"), "DRB5")
}
                .Where(x => !string.IsNullOrWhiteSpace(x.Allele))
                .ToList();

                // ✅ Apply your rules
                string drb345_1 = "";
                string drb345_2 = "";

                string drb345_1_prefix = "";
                string drb345_2_prefix = "";

                if (drb345List.Count == 1)
                {
                    // Only one allele → duplicate
                    drb345_1 = drb345List[0].Allele;
                    drb345_2 = drb345List[0].Allele;

                    drb345_1_prefix = drb345List[0].Prefix;
                    drb345_2_prefix = drb345List[0].Prefix;
                }
                else if (drb345List.Count >= 2)
                {
                    // Take first two
                    drb345_1 = drb345List[0].Allele;
                    drb345_2 = drb345List[1].Allele;

                    drb345_1_prefix = drb345List[0].Prefix;
                    drb345_2_prefix = drb345List[1].Prefix;
                }

                r.Loci["DRB345"] = new[]
                {
    NormalizeDRB345Allele(drb345_1, drb345_1_prefix),
    NormalizeDRB345Allele(drb345_2, drb345_2_prefix)
};




                list.Add(r);
            }

            progressCallback?.Invoke(100);
            return list;
        }
    }
}
