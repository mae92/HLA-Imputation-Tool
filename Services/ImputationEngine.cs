
using System;
using System.Collections.Generic;
using System.Linq;
using HLAImputation.Models;

namespace HLAImputation.Services
{
    public sealed class ImputationEngine
    {
        private readonly ReferenceStore _store;

        // Settings driven by GUI
        public int MaxHaplotypes { get; set; } = 1000000;
        public bool MustMatchInput { get; set; } = true;

        // Input conversion mode: "Raw", "TwoField", "OneField"
        public string InputResolutionMode { get; set; } = "Raw";

        // Use locus flags
        public Dictionary<string, bool> UseLocus { get; set; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            { "A", true }, { "B", true }, { "C", true },
            { "DRB1", true }, { "DRB345", true },
            { "DQB1", true }, { "DQA1", true },
            { "DPB1", true }, { "DPA1", true }
        };

        // Search order controlled by GUI (default matches your pipeline doc) [1](https://upmchs-my.sharepoint.com/personal/ellisoniima_upmc_edu/_layouts/15/Doc.aspx?sourcedoc=%7B321304C1-EB93-4927-9379-E5184A9B5802%7D&file=Imputation%20Pipeline%20Description%2002062026.docx&action=default&mobileredirect=true&DefaultItemOpen=1)
        public List<string> SearchOrder { get; set; } = new List<string>
        {
            "A","B","DRB1","DQB1","C","DQA1","DRB345","DPB1","DPA1"
        };

        public string IfNoRaceListedUse { get; set; } = "FiveRaceAverage";
        public string IfNoHapsForRaceUse { get; set; } = "HighestOfAnyRace";

        public ImputationEngine(ReferenceStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // Race Handling Strategy:
            // Race provided: CAU → FiveRaceAverage → HighestOfAnyRace ✅
            // No race: FiveRaceAverage → HighestOfAnyRace ✅

        public DiplotypeResult? ProcessSingle(
    InputRecord input,
    out string failureReason,
    out string raceStrategyUsed)
        {
            failureReason = "";
            raceStrategyUsed = "";

            var transformed = TransformInputForImputation(input);
            bool hasRace = !string.IsNullOrWhiteSpace(input.Race);

            // Build the ordered strategy chain
            // Race provided:  RACE -> FiveRaceAverage -> HighestOfAnyRace
            // No race:        FiveRaceAverage -> HighestOfAnyRace
            var attempts = new List<(string Column, string Label)>();

            if (hasRace)
            {
                string raceCol = input.Race.Trim().ToUpperInvariant();
                attempts.Add((raceCol, "Race-specific (" + raceCol + ")"));
                attempts.Add(("FiveRaceAverage", "Five-race average (fallback after race)"));
                attempts.Add(("HighestOfAnyRace", "Highest of any race (fallback after five-race average)"));
            }
            else
            {
                attempts.Add(("FiveRaceAverage", "Five-race average (no race listed)"));
                attempts.Add(("HighestOfAnyRace", "Highest of any race (fallback after five-race average)"));
            }

            string lastFailureReason = "Imputation failed.";

            foreach (var attempt in attempts)
            {
                raceStrategyUsed = attempt.Label;

                var candidates = _store.QueryTopHaplotypesForColumn(
                    transformed,
                    orderedLoci: SearchOrder,
                    useLocus: UseLocus,
                    topN: MaxHaplotypes,
                    freqCol: attempt.Column
                );

                if (candidates == null || candidates.Count == 0)
                {
                    lastFailureReason =
                        "No candidate haplotypes were found using " + attempt.Column + ".";

                    continue; // try next tier
                }

                candidates = candidates.OrderByDescending(h => h.Frequency).ToList();

                bool singleHaplotypeCandidate = candidates.Count == 1;

                var best = FindBestDiplotypePruned(transformed, candidates);

                if (best != null)
                {
                    best.FinalSelection =
                        singleHaplotypeCandidate
                            ? "single haplotype homozygous imputation"
                            : "highest frequency";
                    best.RaceStrategyUsed = raceStrategyUsed;
                    best.FailureReason = "";
                    best.IsSingleHaplotypeImputation = singleHaplotypeCandidate;
                    return best;
                }

                lastFailureReason = MustMatchInput
                    ? "No diplotype fully matched the input at the one-field level using " + attempt.Column + " (Must Match Input / strict mode)."
                    : "No acceptable diplotype could be formed using " + attempt.Column + ".";
                // try next tier
            }

            // All tiers failed
            failureReason = lastFailureReason;
            return null;
        }

        private InputRecord TransformInputForImputation(InputRecord input)
        {
            // Create a deep-ish copy (so UI can still show raw values if desired)
            var copy = new InputRecord
            {
                TxID = input.TxID,
                Race = input.Race,
                PatType = input.PatType,
                Loci = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (var kv in input.Loci)
            {
                var locus = kv.Key;
                var a1 = kv.Value[0];
                var a2 = kv.Value[1];

                // If locus excluded by user, blank it out for imputation (impute as missing)
                if (UseLocus.ContainsKey(locus) && !UseLocus[locus])
                {
                    copy.Loci[locus] = new[] { "", "" };
                    continue;
                }

                // Apply resolution mode
                if (InputResolutionMode == "OneField")
                {
                    a1 = AlleleUtils.ToOneField(a1);
                    a2 = AlleleUtils.ToOneField(a2);
                }
                else if (InputResolutionMode == "TwoField")
                {
                    a1 = AlleleUtils.ToTwoField(a1);
                    a2 = AlleleUtils.ToTwoField(a2);
                }
                // Raw = unchanged

                copy.Loci[locus] = new[] { a1, a2 };
            }

            return copy;
        }


        private DiplotypeResult? FindBestDiplotypePruned(InputRecord input, List<Haplotype> candidates)
        {
            double bestProduct = -1.0;
            int bestMismatch = int.MaxValue;

            Haplotype? bestH1 = null;
            Haplotype? bestH2 = null;

            int n = candidates.Count;

            for (int i = 0; i < n; i++)
            {
                var h1 = candidates[i];
                double fi = h1.Frequency;

                // ✅ Strict mode pruning (existing logic preserved)
                if (MustMatchInput && bestH1 != null && (fi * fi) < bestProduct)
                    break;

                // ✅ Smart allow-mismatch pruning:
                // once we have already found a 0-mismatch solution, only a higher-product 0-mismatch pair can beat it
                if (!MustMatchInput && bestMismatch == 0 && bestH1 != null && (fi * fi) < bestProduct)
                    break;

                for (int j = i; j < n; j++)
                {
                    var h2 = candidates[j];
                    double product = fi * h2.Frequency;

                    // ✅ Strict mode pruning (existing logic preserved)
                    if (MustMatchInput && bestH1 != null && product < bestProduct)
                        break;

                    // ✅ Smart allow-mismatch pruning:
                    // if bestMismatch is already 0, lower product cannot beat the current best 0-mismatch solution
                    if (!MustMatchInput && bestMismatch == 0 && bestH1 != null && product < bestProduct)
                        break;

                    // ✅ NEW:
                    // Stop mismatch counting early if the current pair is already worse than the best mismatch found so far
                    int cutoff = MustMatchInput ? 0 : bestMismatch;
                    int mismatch = CalculateMismatchOneField(input, h1, h2, cutoff);

                    if (MustMatchInput)
                    {
                        if (mismatch != 0) continue;

                        if (product > bestProduct)
                        {
                            bestProduct = product;
                            bestH1 = h1;
                            bestH2 = h2;
                        }
                    }
                    else
                    {
                        if (mismatch < bestMismatch || (mismatch == bestMismatch && product > bestProduct))
                        {
                            bestMismatch = mismatch;
                            bestProduct = product;
                            bestH1 = h1;
                            bestH2 = h2;
                        }
                    }
                }
            }

            if (bestH1 == null || bestH2 == null)
                return null;

            int finalMismatch = CalculateMismatchOneField(input, bestH1, bestH2, int.MaxValue);

            return new DiplotypeResult
            {
                TxID = input.TxID,
                Race = input.Race,
                PatType = input.PatType,
                H1 = bestH1,
                H2 = bestH2,
                MismatchCount = finalMismatch,
                NumberInputAlleles = CountInputAlleles(input)
            };
        }



        private int CalculateMismatchOneField(InputRecord input, Haplotype h1, Haplotype h2, int stopIfExceeds)
        {
            // Match-to-input should only be checked for loci actually used in imputation
            int mismatches = 0;

            foreach (var locus in SearchOrder)
            {
                if (!UseLocus.ContainsKey(locus) || !UseLocus[locus]) continue;
                if (!input.Loci.ContainsKey(locus)) continue;

                var inputAlleles = input.Loci[locus];

                string a1 = h1.Alleles.ContainsKey(locus) ? OneField(h1.Alleles[locus]) : "";
                string a2 = h2.Alleles.ContainsKey(locus) ? OneField(h2.Alleles[locus]) : "";

                foreach (var allele in inputAlleles)
                {
                    if (string.IsNullOrWhiteSpace(allele)) continue;

                    var x = OneField(allele);

                    if (!(x == a1 || x == a2))
                    {
                        mismatches++;

                        // ✅ NEW:
                        // if this pair is already worse than the current best, stop evaluating it
                        if (mismatches > stopIfExceeds)
                            return mismatches;
                    }
                }
            }

            return mismatches;
        }


        private static string OneField(string allele)
        {
            if (string.IsNullOrWhiteSpace(allele)) return "";
            int idx = allele.IndexOf(':');
            return idx >= 0 ? allele.Substring(0, idx) : allele;
        }

        private static int CountInputAlleles(InputRecord input)
        {
            return input.Loci.Values.Sum(arr => arr.Count(a => !string.IsNullOrWhiteSpace(a)));
        }
    }
}
