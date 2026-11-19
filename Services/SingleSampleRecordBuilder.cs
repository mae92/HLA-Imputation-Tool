using HLAImputation.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HLAImputation.Services
{
    public sealed class SingleSampleRecordBuilder
    {
        public InputRecord Build(
            string sampleId,
            string race,
            string patientType,
            Dictionary<string, string[]> enteredLoci,
            string[] drb3,
            string[] drb4,
            string[] drb5)
        {
            var record = new InputRecord
            {
                TxID = string.IsNullOrWhiteSpace(sampleId)
                    ? $"SINGLE_{DateTime.Now:yyyyMMdd_HHmmss}"
                    : sampleId.Trim(),

                Race = race?.Trim().ToUpperInvariant() ?? "",
                PatType = patientType?.Trim() ?? "",

                Loci = new Dictionary<string, string[]>(
                    StringComparer.OrdinalIgnoreCase),

                CleaningNotes = new List<string>()
            };

            string[] standardLoci =
            {
                "A", "B", "C",
                "DRB1",
                "DQB1", "DQA1",
                "DPB1", "DPA1"
            };

            foreach (string locus in standardLoci)
            {
                enteredLoci.TryGetValue(
                    locus,
                    out string[]? values);

                string value1 =
                    values != null && values.Length > 0
                        ? values[0]
                        : "";

                string value2 =
                    values != null && values.Length > 1
                        ? values[1]
                        : "";

                record.Loci[locus] = new[]
                {
                    AlleleInputNormalizer.NormalizeToken(
                        locus,
                        value1),

                    AlleleInputNormalizer.NormalizeToken(
                        locus,
                        value2)
                };
            }

            record.Loci["DRB345"] =
                BuildDrb345(drb3, drb4, drb5, record.CleaningNotes);

            return record;
        }

        private static string[] BuildDrb345(
            string[] drb3,
            string[] drb4,
            string[] drb5,
            List<string> notes)
        {
            var values = new List<string>();

            AddDrbValues(values, drb3, "DRB3");
            AddDrbValues(values, drb4, "DRB4");
            AddDrbValues(values, drb5, "DRB5");

            values = values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (values.Count == 0)
            {
                return new[]
                {
                    "DRBX*NNNN",
                    "DRBX*NNNN"
                };
            }

            if (values.Count == 1)
            {
                notes.Add(
                    "Single expressed DRB3/4/5 allele entered. " +
                    "Second DRB345 slot provisionally set to DRBX*NNNN.");

                return new[]
                {
                    values[0],
                    "DRBX*NNNN"
                };
            }

            if (values.Count > 2)
            {
                throw new InvalidOperationException(
                    "More than two unique DRB3/4/5 alleles were entered. " +
                    "Please review the typing.");
            }

            return new[]
            {
                values[0],
                values[1]
            };
        }

        private static void AddDrbValues(
            List<string> destination,
            string[] values,
            string gene)
        {
            if (values == null)
                return;

            foreach (string raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string value = raw.Trim();

                if (!value.StartsWith(
                        gene,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = $"{gene}*{value.TrimStart('*')}";
                }

                string normalized =
                    AlleleInputNormalizer.NormalizeToken(
                        "DRB345",
                        value);

                if (!string.IsNullOrWhiteSpace(normalized))
                    destination.Add(normalized);
            }
        }
    }
}