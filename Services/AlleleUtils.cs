
using System;

namespace HLAImputation.Services
{
    public static class AlleleUtils
    {
        public static bool IsMissing(string allele)
            => string.IsNullOrWhiteSpace(allele) || allele.Trim().Equals("NULL", StringComparison.OrdinalIgnoreCase);

        // Keep original formatting for prefix match
        public static string Prefix(string allele)
        {
            if (string.IsNullOrWhiteSpace(allele)) return "";
            return allele.Trim();
        }

        // Convert A*01:01:01 -> A*01:01 ; A*01 -> A*01
        public static string ToTwoField(string allele)
        {
            if (string.IsNullOrWhiteSpace(allele)) return "";
            allele = allele.Trim();

            int firstColon = allele.IndexOf(':');
            if (firstColon < 0) return allele;

            int secondColon = allele.IndexOf(':', firstColon + 1);
            if (secondColon < 0) return allele;

            return allele.Substring(0, secondColon);
        }

        // Convert A*01:01:01 -> A*01 ; A*01:01 -> A*01 ; A*01 -> A*01
        public static string ToOneField(string allele)
        {
            if (string.IsNullOrWhiteSpace(allele)) return "";
            allele = allele.Trim();

            int firstColon = allele.IndexOf(':');
            return firstColon < 0 ? allele : allele.Substring(0, firstColon);
        }

        /// <summary>
        /// Normalize allele strings with missing leading zeros:
        /// A*3 -> A*03
        /// DRB1*8 -> DRB1*08
        /// DQB1*4:01 -> DQB1*04:01
        /// Does NOT modify already correct values (A*30:02, DPB1*03, etc.)
        /// </summary>
        public static string NormalizeLeadingZeros(string allele)
        {
            if (string.IsNullOrWhiteSpace(allele)) return "";
            allele = allele.Trim();

            int star = allele.IndexOf('*');
            if (star < 0) return allele;

            string left = allele.Substring(0, star + 1);
            string right = allele.Substring(star + 1);

            if (right.Length == 0) return allele;

            int i = 0;
            while (i < right.Length && char.IsDigit(right[i])) i++;

            if (i == 0) return allele;

            string firstToken = right.Substring(0, i);
            string rest = right.Substring(i);

            if (firstToken.Length == 1)
                firstToken = "0" + firstToken;

            return left + firstToken + rest;
        }
    }
}
