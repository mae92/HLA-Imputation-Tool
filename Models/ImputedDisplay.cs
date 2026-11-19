using System;

namespace HLAImputation.Models
{
    public class ImputedDisplay
    {
        public string TxID { get; set; } = "";
        public string Ethnicity { get; set; } = "";
        public string Type { get; set; } = "";

        // Loci actually used for this sample.
        public string GenesUsed { get; set; } = "";

        public string A1 { get; set; } = "";
        public string A2 { get; set; } = "";

        public string B1 { get; set; } = "";
        public string B2 { get; set; } = "";

        public string C1 { get; set; } = "";
        public string C2 { get; set; } = "";

        public string DRB11 { get; set; } = "";
        public string DRB12 { get; set; } = "";

        public string DRB3451 { get; set; } = "";
        public string DRB3452 { get; set; } = "";

        public string DQB11 { get; set; } = "";
        public string DQB12 { get; set; } = "";

        public string DQA11 { get; set; } = "";
        public string DQA12 { get; set; } = "";

        public string DPB11 { get; set; } = "";
        public string DPB12 { get; set; } = "";

        public string DPA11 { get; set; } = "";
        public string DPA12 { get; set; } = "";


        // =====================================================
        // Existing phased frequency results
        // =====================================================

        public double FreqH1 { get; set; }

        public double FreqH2 { get; set; }

        public double FreqDip { get; set; }


        // =====================================================
        // Reviewer-requested Top-15 metrics
        // =====================================================

        // Sum of diplotype frequencies for the Top 15
        // eligible diplotypes.
        public double Top15FrequencySum { get; set; }


        // Frequency of rank 1 divided by the Top-15
        // frequency sum, multiplied by 100.
        public double TopDiplotypePercentOfTop15 { get; set; }


        // =====================================================
        // Reviewer-requested unphased genotype metrics
        // =====================================================

        // Sum of frequencies for all reference-supported
        // phase configurations of the selected genotype.
        public double UnphasedGenotypeFrequency { get; set; }


        // Number of reference-supported phased diplotypes
        // contributing to the unphased genotype frequency.
        public int UnphasedCompatibleDiplotypeCount { get; set; }


        // =====================================================
        // Reviewer-requested antigen-association metrics
        // =====================================================

        // Accuracy using all possible output allele positions.
        public int AntigenCorrectAllPossible { get; set; }

        public int AntigenTotalAllPossible { get; set; }

        public double AntigenAccuracyAllPossible { get; set; }


        // Accuracy using only alleles actually supplied
        // in the input.
        public int AntigenCorrectInputObserved { get; set; }

        public int AntigenTotalInputObserved { get; set; }

        public double AntigenAccuracyInputObserved { get; set; }


        // =====================================================
        // Existing imputation diagnostics
        // =====================================================

        public int Mismatch { get; set; }

        public string Selection { get; set; } = "";

        public string EthnicityStrategyUsed { get; set; } = "";

        public string FailureReason { get; set; } = "";

        public bool Success { get; set; }


        // Per-sample data-cleaning summary.
        public string CleaningSummary { get; set; } = "";
    }
}