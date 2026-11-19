using System;
using System.Collections.Generic;
using System.Linq;

namespace HLAImputation.Models
{
    public sealed class InputRecord
    {
        public string TxID { get; set; } = "";
        public string Ethnicity { get; set; } = "";
        public string PatType { get; set; } = "";
        public Dictionary<string, string[]> Loci { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CleaningNotes { get; set; } = new();
    }

    public sealed class Haplotype
    {
        public string EthnicityFrequencyColumnUsed { get; set; } = "";
        public double Frequency { get; set; }
        public Dictionary<string, string> Alleles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class DiplotypeResult
    {
        public string TxID { get; set; } = "";
        public string Ethnicity { get; set; } = "";
        public string PatType { get; set; } = "";
        public Haplotype H1 { get; set; } = new();
        public Haplotype H2 { get; set; } = new();
        public double FreqH1 => H1.Frequency;
        public double FreqH2 => H2.Frequency;
        public double FreqDip => FreqH1 * FreqH2;
        public int NumberInputAlleles { get; set; }
        public int MismatchCount { get; set; }
        public string EthnicityStrategyUsed { get; set; } = "";
        public string FailureReason { get; set; } = "";
        public bool IsSingleHaplotypeImputation { get; set; }
        public string FinalSelection { get; set; } = "";
    }

    public sealed class ImputationCandidateSet
    {
        public DiplotypeResult? Best { get; set; }
        public List<DiplotypeResult> Top15 { get; set; } = new();
        public List<PhasedAlternativeResult> CompatiblePhasedAlternatives { get; set; } = new();
        public double Top15FrequencySum => Top15.Sum(x => x.FreqDip);
        public double TopDiplotypePercentOfTop15 => Best != null && Top15FrequencySum > 0
            ? Best.FreqDip / Top15FrequencySum * 100.0 : 0.0;
        public double UnphasedGenotypeFrequency => CompatiblePhasedAlternatives.Sum(x => x.DiplotypeFrequency);
    }

    public sealed class PhasedAlternativeResult
    {
        public string TxID { get; set; } = "";
        public string UnphasedGenotype { get; set; } = "";
        public string Haplotype1 { get; set; } = "";
        public string Haplotype2 { get; set; } = "";
        public double Haplotype1Frequency { get; set; }
        public double Haplotype2Frequency { get; set; }
        public double DiplotypeFrequency { get; set; }
        public bool IsSelectedDiplotype { get; set; }
    }

    public sealed class TopDiplotypeExportRow
    {
        public string TxID { get; set; } = "";
        public string Ethnicity { get; set; } = "";
        public int Rank { get; set; }
        public string Haplotype1 { get; set; } = "";
        public string Haplotype2 { get; set; } = "";
        public double Haplotype1Frequency { get; set; }
        public double Haplotype2Frequency { get; set; }
        public double DiplotypeFrequency { get; set; }
        public double TopDiplotypePercentOfTop15 { get; set; }
        public int MismatchCount { get; set; }
        public string EthnicityStrategyUsed { get; set; } = "";
    }

    public sealed class AntigenAccuracyResult
    {
        public int CorrectAllPossible { get; set; }
        public int TotalAllPossible { get; set; }
        public double AccuracyAllPossible => TotalAllPossible == 0 ? 0 : CorrectAllPossible * 100.0 / TotalAllPossible;
        public int CorrectInputObserved { get; set; }
        public int TotalInputObserved { get; set; }
        public double AccuracyInputObserved => TotalInputObserved == 0 ? 0 : CorrectInputObserved * 100.0 / TotalInputObserved;
    }
}

