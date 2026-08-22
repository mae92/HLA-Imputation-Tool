
using System;
using System.Collections.Generic;
using HLAImputation.Models;

namespace HLAImputation.Services
{
    /// <summary>
    /// DataCleaning:
    /// Centralizes input transformations so the SAME rules apply to:
    ///  - Displayed transformed input (InputGrid)
    ///  - Data passed into the imputation engine
    ///
    /// Required order:
    ///  0) Normalize leading zeros
    ///  1) Field conversion (Raw / 2-field / 1-field)
    ///  2) Then optional G-Group conversion
    /// </summary>
    public sealed class DataCleaning
    {
        private readonly GGroupConversionService _gGroupService;

        public DataCleaning(GGroupConversionService gGroupService)
        {
            _gGroupService = gGroupService;
        }

        /// <summary>
        /// Transform a single allele with correct ordering
        /// </summary>
        public string TransformAllele(string allele, string resolutionMode, bool convertToGGroup)
        {
            if (string.IsNullOrWhiteSpace(allele))
                return "";

            allele = allele.Trim();

            // ✅ 0) Normalize missing leading zeros FIRST
            allele = AlleleUtils.NormalizeLeadingZeros(allele);

            // ✅ 1) Field conversion
            string afterField = resolutionMode switch
            {
                "OneField" => AlleleUtils.ToOneField(allele),
                "TwoField" => AlleleUtils.ToTwoField(allele),
                _ => allele
            };

            // ✅ 2) G-group conversion
            if (convertToGGroup)
            {
                return _gGroupService.ConvertAllele(afterField);
            }

            return afterField;
        }

        /// <summary>
        /// Transform whole record for imputation
        /// </summary>
        public InputRecord TransformRecord(
            InputRecord input,
            string resolutionMode,
            bool convertToGGroup,
            Dictionary<string, bool> useLocus)
        {
            var copy = new InputRecord
            {
                TxID = input.TxID,
                Race = input.Race,
                PatType = input.PatType,
                Loci = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (var kv in input.Loci)
            {
                string locus = kv.Key;
                string a1 = kv.Value.Length > 0 ? kv.Value[0] : "";
                string a2 = kv.Value.Length > 1 ? kv.Value[1] : "";

                if (useLocus.TryGetValue(locus, out bool use) && !use)
                {
                    copy.Loci[locus] = new[] { "", "" };
                    continue;
                }

                string t1 = TransformAllele(a1, resolutionMode, convertToGGroup);
                string t2 = TransformAllele(a2, resolutionMode, convertToGGroup);


                // ✅ Ensure DRB345 normalization persists through transformation
                if (locus.Equals("DRB345", StringComparison.OrdinalIgnoreCase))
                {
                    t1 = NormalizeDRB345Allele(t1);
                    t2 = NormalizeDRB345Allele(t2);
                }
                copy.Loci[locus] = new[] { t1, t2 };
            }

            ApplyDrb345NullCorrections(copy);

            return copy;
        }

        private static bool IsHomozygousDrb345(
            string allele1,
            string allele2)
        {
            allele1 = allele1?.Trim() ?? "";
            allele2 = allele2?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(allele1) &&
                string.IsNullOrWhiteSpace(allele2))
            {
                return true;
            }

            return string.Equals(
                allele1,
                allele2,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNullAssociatedDrb1(
            string allele)
        {
            if (string.IsNullOrWhiteSpace(allele))
                return false;

            allele = allele.Trim().ToUpperInvariant();

            return allele.StartsWith("DRB1*01") ||
                   allele.StartsWith("DRB1*08") ||
                   allele.StartsWith("DRB1*10");
        }

        private void ApplyDrb345NullCorrections(
            InputRecord record)
        {
            if (!record.Loci.ContainsKey("DRB1"))
                return;

            if (!record.Loci.ContainsKey("DRB345"))
                return;

            string drb1a = record.Loci["DRB1"][0] ?? "";
            string drb1b = record.Loci["DRB1"][1] ?? "";

            string drb345a = record.Loci["DRB345"][0] ?? "";
            string drb345b = record.Loci["DRB345"][1] ?? "";

            //
            // ONLY inspect homozygous DRB345 samples
            //
            if (!IsHomozygousDrb345(drb345a, drb345b))
                return;

            //
            // Apply mapping from DRB1
            //
            if (IsNullAssociatedDrb1(drb1a))
                drb345a = "DRBX*NNNN";

            if (IsNullAssociatedDrb1(drb1b))
                drb345b = "DRBX*NNNN";

            record.Loci["DRB345"] = new[]
            {
        drb345a,
        drb345b
    };
        }



        private string NormalizeDRB345Allele(string allele)
        {
            if (string.IsNullOrWhiteSpace(allele))
                return "DRBX*NNNN";

            allele = allele.Trim();

            if (allele.EndsWith("N", StringComparison.OrdinalIgnoreCase))
                return "DRBX*NNNN";

            return allele;
        }

    }
}
