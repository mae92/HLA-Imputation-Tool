
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HLAImputation.Models;

namespace HLAImputation.Services
{
    /// <summary>
    /// Normalizes messy allele tokens to standard "LOCUS*field[:field...]" form.
    /// Works when locus is known (preferred): A, B, C, DRB1, DRB345, DQB1, DQA1, DPB1, DPA1.
    /// Examples for locus="A":
    ///  - "A*01:01" -> "A*01:01"
    ///  - "A01:01"  -> "A*01:01"
    ///  - "01:01"   -> "A*01:01"
    ///  - "0101"    -> "A*01:01"
    ///  - "1"       -> "A*01"
    ///  - "A1"      -> "A*01"
    ///  - "A0101"   -> "A*01:01"
    /// </summary>
    public static class AlleleInputNormalizer
    {
        public static InputRecord NormalizeRecord(InputRecord input)
        {
            var copy = new InputRecord
            {
                TxID = input.TxID,
                Race = input.Race,
                PatType = input.PatType,
                Loci = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (var kv in input.Loci)
            {
                string locus = kv.Key;
                string a1 = kv.Value.Length > 0 ? kv.Value[0] ?? "" : "";
                string a2 = kv.Value.Length > 1 ? kv.Value[1] ?? "" : "";


                var n1 = NormalizeToken(locus, a1);
                var n2 = NormalizeToken(locus, a2);

                if (locus.Equals("DRB345", StringComparison.OrdinalIgnoreCase))
                {
                    n1 = NormalizeDRB345Allele(n1);
                    n2 = NormalizeDRB345Allele(n2);
                }

                copy.Loci[locus] = new[] { n1, n2 };

            }

            return copy;
        }


        // ===========================================================
        // ✅ DRB345 NORMALIZATION (Null + Blank handling)
        // ===========================================================
        private static string NormalizeDRB345Allele(string allele)
        {
            if (string.IsNullOrWhiteSpace(allele))
                return "DRBX*NNNN";

            allele = allele.Trim();

            // Null allele handling (ends with N)
            if (allele.EndsWith("N", StringComparison.OrdinalIgnoreCase))
                return "DRBX*NNNN";

            return allele;
        }


        private static string NormalizeDrb345Token(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "";
            token = System.Text.RegularExpressions.Regex.Replace(token.Trim().ToUpperInvariant(), @"\s+", "");

            // Strip trailing G if pasted
            if (token.EndsWith("G", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(0, token.Length - 1);

            // Accept DRB3*, DRB4*, DRB5* (with or without the star in pasted strings)
            string gene;
            if (token.StartsWith("DRB3")) gene = "DRB3";
            else if (token.StartsWith("DRB4")) gene = "DRB4";
            else if (token.StartsWith("DRB5")) gene = "DRB5";
            else
            {
                // If the user pasted just "03:01" etc, we cannot infer DRB3 vs DRB4 vs DRB5 safely.
                // Best behavior: return the original token unchanged (or blank).
                return token;
            }

            // Remove gene prefix and optional star
            string after = token.Substring(gene.Length);
            if (after.StartsWith("*")) after = after.Substring(1);

            // Remove separators
            after = after.Replace("_", "").Replace("-", "");

            if (string.IsNullOrWhiteSpace(after)) return "";

            // If contains ':' normalize padding like other loci
            if (after.Contains(":"))
            {
                var parts = after.Split(':').Where(p => p.Length > 0).ToList();
                if (parts.Count == 0) return "";
                parts[0] = PadFirstField(parts[0]);
                for (int i = 1; i < parts.Count; i++)
                    parts[i] = PadTwoDigit(parts[i]);
                return $"{gene}*{string.Join(":", parts)}";
            }

            // Digits blob behavior (reuse your existing conventions)
            string digits = new string(after.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) return "";
            if (int.TryParse(digits, out int v) && v == 0) return "";

            string formatted;
            if (digits.Length == 1) formatted = PadFirstField(digits);
            else if (digits.Length == 2) formatted = digits;
            else if (digits.Length == 3) formatted = digits;
            else if (digits.Length == 4) formatted = $"{digits.Substring(0, 2)}:{digits.Substring(2, 2)}";
            else if (digits.Length == 5) formatted = $"{digits.Substring(0, 3)}:{digits.Substring(3, 2)}";
            else
            {
                string first = digits.Substring(0, 3);
                string rest = digits.Substring(3);
                var chunks = new List<string>();
                for (int i = 0; i < rest.Length; i += 2)
                    chunks.Add(PadTwoDigit(rest.Substring(i, Math.Min(2, rest.Length - i))));
                formatted = first + ":" + string.Join(":", chunks);
            }

            // Apply padding
            if (formatted.Contains(":"))
            {
                var parts = formatted.Split(':').ToList();
                parts[0] = PadFirstField(parts[0]);
                for (int i = 1; i < parts.Count; i++)
                    parts[i] = PadTwoDigit(parts[i]);
                formatted = string.Join(":", parts);
            }
            else
            {
                formatted = PadFirstField(formatted);
            }

            return $"{gene}*{formatted}";
        }


        public static string NormalizeToken(string locus, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "";

            token = token.Trim();

            if (token.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                return "";

            // Remove whitespace
            token = Regex.Replace(token, @"\s+", "");


            // ✅ Special-case DRB345: preserve DRB3/DRB4/DRB5 gene identity in the allele string
            if (locus.Equals("DRB345", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeDrb345Token(token);
            }


            // Uppercase locus-ish characters
            // (keep digits and ':' as-is)
            token = token.ToUpperInvariant();

            // Strip a trailing "G" if present (user may paste G-group strings)
            if (token.EndsWith("G", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(0, token.Length - 1);

            // Remove locus prefix if present in various forms (A, A*, DRB1, DRB1*, etc.)
            // We will re-add as "LOCUS*"
            string locusUpper = locus.ToUpperInvariant();

            // If token contains '*', keep only what is after '*'
            if (token.Contains("*"))
            {
                token = token.Split('*')[1];
            }
            else
            {
                // Remove explicit locus prefix without '*'
                // Examples: A0101, A01:01, DRB108, DRB1_08, etc.
                if (token.StartsWith(locusUpper))
                {
                    token = token.Substring(locusUpper.Length);
                }
                // Also handle single-letter loci where user types "A01" but locus="A" works above.
                // For safety, also strip leading locus letter if locus is A/B/C and token starts with that letter.
                else if ((locusUpper == "A" || locusUpper == "B" || locusUpper == "C") &&
                         token.Length > 0 && token[0].ToString() == locusUpper)
                {
                    token = token.Substring(1);
                }
            }

            // Remove any leftover separators like "_" or "-" (common paste artifacts)
            token = token.Replace("_", "").Replace("-", "");

            // If token is now empty, return empty
            if (string.IsNullOrWhiteSpace(token)) return "";

            // If token already has ':', pad each field to at least 2 digits (but allow 3-digit first field)
            if (token.Contains(":"))
            {
                var parts = token.Split(':')
                                 .Where(p => p.Length > 0)
                                 .ToList();

                if (parts.Count == 0) return "";

                // First field: if 1 digit => pad2, if 2 digits => keep, if 3 digits => keep
                parts[0] = PadFirstField(parts[0]);

                // Subsequent fields: pad to 2 digits when numeric
                for (int i = 1; i < parts.Count; i++)
                    parts[i] = PadTwoDigit(parts[i]);

                return $"{locusUpper}*{string.Join(":", parts)}";
            }

            // Otherwise token is "digits blob" or digits
            // Keep only leading digits
            string digits = new string(token.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) return "";

            // ✅ Treat 0 or 00 or 000 as missing
            if (int.TryParse(digits, out int antigenVal) && antigenVal == 0)
                return "";



            string formatted;

            // Handle common blobs:
            //  - 1 digit -> one-field (pad2)
            //  - 2 digits -> one-field (keep)
            //  - 4 digits -> 2+2 (01:01)
            //  - 5 digits -> 3+2 (101:01)
            //  - 6 digits -> 3+3 (rare; keep as 3:3)
            if (digits.Length == 1)
            {
                formatted = PadFirstField(digits); // -> 01
            }
            else if (digits.Length == 2)
            {
                formatted = digits; // -> 01, 15, 80
            }
            else if (digits.Length == 3)
            {
                // 3-digit first field exists (e.g., A*101)
                formatted = digits;
            }
            else if (digits.Length == 4)
            {
                formatted = $"{digits.Substring(0, 2)}:{digits.Substring(2, 2)}";
            }
            else if (digits.Length == 5)
            {
                formatted = $"{digits.Substring(0, 3)}:{digits.Substring(3, 2)}";
            }
            else // 6+
            {
                // Best-effort: 3 + remaining grouped as 2-digit chunks where possible
                string first = digits.Substring(0, 3);
                string rest = digits.Substring(3);
                var chunks = new List<string>();
                for (int i = 0; i < rest.Length; i += 2)
                {
                    string chunk = rest.Substring(i, Math.Min(2, rest.Length - i));
                    chunks.Add(PadTwoDigit(chunk));
                }
                formatted = first + ":" + string.Join(":", chunks);
            }

            // If formatted contains ':', ensure padding is applied
            if (formatted.Contains(":"))
            {
                var parts = formatted.Split(':').ToList();
                parts[0] = PadFirstField(parts[0]);
                for (int i = 1; i < parts.Count; i++)
                    parts[i] = PadTwoDigit(parts[i]);

                formatted = string.Join(":", parts);
            }
            else
            {
                formatted = PadFirstField(formatted);
            }

            return $"{locusUpper}*{formatted}";
        }

        private static string PadFirstField(string s)
        {
            // numeric?
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                // first field: pad only if 1 digit
                if (s.Length == 1) return n.ToString("D2", CultureInfo.InvariantCulture);
                return s; // 2 or 3 digits stays
            }
            return s;
        }

        private static string PadTwoDigit(string s)
        {
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                if (s.Length == 1) return n.ToString("D2", CultureInfo.InvariantCulture);
                return s;
            }
            return s;
        }
    }
}
