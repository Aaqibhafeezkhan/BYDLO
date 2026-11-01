using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZXing;
using ZXing.Common;

namespace Bydlo.App.Services;

/// <summary>Decodes QR codes from image files (screenshots, saved QR images). Camera scanning is planned for Android.</summary>
public static class QrDecoder
{
    public static string? DecodeFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var bitmap = new Bitmap(stream);
        return Decode(bitmap);
    }

    public static string? Decode(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        var format = bitmap.Format == PixelFormat.Rgba8888 ? RGBLuminanceSource.BitmapFormat.RGBA32 : RGBLuminanceSource.BitmapFormat.BGRA32;
        var source = new RGBLuminanceSource(buffer, size.Width, size.Height, format);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] },
        };
        return reader.Decode(source)?.Text;
    }
}
