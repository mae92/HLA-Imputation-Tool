using System;
using System.Collections.Generic;
using System.Linq;

namespace HLAImputation.Models
{
    public sealed class ParsedHlaTyping
    {
        public string SampleId { get; set; } = "";
        public string Race { get; set; } = "";
        public string PatientType { get; set; } = "";

        public Dictionary<string, List<string>> AllelesByLocus { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Warnings { get; set; } = new();

        public string SourceDescription { get; set; } = "";

        public void AddAllele(string locus, string allele)
        {
            if (string.IsNullOrWhiteSpace(locus) ||
                string.IsNullOrWhiteSpace(allele))
            {
                return;
            }

            if (!AllelesByLocus.TryGetValue(locus, out var values))
            {
                values = new List<string>();
                AllelesByLocus[locus] = values;
            }

            if (!values.Contains(allele, StringComparer.OrdinalIgnoreCase))
                values.Add(allele);
        }
    }
}