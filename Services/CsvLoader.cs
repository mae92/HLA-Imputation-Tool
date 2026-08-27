
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
        // ✅ NEW: Reference-alignment allele fixes.
        // Some input alleles carry expression suffixes (N / M) that do NOT
        // exist in the imputation reference panel. Map each one to the
        // expressed allele the reference uses so the input can match.
        // Keys are matched case-insensitively against the full "LOCUS*fields" string.
        // ===========================================================
        private static readonly Dictionary<string, string> InputAlleleFixes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "A*24:09N",    "A*24:02"    },
            { "B*51:11N",    "B*51:01"    },
            { "C*04:09M",    "C*04:01"    },
            { "DRB4*01:03N", "DRB4*01:01" },
            { "DRB5*01:08N", "DRB5*01:02" },
        };

        private static string FixInputAllele(string allele)
        {
            if (string.IsNullOrWhiteSpace(allele)) return allele;
            string key = allele.Trim();
            return InputAlleleFixes.TryGetValue(key, out var fixedAllele)
                ? fixedAllele
                : allele;
        }


        // ===========================================================
        // ✅ DRB345 NORMALIZATION (CsvLoader scope)
        // ===========================================================

        private static string NormalizeDRB345Allele(
string allele, string locusHint, List<string> notes, string slotLabel)
        {
            if (string.IsNullOrWhiteSpace(allele))
                return "DRBX*NNNN"; // blank handled/logged by the caller (hemizygous / absent)

            allele = allele.Trim();

            // Attach the gene prefix FIRST so reference-fix keys like "DRB4*01:03N"
            // can match even if the lab stored the allele without a "DRB4*" prefix.
            if (!allele.Contains("*"))
            {
                if (locusHint.Equals("DRB3", StringComparison.OrdinalIgnoreCase))
                    allele = "DRB3*" + allele;
                else if (locusHint.Equals("DRB4", StringComparison.OrdinalIgnoreCase))
                    allele = "DRB4*" + allele;
                else if (locusHint.Equals("DRB5", StringComparison.OrdinalIgnoreCase))
                    allele = "DRB5*" + allele;
                else
                    allele = "DRB4*" + allele; // safe default
            }

            // Reference-alignment fix BEFORE null-collapse (e.g., DRB4*01:03N -> DRB4*01:01).
            string beforeFix = allele;
            allele = FixInputAllele(allele);
            if (!string.Equals(allele, beforeFix, StringComparison.OrdinalIgnoreCase))
                notes?.Add($"DRB345 reference-alignment fix [{slotLabel}]: {beforeFix} → {allele}");

            // Any allele still ending in N is a TRUE null -> placeholder.
            if (allele.EndsWith("N", StringComparison.OrdinalIgnoreCase))
            {
                notes?.Add($"DRB345 null allele collapsed [{slotLabel}]: {beforeFix} → DRBX*NNNN");
                return "DRBX*NNNN";
            }

            return allele;
        }


        // ===========================================================
        // ✅ NEW: Detect the lab's homozygous marker ("-") in a DRB3/4/5 slot.
        // "-" means "same as the paired allele" (true homozygote),
        // NOT a new allele and NOT a null.
        // ===========================================================
        private static bool IsHomozygousMarker(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return s == "-" || s == "–" || s == "—"; // hyphen + en/em dash, just in case
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

            // Row-scoped audit notes. Reassigned at the top of each row (see B-2).
            List<string> currentRowNotes = new List<string>();

            string Get(string[] parts, string name)
            {
                if (!col.ContainsKey(name) || col[name] >= parts.Length) return "";
                string raw = parts[col[name]].Trim();
                if (string.IsNullOrWhiteSpace(raw)) return "";

                string fixedVal = FixInputAllele(raw);
                if (!string.Equals(fixedVal, raw, StringComparison.OrdinalIgnoreCase))
                    currentRowNotes.Add($"Reference-alignment fix [{name}]: {raw} → {fixedVal}");

                return fixedVal;
            }

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

                // ✅ NEW: start a fresh audit list for this row.
                currentRowNotes = new List<string>();

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


                // ✅ Build DRB345 from drb3/4/5 columns while preserving gene identity.
                // IMPORTANT: the lab uses "-" in the second slot to mean HOMOZYGOUS
                // ("same as the paired allele"), which is different from a blank slot
                // (HEMIZYGOUS -> the second copy is the null placeholder DRBX*NNNN).
                var drb345Raw = new List<(string Allele, string Prefix)>
                {
                    (Get(parts, "drb31"), "DRB3"),
                    (Get(parts, "drb32"), "DRB3"),
                    (Get(parts, "drb41"), "DRB4"),
                    (Get(parts, "drb42"), "DRB4"),
                    (Get(parts, "drb51"), "DRB5"),
                    (Get(parts, "drb52"), "DRB5")
                };

                // Was a homozygous marker ("-") present anywhere in the DRB3/4/5 slots?
                bool drbHasHomozygousMarker =
                    drb345Raw.Any(x => IsHomozygousMarker(x.Allele));

                // Keep only genuinely expressed alleles (drop blanks AND "-").
                var drb345Real = drb345Raw
                    .Where(x => !string.IsNullOrWhiteSpace(x.Allele) && !IsHomozygousMarker(x.Allele))
                    .ToList();

                string drb345_1 = "";
                string drb345_2 = "";
                string drb345_1_prefix = "";
                string drb345_2_prefix = "";

                if (drb345Real.Count >= 2)
                {
                    // Two expressed alleles (e.g., DR3/DR4, or a true DRB3 heterozygote).
                    drb345_1 = drb345Real[0].Allele; drb345_1_prefix = drb345Real[0].Prefix;
                    drb345_2 = drb345Real[1].Allele; drb345_2_prefix = drb345Real[1].Prefix;
                }
                else if (drb345Real.Count == 1)
                {
                    drb345_1 = drb345Real[0].Allele; drb345_1_prefix = drb345Real[0].Prefix;
                    if (drbHasHomozygousMarker)
                    {
                        // "-" present => TRUE homozygote => copy the expressed allele.
                        drb345_2 = drb345Real[0].Allele; drb345_2_prefix = drb345Real[0].Prefix;
                        currentRowNotes.Add(
                            $"DRB345 homozygous marker '-' expanded: {drb345Real[0].Allele} copied to both slots");
                    }
                    else
                    {
                        // No marker => hemizygous => second copy is the null placeholder.
                        drb345_2 = ""; drb345_2_prefix = ""; // NormalizeDRB345Allele("") => DRBX*NNNN
                        currentRowNotes.Add(
                            $"DRB345 hemizygous: single {drb345Real[0].Allele}, second slot set to DRBX*NNNN");
                    }
                }
                else
                {
                    // No expressed DRB3/4/5 => both null.
                    drb345_1 = ""; drb345_1_prefix = "";
                    drb345_2 = ""; drb345_2_prefix = "";
                }

                r.Loci["DRB345"] = new[]
{
                    NormalizeDRB345Allele(drb345_1, drb345_1_prefix, currentRowNotes, "slot1"),
                    NormalizeDRB345Allele(drb345_2, drb345_2_prefix, currentRowNotes, "slot2")
                };

                // ✅ NEW: attach this row's audit notes to the record.
                r.CleaningNotes = currentRowNotes;

                list.Add(r);
            }

            progressCallback?.Invoke(100);
            return list;
        }
    }
}
