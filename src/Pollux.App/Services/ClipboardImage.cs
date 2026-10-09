using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Pollux.App.Services;

/// <summary>Turns the bitmap on the clipboard into PNG bytes (Clipboard History capture and paste-as-file).</summary>
internal static class ClipboardImage
{
    public static async Task<byte[]> ToPngAsync(DataPackageView view)
    {
        var reference = await view.GetBitmapAsync();
        using var source = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(source);
        // Ignore alpha: clipboard bitmaps (screenshots, Paint) often carry an all-zero alpha channel.
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        using var png = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        var bytes = new byte[png.Size];
        using var reader = new DataReader(png.GetInputStreamAt(0));
        await reader.LoadAsync((uint)png.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
