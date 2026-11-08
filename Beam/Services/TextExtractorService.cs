using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Beam.Services
{
    /// <summary>
    /// PowerToys Text Extractor equivalent for Beam.
    /// Uses native Windows 10/11 WinRT OCR Engine (zero external dependencies).
    /// </summary>
    public static class TextExtractorService
    {
        public static async Task<string> ExtractTextFromBitmapAsync(Bitmap bitmap)
        {
            if (bitmap == null || bitmap.Width <= 2 || bitmap.Height <= 2)
                return string.Empty;

            try
            {
                // 1. Convert GDI Bitmap to Windows Runtime IRandomAccessStream
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                var randomAccessStream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(ms.ToArray());
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                }

                // 2. Decode SoftwareBitmap
                var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);

                // 3. Run Windows Native OCR Engine
                var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                          ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

                if (engine == null)
                    return string.Empty;

                var result = await engine.RecognizeAsync(softwareBitmap);
                return result?.Text?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                App.Log($"OCR extraction error: {ex}");
                return string.Empty;
            }
        }
    }
}
