
using HLAImputation.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HLAImputation.Services
{
    /// <summary>
    /// QCService (G-Group baseline, "total estimate" + order-independent matching):
    /// - Builds baseline from RAW input by converting each raw allele to G-Group (where possible).
    /// - Prints BOTH:
    ///     1) One-field concordance  (force all alleles to one-field for comparison)
    ///     2) Two-field concordance  (force to two-field where present; one-field stays one-field)
    ///
    /// Important behavior (per your request):
    /// - NO subsetting. Two-field section still includes alleles that are only one-field (they remain one-field).
    /// - Concordance matching is ORDER-INDEPENDENT and DUPLICATE-AWARE (handles allele swaps + homozygotes correctly).
    ///
    /// Notes:
    /// - Any trailing 'G' is stripped during normalization.
    /// - Concordance compares BASELINE vs IMPUTED for successful imputations.
    ///
    /// DUPLICATE TxID HANDLING (NEW):
    /// - If rawInput contains duplicate TxID values, QC assigns internal "QC keys" of the form:
    ///     TxID.1, TxID.2, ...
    ///   based on appearance order in rawInput.
    /// - Baseline records are keyed by those QC keys.
    /// - Results are assigned QC keys by consuming the available baseline QC keys per TxID (queue).
    ///   This avoids dictionary collisions and allows QC to run even with duplicate TxIDs.
    /// </summary>
    public class QCService
    {
        public GGroupConversionService? GGroupService { get; set; }

        public string GenerateQCReport(
            List<InputRecord> rawInput,
            List<InputRecord> transformedInput, // kept for compatibility, not used for concordance
            List<ImputedDisplay> results,
            string resolutionMode)              // kept for compatibility; report prints both modes regardless
        {
            if (GGroupService == null)
                return "QC ERROR: GGroupService not set on QCService. Set _qcService.GGroupService = gGroupService in MainWindow constructor.";

            // Build baseline as before (still aligned with rawInput by index for "input modification" section)
            var baseline = BuildGGroupBaseline(rawInput, GGroupService);

            var sb = new StringBuilder();

            int totalSamples = results.Count;
            int successSamples = results.Count(r => r.Success);
            double pctSuccess = totalSamples > 0 ? (double)successSamples / totalSamples * 100 : 0;

            sb.AppendLine("=================================");
            sb.AppendLine("IMPUTATION SUCCESS RATE");
            sb.AppendLine("=================================");
            sb.AppendLine($"Successful: {successSamples}/{totalSamples} ({pctSuccess:F2}%)\n");


            sb.AppendLine("=================================");
            sb.AppendLine("ONE-FIELD CONCORDANCE (vs RAW→G-GROUP baseline)");
            sb.AppendLine("=================================\n");
            sb.AppendLine(GenerateReportForMode(rawInput, baseline, results, "OneField"));
            sb.AppendLine();

            // ✅ NEW SECTION — G-GROUP CONCORDANCE
            sb.AppendLine("=================================");
            sb.AppendLine("G-GROUP CONCORDANCE (BASELINE vs IMPUTED)");
            sb.AppendLine("=================================\n");
            sb.AppendLine(GenerateGGroupReport(rawInput, baseline, results));
            sb.AppendLine();

            sb.AppendLine("=================================");
            sb.AppendLine("STRICT TWO-FIELD CONCORDANCE (vs RAW→G-GROUP baseline)");
            sb.AppendLine("=================================");
            sb.AppendLine("NOTE: Two-field concordance includes alleles that are only one-field in the baseline.");
            sb.AppendLine("      Those one-field alleles remain one-field and are compared as-is in the two-field section.\n");
            sb.AppendLine(GenerateReportForMode(rawInput, baseline, results, "TwoField"));


            return sb.ToString();
        }

        private string GenerateReportForMode(
            List<InputRecord> rawInput,
            List<InputRecord> baselineGGroup,
            List<ImputedDisplay> results,
            string mode)
        {
            var sb = new StringBuilder();

            // =========================
            // 1) INPUT MODIFICATION (RAW → BASELINE)
            // (still position-based; this is "how many cells changed" rather than genotype-set logic)
            // =========================
            int totalAlleles = 0;
            int modifiedAlleles = 0;


            string NormalizeDRB345(string allele)
            {
                if (string.IsNullOrWhiteSpace(allele)) return "DRBX*NNNN";
                if (allele.EndsWith("N", StringComparison.OrdinalIgnoreCase)) return "DRBX*NNNN";
                return allele;
            }

            for (int i = 0; i < rawInput.Count; i++)
            {
                var raw = rawInput[i];
                var baseRec = baselineGGroup[i];

                foreach (var locus in raw.Loci.Keys)
                {
                    if (!raw.Loci.ContainsKey(locus) || !baseRec.Loci.ContainsKey(locus))
                        continue;

                    for (int j = 0; j < 2; j++)
                    {
                        string r = raw.Loci[locus][j] ?? "";
                        string b = baseRec.Loci[locus][j] ?? "";

                        if (string.IsNullOrWhiteSpace(r)) continue;

                        totalAlleles++;

                        string rNorm = Normalize(r, mode);
                        string bNorm = Normalize(b, mode);

                        if (!rNorm.Equals(bNorm))
                            modifiedAlleles++;
                    }
                }
            }

            double modPct = totalAlleles > 0 ? (double)modifiedAlleles / totalAlleles * 100 : 0;

            sb.AppendLine("INPUT MODIFICATION (RAW → G-GROUP BASELINE)");
            sb.AppendLine($"Modified: {modifiedAlleles}/{totalAlleles} ({modPct:F2}%)\n");

            // =========================
            // 2) ALLELE LEVEL CONCORDANCE (BASELINE vs IMPUTED)
            // ORDER-INDEPENDENT + DUPLICATE-AWARE
            //
            // ✅ FIXED: MATCH BASELINE TO RESULTS USING DUPLICATE-SAFE QC KEYS
            // =========================
            sb.AppendLine("ALLELE LEVEL CONCORDANCE (BASELINE vs IMPUTED)");

            string[] loci = { "A", "B", "C", "DRB1", "DRB345", "DQB1", "DQA1", "DPB1", "DPA1" };

            // Build duplicate-safe alignment structures
            var alignment = BuildDuplicateSafeAlignment(rawInput, baselineGGroup, results);
            var baselineByQcKey = alignment.BaselineByQcKey;
            var qcKeyByResultIndex = alignment.QcKeyByResultIndex;

            foreach (var locus in loci)
            {
                int correct = 0;
                int total = 0;

                for (int i = 0; i < results.Count; i++)
                {
                    var res = results[i];
                    if (!res.Success) continue;

                    string qcKey = qcKeyByResultIndex[i];
                    if (string.IsNullOrWhiteSpace(qcKey)) continue;

                    if (!baselineByQcKey.TryGetValue(qcKey, out var baseRec))
                        continue;

                    if (!baseRec.Loci.ContainsKey(locus)) continue;

                    var inValsRaw = baseRec.Loci[locus];
                    var outValsRaw = GetResultAlleles(res, locus);

                    // Normalize both alleles using the requested mode

                    var inVals = new[]
                    {
                        NormalizeDRB345(Normalize(inValsRaw[0], mode)),
                        NormalizeDRB345(Normalize(inValsRaw[1], mode))
                    };

                    var outVals = new[]
                    {
                        NormalizeDRB345(Normalize(outValsRaw[0], mode)),
                        NormalizeDRB345(Normalize(outValsRaw[1], mode))
                    };


                    // Count matches ignoring order (and handling homozygotes correctly)
                    int matches = CountUnorderedMatches(inVals, outVals);
                    int denom = CountNonEmpty(inVals);

                    correct += matches;
                    total += denom;
                }

                double pct = total > 0 ? (double)correct / total * 100 : 0;
                sb.AppendLine($"{locus}: {correct}/{total} ({pct:F2}%)");
            }

            sb.AppendLine();

            // =========================
            // 3) SAMPLE LEVEL CONCORDANCE (BASELINE vs IMPUTED)
            // A sample "matches" if ALL non-empty baseline alleles are found in output (order-independent + duplicate-aware)
            //
            // ✅ FIXED: MATCH BASELINE TO RESULTS USING DUPLICATE-SAFE QC KEYS
            // =========================

            sb.AppendLine("SAMPLE LEVEL CONCORDANCE (BASELINE vs IMPUTED)");

            // ✅ ORIGINAL SETS
            AddSampleMetric(sb, results, baselineGGroup, mode,
                new[] { "A", "B", "DRB1", "DQB1" },
                "A,B,DRB1,DQB1",
                baselineByQcKey,
                qcKeyByResultIndex);

            AddSampleMetric(sb, results, baselineGGroup, mode,
                new[] { "A", "B", "C", "DRB1", "DQB1" },
                "A,B,C,DRB1,DQB1",
                baselineByQcKey,
                qcKeyByResultIndex);

            AddSampleMetric(sb, results, baselineGGroup, mode,
                new[] { "A", "B", "C", "DRB1", "DRB345", "DQB1", "DQA1" },
                "A,B,C,DRB1,DRB345,DQB1,DQA1",
                baselineByQcKey,
                qcKeyByResultIndex);

            AddSampleMetric(sb, results, baselineGGroup, mode,
                new[] { "A", "B", "C", "DRB1", "DRB345", "DQB1", "DQA1", "DPB1", "DPA1" },
                "A,B,C,DRB1,DRB345,DQB1,DQA1,DPB1,DPA1",
                baselineByQcKey,
                qcKeyByResultIndex);

            // ✅ NEW SETS (ADDED)

            // 5) A, B, C
            AddSampleMetric(sb, results, baselineGGroup, mode,
                new[] { "A", "B", "C" },
                "A,B,C",
                baselineByQcKey,
                qcKeyByResultIndex);

            // 6) DRB1, DRB345, DQA1, DQB1
            AddSampleMetric(sb, results, baselineGGroup, mode,
                new[] { "DRB1", "DRB345", "DQA1", "DQB1" },
                "DRB1,DRB345,DQA1,DQB1",
                baselineByQcKey,
                qcKeyByResultIndex);

            // 7) DRB1, DRB345
            AddSampleMetric(sb, results, baselineGGroup, mode,
                new[] { "DRB1", "DRB345" },
                "DRB1,DRB345",
                baselineByQcKey,
                qcKeyByResultIndex);

            // 8) DQA1, DQB1
            AddSampleMetric(sb, results, baselineGGroup, mode,
                new[] { "DQA1", "DQB1" },
                "DQA1,DQB1",
                baselineByQcKey,
                qcKeyByResultIndex);

            // 9) DPA1, DPB1
            AddSampleMetric(sb, results, baselineGGroup, mode,
                new[] { "DPA1", "DPB1" },
                "DPA1,DPB1",
                baselineByQcKey,
                qcKeyByResultIndex);


            return sb.ToString();
        }

        private List<InputRecord> BuildGGroupBaseline(List<InputRecord> rawInput, GGroupConversionService ggs)
        {
            var list = new List<InputRecord>(rawInput.Count);

            foreach (var raw in rawInput)
            {
                var copy = new InputRecord
                {
                    TxID = raw.TxID,
                    Race = raw.Race,
                    PatType = raw.PatType,
                    Loci = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                };

                foreach (var kv in raw.Loci)
                {
                    string locus = kv.Key;
                    string a1 = kv.Value.Length > 0 ? kv.Value[0] ?? "" : "";
                    string a2 = kv.Value.Length > 1 ? kv.Value[1] ?? "" : "";

                    // Normalize leading zeros first
                    a1 = AlleleUtils.NormalizeLeadingZeros(a1);
                    a2 = AlleleUtils.NormalizeLeadingZeros(a2);

                    // Convert to g-group (ConvertAllele may fall back to one-field)
                    string g1 = string.IsNullOrWhiteSpace(a1) ? "" : ggs.ConvertAllele(a1);
                    string g2 = string.IsNullOrWhiteSpace(a2) ? "" : ggs.ConvertAllele(a2);

                    copy.Loci[locus] = new[] { g1, g2 };
                }

                list.Add(copy);
            }

            return list;
        }


        // ===========================================================
        // DUPLICATE-SAFE OVERLOAD (NEW)
        // ===========================================================

        private void AddSampleMetric(
            StringBuilder sb,
            List<ImputedDisplay> results,
            List<InputRecord> baseline,
            string mode,
            string[] loci,
            string label,
            Dictionary<string, InputRecord> baselineByQcKey,
            List<string> qcKeyByResultIndex)
        {
            string NormalizeDRB345(string allele)
            {
                if (string.IsNullOrWhiteSpace(allele)) return "DRBX*NNNN";
                if (allele.EndsWith("N", StringComparison.OrdinalIgnoreCase)) return "DRBX*NNNN";
                return allele;
            }

            int correct = 0;
            int total = 0;

            for (int i = 0; i < results.Count; i++)
            {
                var res = results[i];
                if (!res.Success) continue;

                string qcKey = qcKeyByResultIndex[i];
                if (string.IsNullOrWhiteSpace(qcKey)) continue;

                if (!baselineByQcKey.TryGetValue(qcKey, out var baseRec))
                    continue;

                total++;

                bool match = true;

                foreach (var locus in loci)
                {
                    if (!baseRec.Loci.ContainsKey(locus)) continue;

                    var inValsRaw = baseRec.Loci[locus];
                    var outValsRaw = GetResultAlleles(res, locus);

                    var inVals = new[]
                    {
                NormalizeDRB345(Normalize(inValsRaw[0], mode)),
                NormalizeDRB345(Normalize(inValsRaw[1], mode))
            };

                    var outVals = new[]
                    {
                NormalizeDRB345(Normalize(outValsRaw[0], mode)),
                NormalizeDRB345(Normalize(outValsRaw[1], mode))
            };

                    if (!AllInputAllelesMatch(inVals, outVals))
                    {
                        match = false;
                        break;
                    }
                }

                if (match) correct++;
            }

            double pct = total > 0 ? (double)correct / total * 100 : 0;
            sb.AppendLine($"{label}: {correct}/{total} ({pct:F2}%)");
        }


        // ===========================================================
        // DUPLICATE-SAFE ALIGNMENT HELPERS (NEW)
        // ===========================================================
        private (Dictionary<string, InputRecord> BaselineByQcKey, List<string> QcKeyByResultIndex)
            BuildDuplicateSafeAlignment(
                List<InputRecord> rawInput,
                List<InputRecord> baselineGGroup,
                List<ImputedDisplay> results)
        {
            // Determine which TxIDs are duplicated in raw input
            var counts = rawInput
                .GroupBy(r => r.TxID ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            // Build per-baseTx queue of QC keys in raw order (TxID or TxID.1 / TxID.2 ...)
            var qcKeyQueues = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
            var baselineByQcKey = new Dictionary<string, InputRecord>(StringComparer.OrdinalIgnoreCase);

            // Build baseline QC keys in raw order using baselineGGroup (same ordering as rawInput)
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < baselineGGroup.Count; i++)
            {
                string baseTx = baselineGGroup[i].TxID ?? "";
                if (!seen.ContainsKey(baseTx)) seen[baseTx] = 0;
                seen[baseTx]++;

                bool isDup = counts.TryGetValue(baseTx, out int c) && c > 1;

                string qcKey = isDup ? $"{baseTx}.{seen[baseTx]}" : baseTx;

                if (!qcKeyQueues.TryGetValue(baseTx, out var q))
                {
                    q = new Queue<string>();
                    qcKeyQueues[baseTx] = q;
                }
                q.Enqueue(qcKey);

                // Create a baseline record copy with the QC key as TxID so we can look it up safely
                var baseRec = baselineGGroup[i];

                var baseCopy = new InputRecord
                {
                    TxID = qcKey,
                    Race = baseRec.Race,
                    PatType = baseRec.PatType,
                    Loci = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                };

                foreach (var kv in baseRec.Loci)
                {
                    string locus = kv.Key;
                    string a1 = kv.Value.Length > 0 ? kv.Value[0] ?? "" : "";
                    string a2 = kv.Value.Length > 1 ? kv.Value[1] ?? "" : "";
                    baseCopy.Loci[locus] = new[] { a1, a2 };
                }

                baselineByQcKey[qcKey] = baseCopy;
            }

            // Assign QC keys to results by consuming from the queues
            var qcKeyByResultIndex = new List<string>(new string[results.Count]);

            for (int i = 0; i < results.Count; i++)
            {
                string baseTx = results[i].TxID ?? "";

                if (qcKeyQueues.TryGetValue(baseTx, out var q) && q.Count > 0)
                {
                    qcKeyByResultIndex[i] = q.Dequeue();
                }
                else
                {
                    // No baseline available for this result TxID (or too many duplicates in results).
                    // Leave blank so caller will skip.
                    qcKeyByResultIndex[i] = "";
                }
            }

            return (baselineByQcKey, qcKeyByResultIndex);
        }


        private int CountUnorderedMatches(string[] input, string[] output)
        {
            var inList = input.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var outList = output.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

            int matches = 0;

            foreach (var a in inList)
            {
                int idx = outList.FindIndex(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    matches++;
                    outList.RemoveAt(idx);
                }
            }

            return matches;
        }



        private bool AllInputAllelesMatch(string[] input, string[] output)
        {
            var inList = input.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var outList = output.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

            foreach (var a in inList)
            {
                int idx = outList.FindIndex(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                    return false;

                outList.RemoveAt(idx);
            }

            return true;
        }


        private int CountNonEmpty(string[] arr)
            => arr.Count(x => !string.IsNullOrWhiteSpace(x));



        // ===========================================================
        // ✅ G-GROUP CONCORDANCE REPORT (NEW)
        // ===========================================================

        private string GenerateGGroupReport(
            List<InputRecord> rawInput,
            List<InputRecord> baselineGGroup,
            List<ImputedDisplay> results)
        {
            if (GGroupService == null)
                return "ERROR: GGroupService not set.";

            var sb = new StringBuilder();

            string[] loci = { "A", "B", "C", "DRB1", "DRB345", "DQB1", "DQA1", "DPB1", "DPA1" };

            var alignment = BuildDuplicateSafeAlignment(rawInput, baselineGGroup, results);
            var baselineByQcKey = alignment.BaselineByQcKey;
            var qcKeyByResultIndex = alignment.QcKeyByResultIndex;

            foreach (var locus in loci)
            {
                int correct = 0;
                int total = 0;

                for (int i = 0; i < results.Count; i++)
                {
                    var res = results[i];
                    if (!res.Success) continue;

                    string qcKey = qcKeyByResultIndex[i];
                    if (string.IsNullOrWhiteSpace(qcKey)) continue;

                    if (!baselineByQcKey.TryGetValue(qcKey, out var baseRec))
                        continue;

                    if (!baseRec.Loci.ContainsKey(locus)) continue;

                    var inValsRaw = baseRec.Loci[locus];
                    var outValsRaw = GetResultAlleles(res, locus);

                    var inVals = new[]
                    {
                string.IsNullOrWhiteSpace(inValsRaw[0]) ? "" : GGroupService.ConvertAllele(Normalize(inValsRaw[0], "TwoField")),
                string.IsNullOrWhiteSpace(inValsRaw[1]) ? "" : GGroupService.ConvertAllele(Normalize(inValsRaw[1], "TwoField"))
            };

                    var outVals = new[]
                    {
                string.IsNullOrWhiteSpace(outValsRaw[0]) ? "" : GGroupService.ConvertAllele(Normalize(outValsRaw[0], "TwoField")),
                string.IsNullOrWhiteSpace(outValsRaw[1]) ? "" : GGroupService.ConvertAllele(Normalize(outValsRaw[1], "TwoField"))
            };

                    int matches = CountUnorderedMatches(inVals, outVals);
                    int denom = CountNonEmpty(inVals);

                    correct += matches;
                    total += denom;
                }

                double pct = total > 0 ? (double)correct / total * 100 : 0;
                sb.AppendLine($"{locus}: {correct}/{total} ({pct:F2}%)");
            }

            sb.AppendLine();
            sb.AppendLine("SAMPLE LEVEL G-GROUP CONCORDANCE (Exact G-Group Match Across All Loci)");

            int sampleCorrect = 0;
            int sampleTotal = 0;

            for (int i = 0; i < results.Count; i++)
            {
                var res = results[i];
                if (!res.Success) continue;

                string qcKey = qcKeyByResultIndex[i];
                if (string.IsNullOrWhiteSpace(qcKey)) continue;

                if (!baselineByQcKey.TryGetValue(qcKey, out var baseRec))
                    continue;

                sampleTotal++;
                bool match = true;

                foreach (var locus in loci)
                {
                    if (!baseRec.Loci.ContainsKey(locus)) continue;

                    var inValsRaw = baseRec.Loci[locus];
                    var outValsRaw = GetResultAlleles(res, locus);

                    var inVals = new[]
                    {
                string.IsNullOrWhiteSpace(inValsRaw[0]) ? "" : GGroupService.ConvertAllele(Normalize(inValsRaw[0], "TwoField")),
                string.IsNullOrWhiteSpace(inValsRaw[1]) ? "" : GGroupService.ConvertAllele(Normalize(inValsRaw[1], "TwoField"))
            };

                    var outVals = new[]
                    {
                string.IsNullOrWhiteSpace(outValsRaw[0]) ? "" : GGroupService.ConvertAllele(Normalize(outValsRaw[0], "TwoField")),
                string.IsNullOrWhiteSpace(outValsRaw[1]) ? "" : GGroupService.ConvertAllele(Normalize(outValsRaw[1], "TwoField"))
            };


                    int matches = CountUnorderedMatches(inVals, outVals);
                    int denom = CountNonEmpty(inVals);

                    if (matches != denom)
                    {
                        match = false;
                        break;
                    }

                }

                if (match) sampleCorrect++;
            }

            double samplePct = sampleTotal > 0 ? (double)sampleCorrect / sampleTotal * 100 : 0;
            sb.AppendLine($"ALL LOCI: {sampleCorrect}/{sampleTotal} ({samplePct:F2}%)");

            return sb.ToString();
        }


        // Strip trailing 'G' so we never compare "...G" vs "..."

        private string Normalize(string allele, string mode)
        {
            if (string.IsNullOrWhiteSpace(allele)) return "";

            allele = allele.Trim();

            // Remove trailing group / expression suffixes used in comparison-sensitive contexts
            // Examples:
            //   DPB1*04:01P -> DPB1*04:01
            //   A*01:01G    -> A*01:01
            if (allele.EndsWith("G", StringComparison.OrdinalIgnoreCase) ||
                allele.EndsWith("P", StringComparison.OrdinalIgnoreCase))
            {
                allele = allele.Substring(0, allele.Length - 1);
            }

            if (mode == "OneField") return AlleleUtils.ToOneField(allele);
            if (mode == "TwoField") return AlleleUtils.ToTwoField(allele);

            return allele;
        }


        private string[] GetResultAlleles(ImputedDisplay r, string locus)
        {
            return locus switch
            {
                "A" => new[] { r.A1, r.A2 },
                "B" => new[] { r.B1, r.B2 },
                "C" => new[] { r.C1, r.C2 },
                "DRB1" => new[] { r.DRB11, r.DRB12 },
                "DRB345" => new[] { r.DRB3451, r.DRB3452 },
                "DQB1" => new[] { r.DQB11, r.DQB12 },
                "DQA1" => new[] { r.DQA11, r.DQA12 },
                "DPB1" => new[] { r.DPB11, r.DPB12 },
                "DPA1" => new[] { r.DPA11, r.DPA12 },
                _ => new[] { "", "" }
            };
        }
    }
}
