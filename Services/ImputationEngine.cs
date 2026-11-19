
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

        public string IfNoEthnicityListedUse { get; set; } = "FiveEthnicityAverage";
        public string IfNoHapsForEthnicityUse { get; set; } = "HighestOfAnyEthnicity";

        public ImputationEngine(ReferenceStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // Ethnicity Handling Strategy:
        // Ethnicity provided: CAU → FiveEthnicityAverage → HighestOfAnyEthnicity ✅
        // No ethnicity: FiveEthnicityAverage → HighestOfAnyEthnicity ✅

        // Compatibility wrapper.
        //
        // Existing callers that only need the selected rank-1 diplotype
        // can continue calling ProcessSingle().
        public DiplotypeResult? ProcessSingle(
            InputRecord input,
            out string failureReason,
            out string ethnicityStrategyUsed)
        {
            return ProcessCandidateSet(
                input,
                out failureReason,
                out ethnicityStrategyUsed)
                .Best;
        }


        // Main imputation method.
        //
        // This returns:
        //   1. The selected rank-1 diplotype
        //   2. The Top 15 diplotypes from the winning ethnicity tier
        //   3. All reference-supported phase arrangements of the
        //      selected unphased genotype
        public ImputationCandidateSet ProcessCandidateSet(
            InputRecord input,
            out string failureReason,
            out string ethnicityStrategyUsed)
        {
            failureReason = "";
            ethnicityStrategyUsed = "";

            var transformed =
                TransformInputForImputation(input);

            bool hasEthnicity =
                !string.IsNullOrWhiteSpace(input.Ethnicity);


            // Build the same ordered strategy chain that the
            // application already uses.
            //
            // Ethnicity provided:
            // ethnicity-specific -> five-ethnicity average
            // -> highest of any ethnicity
            //
            // No ethnicity:
            // five-ethnicity average -> highest of any ethnicity

            var attempts =
                new List<(string Column, string Label)>();

            if (hasEthnicity)
            {
                string ethnicityColumn =
                    input.Ethnicity
                        .Trim()
                        .ToUpperInvariant();

                attempts.Add(
                    (
                        ethnicityColumn,
                        "Ethnicity-specific (" +
                        ethnicityColumn +
                        ")"
                    ));

                attempts.Add(
                    (
                        "FiveEthnicityAverage",
                        "Five-ethnicity average " +
                        "(fallback after ethnicity)"
                    ));

                attempts.Add(
                    (
                        "HighestOfAnyEthnicity",
                        "Highest of any ethnicity " +
                        "(fallback after five-ethnicity average)"
                    ));
            }
            else
            {
                attempts.Add(
                    (
                        "FiveEthnicityAverage",
                        "Five-ethnicity average " +
                        "(no ethnicity listed)"
                    ));

                attempts.Add(
                    (
                        "HighestOfAnyEthnicity",
                        "Highest of any ethnicity " +
                        "(fallback after five-ethnicity average)"
                    ));
            }


            string lastFailureReason =
                "Imputation failed.";


            // Each frequency strategy is attempted separately.
            //
            // As soon as one tier produces an acceptable diplotype,
            // that tier becomes the winning tier. The Top 15 and
            // unphased frequency are calculated only from that tier.
            foreach (var attempt in attempts)
            {
                ethnicityStrategyUsed =
                    attempt.Label;


                var candidates =
                    _store.QueryTopHaplotypesForColumn(
                        transformed,
                        orderedLoci: SearchOrder,
                        useLocus: UseLocus,
                        topN: MaxHaplotypes,
                        freqCol: attempt.Column);


                if (candidates == null ||
                    candidates.Count == 0)
                {
                    lastFailureReason =
                        "No candidate haplotypes were found using " +
                        attempt.Column +
                        ".";

                    // Try the next ethnicity-frequency tier.
                    continue;
                }


                // The Top-15 pruning logic assumes descending
                // haplotype-frequency order.
                candidates =
                    candidates
                        .OrderByDescending(
                            h => h.Frequency)
                        .ToList();


                bool singleHaplotypeCandidate =
                    candidates.Count == 1;


                var top15 =
                    FindTopDiplotypesPruned(
                        transformed,
                        candidates,
                        15);


                if (top15.Count == 0)
                {
                    lastFailureReason =
                        MustMatchInput
                            ? "No diplotype fully matched the input " +
                              "at the one-field level using " +
                              attempt.Column +
                              " (Must Match Input / strict mode)."
                            : "No acceptable diplotype could be formed " +
                              "using " +
                              attempt.Column +
                              ".";

                    // Candidates existed, but they could not produce
                    // an acceptable diplotype. Try the next tier.
                    continue;
                }


                // FindTopDiplotypesPruned returns records in descending
                // diplotype-frequency order.
                var best =
                    top15[0];


                best.FinalSelection =
                    singleHaplotypeCandidate
                        ? "single haplotype homozygous imputation"
                        : "highest frequency";


                best.EthnicityStrategyUsed =
                    ethnicityStrategyUsed;

                best.FailureReason =
                    "";

                best.IsSingleHaplotypeImputation =
                    singleHaplotypeCandidate;


                // Enumerate only phase arrangements for the selected
                // genotype. Do not enumerate every possible pair
                // among all candidate haplotypes.
                var compatiblePhases =
                    FindCompatiblePhasesForSelectedGenotype(
                        best,
                        attempt.Column);


                return new ImputationCandidateSet
                {
                    Best = best,

                    Top15 = top15,

                    CompatiblePhasedAlternatives =
                        compatiblePhases
                };
            }


            // Every ethnicity-frequency tier failed.
            failureReason =
                lastFailureReason;

            return new ImputationCandidateSet();
        }


        private InputRecord TransformInputForImputation(InputRecord input)
        {
            // Create a deep-ish copy (so UI can still show raw values if desired)
            var copy = new InputRecord
            {
                TxID = input.TxID,
                Ethnicity = input.Ethnicity,
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


        private List<DiplotypeResult> FindTopDiplotypesPruned(
    InputRecord input,
    List<Haplotype> candidates,
    int keep)
        {
            // This list never grows beyond "keep" records.
            //
            // For this project, keep = 15.
            var top =
                new List<DiplotypeResult>(keep);


            // In allow-mismatch mode, diplotypes are selected first
            // by the lowest mismatch count and then by frequency.
            int bestMismatch =
                int.MaxValue;


            int candidateCount =
                candidates.Count;


            for (int i = 0; i < candidateCount; i++)
            {
                var h1 =
                    candidates[i];


                // Because candidates are sorted by descending frequency,
                // h1 x h1 is the largest possible product remaining
                // in this outer-loop row.
                double rowMaximum =
                    h1.Frequency *
                    h1.Frequency;


                double threshold =
                    top.Count == keep
                        ? top[^1].FreqDip
                        : -1.0;


                // Once strict mode has 15 records, no later row can
                // enter the Top 15 if even its maximum product is
                // below the current rank-15 product.
                //
                // The same is safe in allow-mismatch mode after a
                // zero-mismatch solution has been reached.
                if (top.Count == keep &&
                    (MustMatchInput || bestMismatch == 0) &&
                    rowMaximum < threshold)
                {
                    break;
                }


                for (int j = i; j < candidateCount; j++)
                {
                    var h2 =
                        candidates[j];


                    double product =
                        h1.Frequency *
                        h2.Frequency;


                    threshold =
                        top.Count == keep
                            ? top[^1].FreqDip
                            : -1.0;


                    // Later h2 records have equal or lower frequency.
                    // If this product is already below rank 15,
                    // the remainder of this inner loop cannot enter
                    // the Top 15.
                    if (top.Count == keep &&
                        (MustMatchInput || bestMismatch == 0) &&
                        product < threshold)
                    {
                        break;
                    }


                    int mismatchCutoff =
                        MustMatchInput
                            ? 0
                            : bestMismatch;


                    int mismatch =
                        CalculateMismatchOneField(
                            input,
                            h1,
                            h2,
                            mismatchCutoff);


                    // Strict mode accepts only zero-mismatch pairs.
                    if (MustMatchInput &&
                        mismatch != 0)
                    {
                        continue;
                    }


                    // In allow-mismatch mode, finding a lower mismatch
                    // tier invalidates all records retained from the
                    // previous higher-mismatch tier.
                    if (!MustMatchInput &&
                        mismatch < bestMismatch)
                    {
                        bestMismatch =
                            mismatch;

                        top.Clear();
                    }


                    // Do not retain records outside the selected
                    // minimum-mismatch tier.
                    if (!MustMatchInput &&
                        mismatch != bestMismatch)
                    {
                        continue;
                    }


                    var result =
                        new DiplotypeResult
                        {
                            TxID =
                                input.TxID,

                            Ethnicity =
                                input.Ethnicity,

                            PatType =
                                input.PatType,

                            H1 =
                                h1,

                            H2 =
                                h2,

                            MismatchCount =
                                mismatch,

                            NumberInputAlleles =
                                CountInputAlleles(input)
                        };


                    InsertTop(
                        top,
                        result,
                        keep);
                }
            }


            return top;
        }


        private static void InsertTop(
            List<DiplotypeResult> top,
            DiplotypeResult candidate,
            int keep)
        {
            // Find the first existing record whose frequency is below
            // the new candidate's frequency.
            int insertionIndex =
                top.FindIndex(
                    existing =>
                        candidate.FreqDip >
                        existing.FreqDip);


            if (insertionIndex < 0)
            {
                // Candidate belongs at the end.
                top.Add(candidate);
            }
            else
            {
                top.Insert(
                    insertionIndex,
                    candidate);
            }


            // Retain only the requested number of records.
            if (top.Count > keep)
            {
                top.RemoveAt(
                    top.Count - 1);
            }
        }


        private List<PhasedAlternativeResult>
    FindCompatiblePhasesForSelectedGenotype(
        DiplotypeResult selected,
        string frequencyColumn)
        {
            // Only loci enabled in the current imputation run
            // contribute to the phase-independent genotype.
            var activeLoci =
                SearchOrder
                    .Where(
                        locus =>
                            UseLocus.TryGetValue(
                                locus,
                                out bool enabled)
                            &&
                            enabled)
                    .ToList();


            // Obtain the two selected alleles at every active locus.
            var allelePairs =
                activeLoci
                    .Select(
                        locus =>
                            new
                            {
                                Locus =
                                    locus,

                                A =
                                    selected.H1.Alleles.TryGetValue(
                                        locus,
                                        out var allele1)
                                        ? allele1
                                        : "",

                                B =
                                    selected.H2.Alleles.TryGetValue(
                                        locus,
                                        out var allele2)
                                        ? allele2
                                        : ""
                            })
                    .ToList();


            int heterozygousLocusCount =
                allelePairs.Count(
                    pair =>
                        !pair.A.Equals(
                            pair.B,
                            StringComparison.OrdinalIgnoreCase));


            // Swapping every locus simultaneously only swaps the
            // complete H1 and H2 labels. That is not a distinct phase.
            //
            // Fixing the first heterozygous locus removes this duplicate.
            int freeBits =
                Math.Max(
                    0,
                    heterozygousLocusCount - 1);


            // Examples:
            // 0 or 1 heterozygous loci -> 1 phase
            // 2 heterozygous loci      -> 2 phases
            // 5 heterozygous loci      -> 16 phases
            // 9 heterozygous loci      -> 256 phases
            long possibleCombinations =
                1L << freeBits;


            var results =
                new List<PhasedAlternativeResult>();


            // Defensive duplicate protection.
            var seenPairs =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);


            string unphasedGenotypeKey =
                BuildUnphasedKey(
                    selected,
                    activeLoci);


            string selectedPairKey =
                CanonicalPairKey(
                    selected,
                    activeLoci);


            for (
                long mask = 0;
                mask < possibleCombinations;
                mask++)
            {
                var haplotype1 =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);

                var haplotype2 =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);


                int variableBitIndex =
                    0;

                bool firstHeterozygousLocusFixed =
                    false;


                foreach (var pair in allelePairs)
                {
                    bool swapAlleles =
                        false;


                    if (!pair.A.Equals(
                            pair.B,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (!firstHeterozygousLocusFixed)
                        {
                            // Fix the first heterozygous locus to
                            // remove whole-haplotype H1/H2 swaps.
                            firstHeterozygousLocusFixed =
                                true;
                        }
                        else
                        {
                            swapAlleles =
                                (
                                    (
                                        mask >>
                                        variableBitIndex
                                    )
                                    &
                                    1L
                                )
                                ==
                                1L;

                            variableBitIndex++;
                        }
                    }


                    haplotype1[pair.Locus] =
                        swapAlleles
                            ? pair.B
                            : pair.A;


                    haplotype2[pair.Locus] =
                        swapAlleles
                            ? pair.A
                            : pair.B;
                }


                string haplotype1Text =
                    HaplotypeText(
                        haplotype1,
                        activeLoci);


                string haplotype2Text =
                    HaplotypeText(
                        haplotype2,
                        activeLoci);


                // Canonical ordering ensures H1/H2 and H2/H1 are
                // treated as the same complete diplotype.
                string phasePairKey =
                    string.Compare(
                        haplotype1Text,
                        haplotype2Text,
                        StringComparison.OrdinalIgnoreCase)
                    <=
                    0
                        ? haplotype1Text +
                          "||" +
                          haplotype2Text

                        : haplotype2Text +
                          "||" +
                          haplotype1Text;


                if (!seenPairs.Add(phasePairKey))
                {
                    continue;
                }


                // Perform exact reference lookups using the same
                // frequency column that produced the rank-1 result.
                double haplotype1Frequency =
                    _store.GetExactHaplotypeFrequency(
                        haplotype1,
                        activeLoci,
                        frequencyColumn);


                double haplotype2Frequency =
                    _store.GetExactHaplotypeFrequency(
                        haplotype2,
                        activeLoci,
                        frequencyColumn);


                // Both constituent haplotypes must exist in the
                // reference under the winning frequency column.
                if (haplotype1Frequency <= 0 ||
                    haplotype2Frequency <= 0)
                {
                    continue;
                }


                results.Add(
                    new PhasedAlternativeResult
                    {
                        TxID =
                            selected.TxID,

                        UnphasedGenotype =
                            unphasedGenotypeKey,

                        Haplotype1 =
                            haplotype1Text,

                        Haplotype2 =
                            haplotype2Text,

                        Haplotype1Frequency =
                            haplotype1Frequency,

                        Haplotype2Frequency =
                            haplotype2Frequency,

                        // Preserve the current application formula.
                        DiplotypeFrequency =
                            haplotype1Frequency *
                            haplotype2Frequency,

                        IsSelectedDiplotype =
                            phasePairKey.Equals(
                                selectedPairKey,
                                StringComparison.OrdinalIgnoreCase)
                    });
            }


            return results
                .OrderByDescending(
                    phase =>
                        phase.DiplotypeFrequency)
                .ToList();
        }


        private static string BuildUnphasedKey(
            DiplotypeResult diplotype,
            List<string> loci)
        {
            return string.Join(
                "|",
                loci.Select(
                    locus =>
                    {
                        string allele1 =
                            diplotype.H1.Alleles.TryGetValue(
                                locus,
                                out var h1Allele)
                                ? h1Allele
                                : "";


                        string allele2 =
                            diplotype.H2.Alleles.TryGetValue(
                                locus,
                                out var h2Allele)
                                ? h2Allele
                                : "";


                        var sortedAlleles =
                            new[] { allele1, allele2 }
                                .OrderBy(
                                    allele => allele,
                                    StringComparer.OrdinalIgnoreCase);


                        return
                            $"{locus}=" +
                            string.Join(
                                "/",
                                sortedAlleles);
                    }));
        }


        private static string CanonicalPairKey(
            DiplotypeResult diplotype,
            List<string> loci)
        {
            string haplotype1 =
                HaplotypeText(
                    diplotype.H1.Alleles,
                    loci);


            string haplotype2 =
                HaplotypeText(
                    diplotype.H2.Alleles,
                    loci);


            return string.Compare(
                haplotype1,
                haplotype2,
                StringComparison.OrdinalIgnoreCase)
            <=
            0
                ? haplotype1 +
                  "||" +
                  haplotype2

                : haplotype2 +
                  "||" +
                  haplotype1;
        }


        public static string HaplotypeText(
            IReadOnlyDictionary<string, string> alleles,
            IEnumerable<string> loci)
        {
            return string.Join(
                "~",
                loci.Select(
                    locus =>
                        alleles.TryGetValue(
                            locus,
                            out var allele)
                            ? allele
                            : ""));
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
