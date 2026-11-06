using System.ComponentModel;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// ウインドウの中を、画面に映っているとおりに保存する（ダイアログなど、別の層に出るものも写る）。
    /// ウインドウがほかのウインドウに隠れていないこと。
    /// </summary>
    public static async Task SaveWindowAsync(nint window, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!GetClientRect(window, out var client))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var origin = default(NativePoint);
        if (!ClientToScreen(window, ref origin))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("ウインドウの大きさが 0 です。");

        var pixels = new byte[width * height * 4];
        var screen = GetDC(0);
        if (screen == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        try
        {
            var previous = SelectObject(memory, bitmap);
            var copied = BitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, SourceCopy | CaptureLayered);
            SelectObject(memory, previous); // GetDIBits の前に外しておく決まり
            if (!copied)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height, // 上から下へ
                    Planes = 1,
                    BitCount = 32,
                },
            };
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == 0)
                throw new InvalidOperationException("画面の画素を読めませんでした。");
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }

        // 画面から写した画素は透明度が 0 なので、不透明にする
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var dpi = (double)GetDpiForWindow(window);
        if (dpi <= 0)
            dpi = 96;
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, dpi, dpi, pixels);
        await encoder.FlushAsync();

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        stream.Seek(0);
        await using var file = File.Create(fullPath);
        await stream.AsStreamForRead().CopyToAsync(file);
    }

    private const int SourceCopy = 0x00CC0020;
    private const int CaptureLayered = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    // BITMAPINFO（色の表のぶん、少し余分にとる）
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Color0;
        public int Color1;
        public int Color2;
        public int Color3;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int operation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);
}
