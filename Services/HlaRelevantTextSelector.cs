using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HLAImputation.Services
{
    public sealed class HlaRelevantTextSelector
    {
        private static readonly string[] HighPriorityMarkers =
        {
            "CONCLUSIVE TYPING RESULT",
            "DNA TYPES",
            "LABORATORY ASSIGNMENT",
            "ASSIGNED ALLELE CODE",
            "ASSIGNED SEROLOGY",
            "HLA TYPES",
            "HLA TYPING REPORT"
        };

        private static readonly string[] ExclusionMarkers =
        {
            "POSSIBLE ALLELE CODE",
            "GENOTYPE AMBIGUITIES",
            "CLOSE REACTION",
            "TRAY ASSAYS",
            "NUMBER OF READS",
            "ANALYSIS SETTINGS"
        };

        public string SelectRelevantText(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return "";
            }

            string normalized =
                Normalize(rawText);

            string[] lines =
                normalized.Split(
                    new[] { "\r\n", "\n", "\r" },
                    StringSplitOptions.RemoveEmptyEntries);

            var selected =
                new List<string>();

            // First collect blocks surrounding important
            // HLA result headings.
            for (int index = 0;
                 index < lines.Length;
                 index++)
            {
                string line =
                    lines[index].Trim();

                string upper =
                    line.ToUpperInvariant();

                bool highPriority =
                    HighPriorityMarkers.Any(
                        marker => upper.Contains(marker));

                if (!highPriority)
                {
                    continue;
                }

                int start =
                    Math.Max(0, index - 2);

                int end =
                    Math.Min(
                        lines.Length - 1,
                        index + 30);

                for (int selectedIndex = start;
                     selectedIndex <= end;
                     selectedIndex++)
                {
                    string selectedLine =
                        lines[selectedIndex].Trim();

                    string selectedUpper =
                        selectedLine.ToUpperInvariant();

                    bool exclusionFound =
                        ExclusionMarkers.Any(
                            marker =>
                                selectedUpper.Contains(marker));

                    if (exclusionFound &&
                        selectedIndex > index)
                    {
                        break;
                    }

                    selected.Add(selectedLine);
                }
            }

            // Also retain any individual line that appears
            // to contain an HLA locus.
            foreach (string line in lines)
            {
                string upper =
                    line.ToUpperInvariant();

                bool excluded =
                    ExclusionMarkers.Any(
                        marker => upper.Contains(marker));

                if (excluded)
                {
                    continue;
                }

                if (ContainsHlaLocus(upper))
                {
                    selected.Add(line.Trim());
                }
            }

            // If no special section was identified,
            // let the parser inspect the complete text.
            if (selected.Count == 0)
            {
                return normalized;
            }

            return string.Join(
                Environment.NewLine,
                selected.Distinct(
                    StringComparer.OrdinalIgnoreCase));
        }

        private static bool ContainsHlaLocus(
            string text)
        {
            return Regex.IsMatch(
                text,
                @"\b(?:HLA[\s\-]*)?" +
                @"(?:DRB1|DRB3|DRB4|DRB5|DRB345|" +
                @"DQA1|DQB1|DPA1|DPB1|A|B|C)\b",
                RegexOptions.IgnoreCase);
        }

        private static string Normalize(
            string text)
        {
            return text
                .Replace('\u2013', '-')
                .Replace('\u2014', '-')
                .Replace('\u2217', '*')
                .Replace('\u00A0', ' ');
        }
    }
}