using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Hakobune.App.Infrastructure;

/// <summary>画面の一部を PNG に保存する（CI で見た目を確かめるため）。</summary>
internal static class Screenshot
{
    public static async Task SaveAsync(UIElement element, string path)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        var dpi = 96d * (element.XamlRoot?.RasterizationScale ?? 1d);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            dpi,
            dpi,
            pixels);
        await encoder.FlushAsync();

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        stream.Seek(0);
        await using var file = File.Create(fullPath);
        await stream.AsStreamForRead().CopyToAsync(file);
    }
}
