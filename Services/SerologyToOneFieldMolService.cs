
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HLAImputation.Services
{
    /// <summary>
    /// Serology -> one-field molecular conversion using a lookup table:
    ///   Gene,Antigen,OneFieldMol
    ///
    /// NEW FEATURE:
    /// - Expand ambiguous serology into multiple candidate genotypes by creating additional rows
    ///   with TxID suffixes ".1", ".2", ...
    /// - Example: A9 -> A*23 OR A*24 creates multiple variants.
    ///
    /// Existing behavior preserved:
    /// - ConvertRecord returns a single "best-effort" record (chooses first mapping deterministically).
    /// </summary>
    public sealed class SerologyToOneFieldMolService
    {
        // geneKey (e.g. "A*", "DRB1*") -> antigenInt -> list of mapped tokens (strings)
        private readonly Dictionary<string, Dictionary<int, List<string>>> _map;

        public SerologyToOneFieldMolService(string csvPath)
        {
            _map = Load(csvPath);
        }

        private static Dictionary<string, Dictionary<int, List<string>>> Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Antigen-to-onefield molecular conversion table not found", path);

            var result = new Dictionary<string, Dictionary<int, List<string>>>(StringComparer.OrdinalIgnoreCase);

            using var sr = new StreamReader(path);
            var header = sr.ReadLine(); // Gene,Antigen,OneFieldMol
            if (header == null) return result;

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(',');
                if (parts.Length < 3) continue;

                string gene = NormalizeGeneKey(parts[0].Trim().Replace("\"", ""));
                string antigenStr = parts[1].Trim().Replace("\"", "");
                string mol = parts[2].Trim().Replace("\"", "");

                if (string.IsNullOrWhiteSpace(gene)) continue;

                if (!int.TryParse(antigenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int antigen))
                    continue;

                if (!result.TryGetValue(gene, out var byAntigen))
                {
                    byAntigen = new Dictionary<int, List<string>>();
                    result[gene] = byAntigen;
                }

                if (!byAntigen.TryGetValue(antigen, out var list))
                {
                    list = new List<string>();
                    byAntigen[antigen] = list;
                }

                list.Add(mol);
            }

            return result;
        }

        private static string NormalizeGeneKey(string gene)
        {
            if (string.IsNullOrWhiteSpace(gene)) return "";
            gene = gene.Trim().Replace(" ", "").Replace("\t", "").Replace("\uFEFF", "").Replace("\\", "");
            gene = gene.ToUpperInvariant();
            if (!gene.EndsWith("*", StringComparison.Ordinal)) gene += "*";
            return gene;
        }

        /// <summary>
        /// Convert one token to one-field mol (single best-effort mapping).
        /// </summary>
        public string ConvertToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "";
            token = token.Trim();

            if (token.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                return "";

            if (!TryParseGeneAndAntigen(token, out string geneKey, out int antigen))
                return token;

            if (antigen == 0) return "";

            geneKey = NormalizeGeneKey(geneKey);

            if (_map.TryGetValue(geneKey, out var byAntigen) &&
                byAntigen.TryGetValue(antigen, out var mappedList) &&
                mappedList.Count > 0)
            {
                // keep your existing deterministic behavior
                string mapped = mappedList[0];
                if (string.IsNullOrWhiteSpace(mapped) || mapped.Equals("NA", StringComparison.OrdinalIgnoreCase))
                    return "";

                string group = mapped.Split(':')[0].Trim();
                if (!int.TryParse(group, out int groupNum))
                    groupNum = antigen;

                return geneKey + Pad2(groupNum);
            }

            return geneKey + Pad2(antigen);
        }

        /// <summary>
        /// Convert an InputRecord copy: normalizes tokens first, then converts supported loci to one-field mol.
        /// This produces a SINGLE record (best-effort).
        /// </summary>
        public Models.InputRecord ConvertRecord(Models.InputRecord input)
        {
            var copy = new Models.InputRecord
            {
                TxID = input.TxID,
                Race = input.Race,
                PatType = input.PatType,
                Loci = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (var kv in input.Loci)
            {
                string locus = kv.Key;
                string a1 = kv.Value.Length > 0 ? kv.Value[0] ?? "" : "";
                string a2 = kv.Value.Length > 1 ? kv.Value[1] ?? "" : "";

                // normalize first (handles missing "A*" prefixes etc.)
                a1 = AlleleInputNormalizer.NormalizeToken(locus, a1);
                a2 = AlleleInputNormalizer.NormalizeToken(locus, a2);

                if (IsSupportedLocus(locus))
                {
                    a1 = ConvertNormalizedAlleleToOneFieldMol(locus, a1);
                    a2 = ConvertNormalizedAlleleToOneFieldMol(locus, a2);
                }

                copy.Loci[locus] = new[] { a1, a2 };
            }

            return copy;
        }

        /// <summary>
        /// NEW: Expand a (normalized) record into multiple candidate one-field molecular genotypes.
        /// - Adds suffix ".1", ".2", ... to TxID.
        /// - Only expands loci supported by the mapping table (A,B,C,DRB1,DQB1).
        /// - Uses maxVariantsPerSample to prevent combinatorial explosion.
        /// </summary>
        public List<Models.InputRecord> ExpandRecordVariants(Models.InputRecord normalizedInput, int maxVariantsPerSample = 50)
        {
            // Step 1: Build candidate lists per allele-slot for supported loci
            // Slot key: (locus, alleleIndex 0/1)
            var slotCandidates = new List<(string locus, int idx, List<string> candidates)>();

            foreach (var kv in normalizedInput.Loci)
            {
                string locus = kv.Key;
                string a1 = kv.Value.Length > 0 ? kv.Value[0] ?? "" : "";
                string a2 = kv.Value.Length > 1 ? kv.Value[1] ?? "" : "";

                if (!IsSupportedLocus(locus))
                    continue;

                // Ensure tokens are normalized (caller should already do this, but safe)
                a1 = AlleleInputNormalizer.NormalizeToken(locus, a1);
                a2 = AlleleInputNormalizer.NormalizeToken(locus, a2);

                slotCandidates.Add((locus, 0, GetAllCandidatesForNormalized(locus, a1)));
                slotCandidates.Add((locus, 1, GetAllCandidatesForNormalized(locus, a2)));
            }

            // Step 2: Cartesian product across all slots (bounded by maxVariantsPerSample)
            // Start with one empty assignment
            var assignments = new List<Dictionary<(string locus, int idx), string>>
            {
                new Dictionary<(string locus, int idx), string>()
            };

            foreach (var slot in slotCandidates)
            {
                var next = new List<Dictionary<(string locus, int idx), string>>();

                foreach (var a in assignments)
                {
                    foreach (var cand in slot.candidates)
                    {
                        var copy = new Dictionary<(string locus, int idx), string>(a);
                        copy[(slot.locus, slot.idx)] = cand;
                        next.Add(copy);

                        if (next.Count >= maxVariantsPerSample)
                            break;
                    }

                    if (next.Count >= maxVariantsPerSample)
                        break;
                }

                assignments = next;

                if (assignments.Count >= maxVariantsPerSample)
                    break;
            }

            // Step 3: Build variant InputRecords from assignments
            var variants = new List<Models.InputRecord>(assignments.Count);

            for (int k = 0; k < assignments.Count; k++)
            {
                var assign = assignments[k];

                var rec = new Models.InputRecord
                {
                    TxID = $"{normalizedInput.TxID}.{k + 1}",
                    Race = normalizedInput.Race,
                    PatType = normalizedInput.PatType,
                    Loci = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                };

                // copy all loci, then override supported ones with assigned candidates
                foreach (var kv in normalizedInput.Loci)
                {
                    string locus = kv.Key;
                    string a1 = kv.Value.Length > 0 ? kv.Value[0] ?? "" : "";
                    string a2 = kv.Value.Length > 1 ? kv.Value[1] ?? "" : "";

                    if (IsSupportedLocus(locus))
                    {
                        if (assign.TryGetValue((locus, 0), out var v1)) a1 = v1;
                        if (assign.TryGetValue((locus, 1), out var v2)) a2 = v2;
                    }

                    rec.Loci[locus] = new[] { a1, a2 };
                }

                variants.Add(rec);
            }

            // If there were no supported loci slots (or all blanks), still return a single variant
            if (variants.Count == 0)
            {
                var rec = new Models.InputRecord
                {
                    TxID = $"{normalizedInput.TxID}.1",
                    Race = normalizedInput.Race,
                    PatType = normalizedInput.PatType,
                    Loci = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                };

                foreach (var kv in normalizedInput.Loci)
                {
                    string a1 = kv.Value.Length > 0 ? kv.Value[0] ?? "" : "";
                    string a2 = kv.Value.Length > 1 ? kv.Value[1] ?? "" : "";
                    rec.Loci[kv.Key] = new[] { a1, a2 };
                }

                variants.Add(rec);
            }

            return variants;
        }

        // Return ALL candidate one-field mol outputs for a normalized allele like "A*09" or "A*09:01"
        private List<string> GetAllCandidatesForNormalized(string locus, string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized)) return new List<string> { "" };

            int star = normalized.IndexOf('*');
            if (star < 0 || star == normalized.Length - 1)
                return new List<string> { normalized };

            string after = normalized.Substring(star + 1);
            string firstField = after.Split(':')[0];

            if (!int.TryParse(firstField, out int antigen) || antigen == 0)
                return new List<string> { "" };

            string geneKey = NormalizeGeneKey(locus.ToUpperInvariant());

            // If mapping exists, return ALL unique numeric groups (first part before ':') sorted
            if (_map.TryGetValue(geneKey, out var byAntigen) &&
                byAntigen.TryGetValue(antigen, out var mappedList) &&
                mappedList.Count > 0)
            {
                var nums = mappedList
                    .Where(m => !string.IsNullOrWhiteSpace(m) && !m.Equals("NA", StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.Split(':')[0].Trim())
                    .Select(s => int.TryParse(s, out int n) ? (int?)n : null)
                    .Where(n => n.HasValue)
                    .Select(n => n!.Value)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();

                if (nums.Count > 0)
                    return nums.Select(n => $"{locus.ToUpperInvariant()}*{Pad2(n)}").ToList();
            }

            // fallback: locus*antigen (padded)
            return new List<string> { $"{locus.ToUpperInvariant()}*{Pad2(antigen)}" };
        }

        private string ConvertNormalizedAlleleToOneFieldMol(string locus, string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized)) return "";
            int star = normalized.IndexOf('*');
            if (star < 0 || star == normalized.Length - 1) return normalized;

            string after = normalized.Substring(star + 1);
            string firstField = after.Split(':')[0];

            if (!int.TryParse(firstField, out int antigen) || antigen == 0) return "";

            string geneKey = NormalizeGeneKey(locus.ToUpperInvariant());

            if (_map.TryGetValue(geneKey, out var byAntigen) &&
                byAntigen.TryGetValue(antigen, out var mappedList) &&
                mappedList.Count > 0)
            {
                // deterministic: first row in file
                string mapped = mappedList[0];
                if (string.IsNullOrWhiteSpace(mapped) || mapped.Equals("NA", StringComparison.OrdinalIgnoreCase))
                    return "";

                string group = mapped.Split(':')[0].Trim();
                if (!int.TryParse(group, out int groupNum))
                    groupNum = antigen;

                return $"{locus.ToUpperInvariant()}*{Pad2(groupNum)}";
            }

            return $"{locus.ToUpperInvariant()}*{Pad2(antigen)}";
        }

        private static bool IsSupportedLocus(string locus)
        {
            return locus.Equals("A", StringComparison.OrdinalIgnoreCase)
                || locus.Equals("B", StringComparison.OrdinalIgnoreCase)
                || locus.Equals("C", StringComparison.OrdinalIgnoreCase)
                || locus.Equals("DRB1", StringComparison.OrdinalIgnoreCase)
                || locus.Equals("DQB1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseGeneAndAntigen(string token, out string geneKey, out int antigen)
        {
            geneKey = "";
            antigen = 0;

            token = token.Trim().Replace(" ", "");

            string locus;
            if (token.StartsWith("DRB1", StringComparison.OrdinalIgnoreCase)) locus = "DRB1";
            else if (token.StartsWith("DQB1", StringComparison.OrdinalIgnoreCase)) locus = "DQB1";
            else if (token.StartsWith("A", StringComparison.OrdinalIgnoreCase)) locus = "A";
            else if (token.StartsWith("B", StringComparison.OrdinalIgnoreCase)) locus = "B";
            else if (token.StartsWith("C", StringComparison.OrdinalIgnoreCase)) locus = "C";
            else return false;

            geneKey = locus + "*";

            string after = token.Contains("*")
                ? token.Split('*')[1]
                : token.Substring(locus.Length);

            antigen = ExtractLeadingInt(after);
            return true;
        }

        private static int ExtractLeadingInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            int i = 0;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == 0) return 0;

            if (int.TryParse(s.Substring(0, i), out int val))
                return val;

            return 0;
        }

        private static string Pad2(int n) => n.ToString("D2", CultureInfo.InvariantCulture);
    }
}
