using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace HLAImputation.Services
{
    public static class ClipboardImageHelper
    {
        public static byte[]? GetClipboardImageAsPng()
        {
            if (!Clipboard.ContainsImage())
                return null;

            BitmapSource? bitmap = Clipboard.GetImage();

            if (bitmap == null)
                return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = new MemoryStream();
            encoder.Save(stream);

            return stream.ToArray();
        }
    }
}