using System.Runtime.InteropServices;
using Pollux.Core.Snap;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.System.UserProfile;

namespace Pollux.App.Services;

/// <summary>The recognised text of a Snap, or why there is none; <see cref="Language"/> is the OCR language used.</summary>
internal sealed record SnapText(string Text, string? Language, string? Problem);

/// <summary>Reads the text in a Snap with Windows' own text recognition (Windows.Media.Ocr, on this PC only).</summary>
internal static class SnapTextRecognition
{
    public static async Task<SnapText> RecognizeAsync(byte[] png, string? setting)
    {
        var installed = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
        var tag = SnapOcrLanguage.Choose(setting, GlobalizationPreferences.Languages, installed);
        if (tag is null)
        {
            return new SnapText("", null, installed.Count == 0
                ? "No text recognition language is installed (Windows Settings › Time & language › Language › Optional features)."
                : $"The text recognition language \"{setting}\" is not installed.");
        }

        var engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
        if (engine is null) return new SnapText("", tag, $"Text recognition for {tag} is not available.");

        try
        {
            using var bitmap = await DecodeAsync(png);
            var result = await engine.RecognizeAsync(bitmap);
            var lines = result.Lines.Select(line =>
            {
                var top = line.Words.Count == 0 ? 0 : line.Words.Min(w => w.BoundingRect.Y);
                var bottom = line.Words.Count == 0 ? 0 : line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                return new SnapOcrLine(line.Text, top, bottom - top);
            });
            var text = SnapOcrText.Join(lines);
            return new SnapText(text, tag, text.Length == 0 ? "No text found." : null);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            CrashLog.Write(ex);
            return new SnapText("", tag, "Text recognition failed.");
        }
    }

    /// <summary>The PNG as a bitmap OCR takes, scaled down when it is larger than OCR's limit.</summary>
    private static async Task<SoftwareBitmap> DecodeAsync(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var max = OcrEngine.MaxImageDimension;
        var scale = Math.Min(1.0, (double)max / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Floor(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Floor(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
    }
}
