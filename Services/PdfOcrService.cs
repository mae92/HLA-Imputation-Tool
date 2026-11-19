using PDFtoImage;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace HLAImputation.Services
{
    public sealed class PdfOcrService
    {
        public async Task<string> ExtractTextAsync(
            string pdfPath,
            int maximumPages = 6)
        {
            if (!File.Exists(pdfPath))
            {
                throw new FileNotFoundException(
                    "The PDF file was not found.",
                    pdfPath);
            }

            string temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "HLAImputationPdfOcr",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(temporaryDirectory);

            try
            {
                var extractedPages = new List<string>();

                using FileStream pdfStream = File.OpenRead(pdfPath);

                int pageNumber = 0;

                foreach (SKBitmap bitmap in Conversion.ToImages(pdfStream))
                {
                    using (bitmap)
                    {
                        if (pageNumber >= maximumPages)
                            break;

                        string imagePath = Path.Combine(
                            temporaryDirectory,
                            $"page_{pageNumber + 1:D3}.png");

                        using SKData encoded = bitmap.Encode(
                            SKEncodedImageFormat.Png,
                            100);

                        using FileStream output =
                            File.Create(imagePath);

                        encoded.SaveTo(output);

                        using var ocr = new ImageOcrService();

                        string pageText =
                            await ocr.ExtractTextAsync(imagePath);

                        extractedPages.Add(
                            $"===== PDF PAGE {pageNumber + 1} =====" +
                            Environment.NewLine +
                            pageText);

                        pageNumber++;
                    }
                }

                return string.Join(
                    Environment.NewLine + Environment.NewLine,
                    extractedPages);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(temporaryDirectory))
                    {
                        Directory.Delete(
                            temporaryDirectory,
                            recursive: true);
                    }
                }
                catch
                {
                    // Temporary cleanup failure should not cancel import.
                }
            }
        }
    }
}