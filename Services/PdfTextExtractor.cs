using System;
using System.Linq;
using System.Text;
using UglyToad.PdfPig;

namespace HLAImputation.Services
{
    public sealed class PdfTextExtractor
    {
        public string ExtractNativeText(string pdfPath)
        {
            var text = new StringBuilder();

            using PdfDocument document =
                PdfDocument.Open(pdfPath);

            foreach (var page in document.GetPages())
            {
                text.AppendLine(page.Text);
                text.AppendLine();
            }

            return text.ToString();
        }

        public bool ContainsUsableHlaText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string upper = text.ToUpperInvariant();

            string[] markers =
            {
                "HLA",
                "DRB1",
                "DQB1",
                "DQA1",
                "DPB1",
                "A*",
                "B*",
                "C*"
            };

            int markerCount =
                markers.Count(upper.Contains);

            return text.Count(char.IsLetterOrDigit) >= 25 &&
                   markerCount >= 1;
        }
    }
}