
using System.Collections.Generic;

namespace HLAImputation.Models
{
    public sealed class InputRecord
    {
        public string TxID { get; set; } = "";
        public string Race { get; set; } = "";
        public string PatType { get; set; } = "";

        // Locus -> [allele1, allele2]
        public Dictionary<string, string[]> Loci { get; set; } = new Dictionary<string, string[]>();
    }

    public sealed class Haplotype
    {
        public string RaceUsed { get; set; } = "";  // the column used (e.g., CAU, FiveRaceAverage)
        public double Frequency { get; set; }       // frequency in RaceUsed

        // locus -> allele
        public Dictionary<string, string> Alleles { get; set; } = new Dictionary<string, string>();
    }

    public sealed class DiplotypeResult
    {
        public string TxID { get; set; } = "";
        public string Race { get; set; } = "";
        public string PatType { get; set; } = "";

        public Haplotype H1 { get; set; } = new Haplotype();
        public Haplotype H2 { get; set; } = new Haplotype();

        public double FreqH1 => H1.Frequency;
        public double FreqH2 => H2.Frequency;
        public double FreqDip => FreqH1 * FreqH2;

        public int NumberInputAlleles { get; set; }
        public int MismatchCount { get; set; }

        public string RaceStrategyUsed { get; set; } = "";
        public string FailureReason { get; set; } = "";

        public bool IsSingleHaplotypeImputation { get; set; }
        public string FinalSelection { get; set; } = "";
    }
}

