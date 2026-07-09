
using System;
using System.Collections.Generic;
using System.IO;

namespace HLAImputation.Services
{
    public sealed class GGroupConversionService
    {
        // allele -> list of g.group values (some alleles may map to multiple g.groups)
        private readonly Dictionary<string, List<string>> _alleleToGGroups;
        public GGroupConversionService(string conversionTablePath)
        {
            _alleleToGGroups = LoadConversionTable(conversionTablePath);
        }


        private static Dictionary<string, List<string>> LoadConversionTable(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("G-Group conversion table not found", path);

            var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            using var sr = new StreamReader(path);

            // header: g.group,allele
            string? header = sr.ReadLine();
            if (header == null)
                return dict;

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Remove quotes and split by comma
                var parts = line.Replace("\"", "").Split(',');
                if (parts.Length < 2) continue;

                string gGroupRaw = parts[0].Trim();
                string allele = parts[1].Trim();

                if (allele.Length == 0 || gGroupRaw.Length == 0) continue;

                // ✅ Normalize g-group so it NEVER keeps trailing "G"
                string gGroup = NormalizeGGroupValue(gGroupRaw);

                if (!dict.TryGetValue(allele, out var list))
                {
                    list = new List<string>();
                    dict[allele] = list;
                }

                list.Add(gGroup);
            }

            return dict;
        }

        /// <summary>
        /// Normalize g-group strings so we store/return them without trailing "G".
        /// Example: "A*01:01G" -> "A*01:01"
        /// </summary>
        private static string NormalizeGGroupValue(string gGroup)
        {
            if (string.IsNullOrWhiteSpace(gGroup)) return "";

            gGroup = gGroup.Trim();

            // Remove a single trailing 'G' if present
            if (gGroup.EndsWith("G", StringComparison.OrdinalIgnoreCase))
                gGroup = gGroup.Substring(0, gGroup.Length - 1);

            return gGroup;
        }

        /// <summary>
        /// Convert allele to g-group using the rule set from your R code.
        /// IMPORTANT:
        ///   - allele matches exactly one g-group -> replace (WITHOUT trailing G)
        ///   - allele matches multiple -> collapse to one-field
        ///   - allele matches none -> collapse to one-field
        /// </summary>
        public string ConvertAllele(string allele)
        {
            if (string.IsNullOrWhiteSpace(allele))
                return "";

            allele = allele.Trim();

            if (_alleleToGGroups.TryGetValue(allele, out var gMatches))
            {
                if (gMatches.Count == 1)
                {
                    // Already normalized at load, but keep it safe:
                    return NormalizeGGroupValue(gMatches[0]);
                }

                // ambiguous mapping -> one-field
                return allele;
            }

            // no mapping -> one-field
            return allele;
        }
    }
}
