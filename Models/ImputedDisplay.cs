

using System;

namespace HLAImputation.Models
{
    public class ImputedDisplay
    {
        public string TxID { get; set; }
        public string Race { get; set; }
        public string Type { get; set; }

        // ✅ NEW: Which loci were used as input for this sample (always populated)
        public string GenesUsed { get; set; }

        public string A1 { get; set; }
        public string A2 { get; set; }
        public string B1 { get; set; }
        public string B2 { get; set; }
        public string C1 { get; set; }
        public string C2 { get; set; }
        public string DRB11 { get; set; }
        public string DRB12 { get; set; }
        public string DRB3451 { get; set; }
        public string DRB3452 { get; set; }
        public string DQB11 { get; set; }
        public string DQB12 { get; set; }
        public string DQA11 { get; set; }
        public string DQA12 { get; set; }
        public string DPB11 { get; set; }
        public string DPB12 { get; set; }
        public string DPA11 { get; set; }
        public string DPA12 { get; set; }

        public double FreqH1 { get; set; }
        public double FreqH2 { get; set; }
        public double FreqDip { get; set; }
        public int Mismatch { get; set; }
        public string Selection { get; set; }

        // ✅ existing
        public bool Success { get; set; }
    }
}

