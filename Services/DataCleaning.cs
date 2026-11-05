
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

        // ✅ Reference-driven DRB345 null correction (populated once by MainWindow).
        public ClassIINullDrb345Reference? NullDrb345Reference { get; set; }

        // ✅ Audit: transformed records whose DRB345 was corrected (deduped by TxID).
        public readonly HashSet<string> Drb345CorrectedTxIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ✅ Audit: reason string per corrected TxID (keyed by transformed/variant TxID).
        public readonly Dictionary<string, string> Drb345CorrectionReasons =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            ApplyReferenceBasedDrb345Corrections(copy);

            return copy;
        }

        /// <summary>
        /// Reference-driven DRB345 null correction.
        /// Data are UNPHASED, so we treat DRB345 as an unordered pair:
        ///   1) count how many DRB1 alleles form a known DRBX*NNNN haplotype in the panel,
        ///   2) only ever convert spurious EXPRESSED DRB345 slots to DRBX*NNNN to reach that count.
        /// We never fabricate an expressed DRB3/4/5 allele.
        /// </summary>
        private void ApplyReferenceBasedDrb345Corrections(InputRecord record)
        {
            var reference = NullDrb345Reference;
            if (reference == null || reference.Count == 0)
                return;

            if (!record.Loci.ContainsKey("DRB1")) return;
            if (!record.Loci.ContainsKey("DRB345")) return;

            var drb1 = record.Loci["DRB1"];
            var drb345 = record.Loci["DRB345"];
            if (drb1 == null || drb1.Length < 2) return;
            if (drb345 == null || drb345.Length < 2) return;

            // Available DQ alleles (may be blank; matcher handles that).
            string dqa1a = "", dqa1b = "", dqb1a = "", dqb1b = "";
            if (record.Loci.TryGetValue("DQA1", out var dqa) && dqa != null)
            {
                dqa1a = dqa.Length > 0 ? dqa[0] ?? "" : "";
                dqa1b = dqa.Length > 1 ? dqa[1] ?? "" : "";
            }
            if (record.Loci.TryGetValue("DQB1", out var dqb) && dqb != null)
            {
                dqb1a = dqb.Length > 0 ? dqb[0] ?? "" : "";
                dqb1b = dqb.Length > 1 ? dqb[1] ?? "" : "";
            }

            // 1) How many DRB1 alleles are null-associated?
            int nullCount = 0;
            if (reference.IsDrb1NullAssociated(drb1[0] ?? "", dqa1a, dqa1b, dqb1a, dqb1b)) nullCount++;
            if (reference.IsDrb1NullAssociated(drb1[1] ?? "", dqa1a, dqa1b, dqb1a, dqb1b)) nullCount++;
            if (nullCount == 0) return;

            // 2) Current DRB345 null state (order-independent).
            string d1 = drb345[0] ?? "";
            string d2 = drb345[1] ?? "";
            bool d1Null = ClassIINullDrb345Reference.IsNullDrb345(d1);
            bool d2Null = ClassIINullDrb345Reference.IsNullDrb345(d2);
            int currentNullCount = (d1Null ? 1 : 0) + (d2Null ? 1 : 0);

            // 3) Only ADD nulls; convert expressed slots until counts agree.
            int needToConvert = nullCount - currentNullCount;
            if (needToConvert <= 0) return;

            string new1 = d1;
            string new2 = d2;

            if (needToConvert > 0 && !d1Null) { new1 = "DRBX*NNNN"; needToConvert--; }
            if (needToConvert > 0 && !d2Null) { new2 = "DRBX*NNNN"; needToConvert--; }

            if (!string.Equals(new1, d1, StringComparison.OrdinalIgnoreCase) ||
    !string.Equals(new2, d2, StringComparison.OrdinalIgnoreCase))
            {
                record.Loci["DRB345"] = new[] { new1, new2 };
                string tx = record.TxID ?? "";
                Drb345CorrectedTxIds.Add(tx);
                Drb345CorrectionReasons[tx] =
                    $"Reference DRB345 null correction: [{d1} / {d2}] → [{new1} / {new2}] " +
                    $"(DRB1 null-haplotype count = {nullCount})";
            }
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
