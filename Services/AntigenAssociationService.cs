using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HLAImputation.Models;

namespace HLAImputation.Services
{
    public sealed class AntigenAssociationService
    {
        private static readonly string[] SupportedLoci = { "A", "B", "C", "DRB1", "DQB1" };
        private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _reverse =
            new(StringComparer.OrdinalIgnoreCase);

        public AntigenAssociationService(string csvPath)
        {
            if (!File.Exists(csvPath)) throw new FileNotFoundException("Antigen conversion table not found.", csvPath);
            using var sr = new StreamReader(csvPath);
            _ = sr.ReadLine();
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var p = line.Replace("\"", "").Split(',');
                if (p.Length < 3) continue;
                string locus = p[0].Trim().TrimEnd('*').ToUpperInvariant();
                string antigen = p[1].Trim();
                string molecular = p[2].Trim();
                if (!SupportedLoci.Contains(locus) || antigen.Length == 0 || antigen == "0" || molecular.Length == 0) continue;
                string family = Family(molecular);
                if (!_reverse.TryGetValue(locus, out var byMol))
                    _reverse[locus] = byMol = new(StringComparer.OrdinalIgnoreCase);
                if (!byMol.TryGetValue(family, out var set))
                    byMol[family] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(antigen);
            }
        }

        public IReadOnlyCollection<string> GetCompatibleAntigens(string locus, string allele)
        {
            if (string.IsNullOrWhiteSpace(allele)) return Array.Empty<string>();
            return _reverse.TryGetValue(locus, out var byMol) && byMol.TryGetValue(Family(allele), out var set)
                ? set : Array.Empty<string>();
        }

        public AntigenAccuracyResult Calculate(InputRecord input, DiplotypeResult output)
        {
            var r = new AntigenAccuracyResult();
            foreach (string locus in SupportedLoci)
            {
                string[] ia = input.Loci.TryGetValue(locus, out var x) ? x : new[] { "", "" };
                string[] oa =
                {
                    output.H1.Alleles.TryGetValue(locus, out var a) ? a : "",
                    output.H2.Alleles.TryGetValue(locus, out var b) ? b : ""
                };
                var ins = ia.Select(v => GetCompatibleAntigens(locus, v).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToArray();
                var outs = oa.Select(v => GetCompatibleAntigens(locus, v).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToArray();
                foreach (var o in outs)
                {
                    r.TotalAllPossible++;
                    if (o.Count > 0 && ins.Any(i => i.Overlaps(o))) r.CorrectAllPossible++;
                }
                foreach (var i in ins.Where(s => s.Count > 0))
                {
                    r.TotalInputObserved++;
                    if (outs.Any(o => o.Overlaps(i))) r.CorrectInputObserved++;
                }
            }
            return r;
        }

        private static string Family(string value)
        {
            value = (value ?? "").Trim().ToUpperInvariant();
            int star = value.IndexOf('*');
            if (star >= 0) value = value[(star + 1)..];
            string first = value.Split(':')[0];
            return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                ? n.ToString(CultureInfo.InvariantCulture) : first;
        }
    }
}

