using HLAImputation.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HLAImputation.Services
{
    public sealed class HlaTextParser
    {
        private static readonly string[] SupportedGenes =
        {
            "A",
            "B",
            "C",
            "DRB1",
            "DRB3",
            "DRB4",
            "DRB5",
            "DQA1",
            "DQB1",
            "DPA1",
            "DPB1"
        };

        private static readonly Dictionary<string, string> LocusAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "A", "A" },
                { "HLA-A", "A" },

                { "B", "B" },
                { "HLA-B", "B" },

                { "C", "C" },
                { "CW", "C" },
                { "HLA-C", "C" },

                { "DR", "DRB1" },
                { "DRB1", "DRB1" },
                { "HLA-DRB1", "DRB1" },

                { "DRB3", "DRB3" },
                { "DR52", "DRB3" },
                { "DRB4", "DRB4" },
                { "DR53", "DRB4" },
                { "DRB5", "DRB5" },
                { "DR51", "DRB5" },

                { "DQA", "DQA1" },
                { "DQA1", "DQA1" },
                { "HLA-DQA1", "DQA1" },

                { "DQ", "DQB1" },
                { "DQB", "DQB1" },
                { "DQB1", "DQB1" },
                { "HLA-DQB1", "DQB1" },

                { "DPA", "DPA1" },
                { "DPA1", "DPA1" },
                { "HLA-DPA1", "DPA1" },

                { "DP", "DPB1" },
                { "DPB", "DPB1" },
                { "DPB1", "DPB1" },
                { "HLA-DPB1", "DPB1" }
            };

        // Matches allele strings such as:
        //
        // A*02:01
        // A 02:01
        // DRB1*04:01
        // DQB1 03:02
        //
        // The backslash-star sequence below means a literal
        // asterisk character in the regular expression.
        private static readonly Regex FullyQualifiedAlleleRegex =
            new(
                @"(?<![A-Z0-9])" +
                @"(?<gene>DRB1|DRB3|DRB4|DRB5|" +
                @"DQA1|DQB1|DPA1|DPB1|A|B|C)" +
                @"\s*[\*'_\-]?\s*" +
                @"(?<fields>\d{1,3}" +
                @"(?:(?:\s*[:.]\s*)\d{1,3}){0,3})" +
                @"(?<suffix>[NLSCAQMPG]?)\b",
                RegexOptions.IgnoreCase |
                RegexOptions.Compiled);

        // Matches rows such as:
        //
        // A    02:01    03:01
        // B    07:02    44:02
        // DRB1 04:01    15:01
        //
        // This is especially important for HistoTrac screenshots.
        private static readonly Regex LocusRowRegex =
            new(
                @"^\s*(?:HLA[\s\-]*)?" +
                @"(?<locus>DRB1|DRB3|DRB4|DRB5|" +
                @"DRB345|DQA1|DQB1|DPA1|DPB1|" +
                @"DQA|DQB|DPA|DPB|DR|CW|A|B|C)" +
                @"(?:\s*[\*\-:]?\s*)" +
                @"(?<remainder>.*)$",
                RegexOptions.IgnoreCase |
                RegexOptions.Compiled);

        // Matches allele fields that do not include their locus,
        // for example:
        //
        // 02
        // 02:01
        // 04:01P
        //
        // We only use these after we already know which locus
        // row we are examining.
        private static readonly Regex NakedAlleleRegex =
            new(
                @"(?<!\d)" +
                @"(?<value>\d{1,3}" +
                @"(?:(?:\s*[:.]\s*)\d{1,3}){0,3}" +
                @"[NLSCAQMPG]?)" +
                @"(?!\d)",
                RegexOptions.IgnoreCase |
                RegexOptions.Compiled);

        public ParsedHlaTyping Parse(
            string text,
            string sourceDescription = "")
        {
            var result = new ParsedHlaTyping
            {
                SourceDescription = sourceDescription
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                result.Warnings.Add(
                    "No text was available to parse.");

                return result;
            }

            string cleaned = Preprocess(text);

            ParseSampleInformation(
                cleaned,
                result);

            // Look for high-confidence assigned allele sections first.
            ParseAssignedAlleleCodeSections(
                cleaned,
                result);

            // Next look for table rows such as:
            // DRB1 04:01 15:01
            ParseLocusRows(
                cleaned,
                result);

            // Finally look for explicit strings such as:
            // DRB1*04:01
            ParseQualifiedAlleles(
                cleaned,
                result);

            ValidateCounts(result);

            return result;
        }

        private static string Preprocess(
            string text)
        {
            string cleaned = text
                .Replace('\u2013', '-')
                .Replace('\u2014', '-')
                .Replace('\u2217', '*')
                .Replace('\u00A0', ' ');

            // Correct common OCR mistakes involving the number 1
            // versus a capital I in HLA locus names.

            cleaned = Regex.Replace(
                cleaned,
                @"\bDRBI\b",
                "DRB1",
                RegexOptions.IgnoreCase);

            cleaned = Regex.Replace(
                cleaned,
                @"\bDQAI\b",
                "DQA1",
                RegexOptions.IgnoreCase);

            cleaned = Regex.Replace(
                cleaned,
                @"\bDQBI\b",
                "DQB1",
                RegexOptions.IgnoreCase);

            cleaned = Regex.Replace(
                cleaned,
                @"\bDPAI\b",
                "DPA1",
                RegexOptions.IgnoreCase);

            cleaned = Regex.Replace(
                cleaned,
                @"\bDPBI\b",
                "DPB1",
                RegexOptions.IgnoreCase);

            // Collapse repeated spaces/tabs.
            cleaned = Regex.Replace(
                cleaned,
                @"[ \t]+",
                " ");

            // OCR may produce:
            //
            // 02 : 01
            // 02.01
            //
            // Normalize those to:
            //
            // 02:01
            cleaned = Regex.Replace(
                cleaned,
                @"(?<=\d)\s*[:.]\s*(?=\d)",
                ":");

            return cleaned;
        }

        private static void ParseSampleInformation(
            string text,
            ParsedHlaTyping result)
        {
            Match unosMatch = Regex.Match(
                text,
                @"(?im)\bUNOS\s*#?\s*" +
                @"(?<value>[A-Z0-9\-]+)");

            if (unosMatch.Success)
            {
                result.SampleId =
                    unosMatch.Groups["value"]
                        .Value
                        .Trim();

                return;
            }

            Match sampleMatch = Regex.Match(
                text,
                @"(?im)^\s*" +
                @"(?:sample\s*(?:id|number|#)?|" +
                @"patient\s*id|accession)" +
                @"\s*[:#]?\s*" +
                @"(?<value>[A-Za-z0-9._\-]+)");

            if (sampleMatch.Success)
            {
                result.SampleId =
                    sampleMatch.Groups["value"]
                        .Value
                        .Trim();
            }
        }

        private static void ParseAssignedAlleleCodeSections(
            string text,
            ParsedHlaTyping result)
        {
            string[] lines = SplitLines(text);

            for (int index = 0;
                 index < lines.Length;
                 index++)
            {
                if (!lines[index].Contains(
                        "Assigned Allele Code",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string combined = "";

                // Examine up to three lines following
                // "Assigned Allele Code".
                for (int offset = 1;
                     offset <= 3 &&
                     index + offset < lines.Length;
                     offset++)
                {
                    string nextLine =
                        lines[index + offset];

                    if (nextLine.Contains(
                            "Possible Allele Code",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (nextLine.Contains(
                            "Assigned Serology",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    combined +=
                        " " + nextLine;
                }

                AddQualifiedAlleles(
                    combined,
                    result);
            }
        }

        private static void ParseLocusRows(
            string text,
            ParsedHlaTyping result)
        {
            string[] lines = SplitLines(text);

            for (int index = 0;
                 index < lines.Length;
                 index++)
            {
                string line =
                    lines[index].Trim();

                string upper =
                    line.ToUpperInvariant();

                // Skip areas that commonly contain alternative
                // possibilities rather than assigned typing.
                if (upper.Contains("POSSIBLE ALLELE"))
                {
                    continue;
                }

                if (upper.Contains("GENOTYPE AMBIGUIT"))
                {
                    continue;
                }

                if (upper.Contains("CLOSE REACTION"))
                {
                    continue;
                }

                if (upper.Contains("NUMBER OF READS"))
                {
                    continue;
                }

                if (upper.Contains("ANALYSIS SETTINGS"))
                {
                    continue;
                }

                Match row =
                    LocusRowRegex.Match(line);

                if (!row.Success)
                {
                    continue;
                }

                string rawLocus =
                    row.Groups["locus"].Value;

                string remainder =
                    row.Groups["remainder"].Value;

                // DRB345 behaves differently because the
                // actual gene can be DRB3, DRB4, or DRB5.
                if (rawLocus.Equals(
                        "DRB345",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string block =
                        remainder;

                    if (index + 1 < lines.Length)
                    {
                        block +=
                            " " + lines[index + 1];
                    }

                    if (index + 2 < lines.Length)
                    {
                        block +=
                            " " + lines[index + 2];
                    }

                    AddQualifiedAlleles(
                        block,
                        result);

                    continue;
                }

                string locus =
                    NormalizeLocus(rawLocus);

                // Once we already have two values for a locus,
                // do not continue collecting possibilities
                // from later portions of the report.
                if (GetCount(
                        result,
                        locus) >= 2)
                {
                    continue;
                }

                string candidateText =
                    remainder;

                // OCR occasionally puts the locus on one line
                // and its values on the following line.
                if (index + 1 < lines.Length)
                {
                    string nextLine =
                        lines[index + 1];

                    Match nextLocus =
                        LocusRowRegex.Match(
                            nextLine);

                    if (!nextLocus.Success)
                    {
                        candidateText +=
                            " " + nextLine;
                    }
                }

                List<string> candidates =
                    ExtractNakedCandidates(
                        locus,
                        candidateText);

                foreach (string candidate in
                         candidates.Take(2))
                {
                    AddNormalized(
                        result,
                        locus,
                        candidate);
                }
            }
        }

        private static void ParseQualifiedAlleles(
            string text,
            ParsedHlaTyping result)
        {
            string[] lines = SplitLines(text);

            foreach (string line in lines)
            {
                string upper =
                    line.ToUpperInvariant();

                if (upper.Contains("POSSIBLE ALLELE"))
                {
                    continue;
                }

                if (upper.Contains("GENOTYPE AMBIGUIT"))
                {
                    continue;
                }

                if (upper.Contains("CLOSE REACTION"))
                {
                    continue;
                }

                // Some reports use := in descriptions of
                // ambiguity relationships. Skip those lines.
                if (upper.Contains(":="))
                {
                    continue;
                }

                AddQualifiedAlleles(
                    line,
                    result);
            }
        }

        private static void AddQualifiedAlleles(
            string text,
            ParsedHlaTyping result)
        {
            foreach (Match match in
                     FullyQualifiedAlleleRegex.Matches(text))
            {
                string gene =
                    NormalizeLocus(
                        match.Groups["gene"].Value);

                if (GetCount(
                        result,
                        gene) >= 2)
                {
                    continue;
                }

                string fields =
                    Regex.Replace(
                        match.Groups["fields"].Value,
                        @"\s*[:.]\s*",
                        ":");

                string suffix =
                    match.Groups["suffix"]
                        .Value
                        .ToUpperInvariant();

                string allele =
                    gene +
                    "*" +
                    fields +
                    suffix;

                AddNormalized(
                    result,
                    gene,
                    allele);
            }
        }

        private static List<string> ExtractNakedCandidates(
            string locus,
            string text)
        {
            var output =
                new List<string>();

            foreach (Match match in
                     NakedAlleleRegex.Matches(text))
            {
                string value =
                    match.Groups["value"].Value;

                value = Regex.Replace(
                    value,
                    @"\s*[:.]\s*",
                    ":");

                if (!IsPlausibleForLocus(
                        locus,
                        value))
                {
                    continue;
                }

                string fullAllele =
                    locus +
                    "*" +
                    value;

                bool alreadyPresent =
                    output.Any(
                        existing =>
                            existing.Equals(
                                fullAllele,
                                StringComparison.OrdinalIgnoreCase));

                if (!alreadyPresent)
                {
                    output.Add(
                        fullAllele);
                }
            }

            return output;
        }

        private static bool IsPlausibleForLocus(
            string locus,
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string firstField =
                value.Split(':')[0];

            string digits =
                Regex.Replace(
                    firstField,
                    @"\D",
                    "");

            if (!int.TryParse(
                    digits,
                    out int number))
            {
                return false;
            }

            switch (locus)
            {
                case "A":
                    return number >= 1 &&
                           number <= 80;

                case "B":
                    return number >= 7 &&
                           number <= 83;

                case "C":
                    return number >= 1 &&
                           number <= 18;

                case "DRB1":
                    return number >= 1 &&
                           number <= 18;

                case "DRB3":
                    return number >= 1 &&
                           number <= 3;

                case "DRB4":
                    return number >= 1 &&
                           number <= 2;

                case "DRB5":
                    return number >= 1 &&
                           number <= 2;

                case "DQA1":
                    return number >= 1 &&
                           number <= 6;

                case "DQB1":
                    return number >= 2 &&
                           number <= 6;

                case "DPA1":
                    return number >= 1 &&
                           number <= 4;

                case "DPB1":
                    return number >= 1;

                default:
                    return false;
            }
        }

        private static void AddNormalized(
            ParsedHlaTyping result,
            string locus,
            string rawValue)
        {
            string internalLocus =
                GetInternalLocus(locus);

            string normalized =
                AlleleInputNormalizer.NormalizeToken(
                    internalLocus,
                    rawValue);

            if (string.IsNullOrWhiteSpace(
                    normalized))
            {
                return;
            }

            result.AddAllele(
                locus,
                normalized);
        }

        private static string NormalizeLocus(
            string locus)
        {
            string cleaned =
                locus
                    .Trim()
                    .ToUpperInvariant()
                    .Replace(
                        "HLA-",
                        "");

            if (LocusAliases.TryGetValue(
                    cleaned,
                    out string? normalized))
            {
                return normalized;
            }

            return cleaned;
        }

        private static int GetCount(
            ParsedHlaTyping result,
            string locus)
        {
            if (!result.AllelesByLocus.TryGetValue(
                    locus,
                    out List<string>? alleles))
            {
                return 0;
            }

            return alleles.Count;
        }

        private static string[] SplitLines(
            string text)
        {
            return text.Split(
                new[]
                {
                    "\r\n",
                    "\n",
                    "\r"
                },
                StringSplitOptions.RemoveEmptyEntries);
        }

        private static void ValidateCounts(
            ParsedHlaTyping result)
        {
            foreach (string gene in
                     SupportedGenes)
            {
                if (!result.AllelesByLocus.TryGetValue(
                        gene,
                        out List<string>? alleles))
                {
                    continue;
                }

                if (alleles.Count > 2)
                {
                    result.Warnings.Add(
                        gene +
                        ": more than two unique assigned alleles " +
                        "were identified. Review manually.");
                }
            }

            int totalAlleles =
                result.AllelesByLocus
                    .Values
                    .Sum(
                        alleles =>
                            alleles.Count);

            if (totalAlleles == 0)
            {
                result.Warnings.Add(
                    "No HLA typing was confidently extracted. " +
                    "Review the extracted text and enter the typing manually.");
            }

            if (!result.AllelesByLocus.ContainsKey("A"))
            {
                result.Warnings.Add(
                    "HLA-A was not confidently identified.");
            }

            if (!result.AllelesByLocus.ContainsKey("B"))
            {
                result.Warnings.Add(
                    "HLA-B was not confidently identified.");
            }

            if (!result.AllelesByLocus.ContainsKey("DRB1"))
            {
                result.Warnings.Add(
                    "HLA-DRB1 was not confidently identified.");
            }
        }

        private static string GetInternalLocus(
            string gene)
        {
            if (gene == "DRB3" ||
                gene == "DRB4" ||
                gene == "DRB5")
            {
                return "DRB345";
            }

            return gene;
        }
    }
}