using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

internal static class BrandIconExport
{
    // Render every Windows icon size from the same vector used by the application shell.
    public static void Run(string output)
    {
        if (!Application.Current!.TryFindResource("BrandLogo", out var resource) || resource is not IImage logo)
            throw new InvalidOperationException("BrandLogo is missing.");

        var images = new List<(int Size, byte[] Png)>();
        foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })
        {
            using var rendered = new RenderTargetBitmap(new PixelSize(size, size));
            using (var context = rendered.CreateDrawingContext())
                context.DrawImage(logo, new Rect(0, 0, size, size));
            using var png = new MemoryStream();
            rendered.Save(png);
            images.Add((size, png.ToArray()));
            if (size == 256) File.WriteAllBytes(Path.Combine(output, "bydlo-256.png"), png.ToArray());
        }

        using var icon = new BinaryWriter(File.Create(Path.Combine(output, "bydlo.ico")));
        icon.Write((ushort)0);
        icon.Write((ushort)1);
        icon.Write((ushort)images.Count);
        var offset = 6 + images.Count * 16;
        foreach (var (size, png) in images)
        {
            icon.Write((byte)(size == 256 ? 0 : size));
            icon.Write((byte)(size == 256 ? 0 : size));
            icon.Write((byte)0);
            icon.Write((byte)0);
            icon.Write((ushort)1);
            icon.Write((ushort)32);
            icon.Write(png.Length);
            icon.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) icon.Write(png);
        Console.WriteLine("Exported bydlo.ico (9 sizes) and bydlo-256.png from BrandLogo.");
    }
}
