using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tesseract;
using SkiaSharp;

namespace HLAImputation.Services
{
    public sealed class ImageOcrService : IDisposable
    {
        private readonly TesseractEngine _engine;

        public ImageOcrService()
        {
            string tessdataPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "tessdata");

            if (!Directory.Exists(tessdataPath))
            {
                throw new DirectoryNotFoundException(
                    "Tesseract language-data directory was not found:" +
                    Environment.NewLine +
                    tessdataPath);
            }

            _engine = new TesseractEngine(
                tessdataPath,
                "eng",
                EngineMode.LstmOnly);

            _engine.SetVariable(
                "preserve_interword_spaces",
                "1");

            _engine.SetVariable(
                "user_defined_dpi",
                "300");
        }

        public async Task<string> ExtractTextAsync(string imagePath)
        {
            byte[] bytes = await File.ReadAllBytesAsync(imagePath);

            return await ExtractTextAsync(bytes);
        }

        public Task<string> ExtractTextAsync(byte[] imageBytes)
        {
            return Task.Run(() =>
            {
                var outputs = new List<string>();

                // Pass 1:
                // Let Tesseract automatically detect the page layout.
                outputs.Add(
                    RunOcrPass(
                        imageBytes,
                        PageSegMode.Auto,
                        "Automatic layout"));

                // Pass 2:
                // SparseText often works better for screenshots and tables.
                outputs.Add(
                    RunOcrPass(
                        imageBytes,
                        PageSegMode.SparseText,
                        "Sparse text"));

                // Pass 3:
                // Enlarge and increase contrast, then OCR again.
                byte[] enhancedBytes =
                    EnhanceForHlaOcr(imageBytes);

                outputs.Add(
                    RunOcrPass(
                        enhancedBytes,
                        PageSegMode.SparseText,
                        "Enhanced sparse text"));

                return string.Join(
                    Environment.NewLine +
                    Environment.NewLine,
                    outputs.Where(
                        x => !string.IsNullOrWhiteSpace(x)));
            });
        }

        private string RunOcrPass(
            byte[] imageBytes,
            PageSegMode pageSegMode,
            string passName)
        {
            using Pix image =
                Pix.LoadFromMemory(imageBytes);

            using Page page =
                _engine.Process(
                    image,
                    pageSegMode);

            string text =
                page.GetText() ?? "";

            return
                "===== OCR PASS: " +
                passName +
                " =====" +
                Environment.NewLine +
                text;
        }

        private static byte[] EnhanceForHlaOcr(
            byte[] originalBytes)
        {
            using SKData inputData =
                SKData.CreateCopy(originalBytes);

            using SKCodec codec =
                SKCodec.Create(inputData);

            using var original =
                new SKBitmap(
                    codec.Info.Width,
                    codec.Info.Height);

            codec.GetPixels(
                original.Info,
                original.GetPixels());

            // Enlarge the image to improve recognition
            // of colons, numbers, and small HLA locus labels.
            int targetWidth =
                Math.Max(original.Width * 2, 2000);

            int targetHeight =
                (int)Math.Round(
                    original.Height *
                    (targetWidth /
                     (double)original.Width));

            using var resized =
                new SKBitmap(
                    targetWidth,
                    targetHeight);

            using (var canvas = new SKCanvas(resized))
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;

                paint.ColorFilter =
                    SKColorFilter.CreateColorMatrix(
                        new float[]
                        {
                            1.5f, 0,    0,    0, -40,
                            0,    1.5f, 0,    0, -40,
                            0,    0,    1.5f, 0, -40,
                            0,    0,    0,    1,   0
                        });

                canvas.Clear(SKColors.White);

                canvas.DrawBitmap(
                    original,
                    new SKRect(
                        0,
                        0,
                        targetWidth,
                        targetHeight),
                    paint);
            }

            using SKData encoded =
                resized.Encode(
                    SKEncodedImageFormat.Png,
                    100);

            return encoded.ToArray();
        }

        public void Dispose()
        {
            _engine.Dispose();
        }
    }
}