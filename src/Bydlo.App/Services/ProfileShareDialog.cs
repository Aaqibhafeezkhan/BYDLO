using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using ZXing;
using ZXing.QrCode;

namespace Bydlo.App.Services;

public static class ProfileShareDialog
{
    public static async Task ShowAsync(AppServices services, Profile profile, string action)
    {
        if (services.TopLevel() is not Window owner) { return; }
        var loc = Loc.Instance;
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = profile.Name, FontSize = 20, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = loc["share.warning"], TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var confirm = new Button { Content = loc[action], HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(confirm);
        var close = new Button { Content = loc["share.close"] };
        panel.Children.Add(close);
        var dialog = new Window
        {
            Title = loc["share.title"], Width = 440, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };
        Bitmap? qr = null;
        close.Click += (_, _) => dialog.Close();
        confirm.Click += async (_, _) =>
        {
            confirm.IsEnabled = false;
            try
            {
                var secrets = await services.Secrets.LoadAsync(profile.Id.ToString());
                if (action == "share.file")
                {
                    var content = ProfileExporter.ExportConfig(profile, secrets);
                    var extension = profile.Protocol == ProtocolKind.OpenVpn ? "ovpn" : "conf";
                    var file = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = loc["share.file"], SuggestedFileName = "server." + extension,
                        DefaultExtension = extension, ShowOverwritePrompt = true,
                        FileTypeChoices = [new FilePickerFileType(extension) { Patterns = ["*." + extension] }],
                    });
                    if (file is null) { return; }
                    await using var stream = await file.OpenWriteAsync();
                    stream.SetLength(0);
                    await using var writer = new StreamWriter(stream);
                    await writer.WriteAsync(content);
                    await writer.FlushAsync();
                    dialog.Close();
                }
                else
                {
                    var link = ProfileExporter.ExportLink(profile, secrets);
                    if (action == "share.copy")
                    {
                        var clipboard = dialog.Clipboard ?? throw new InvalidOperationException();
                        await clipboard.SetTextAsync(link);
                        dialog.Close();
                    }
                    else
                    {
                        qr = CreateQr(link);
                        panel.Children.Insert(2, new Image { Source = qr, Width = 320, Height = 320 });
                        confirm.IsVisible = false;
                    }
                }
            }
            catch (Exception)
            {
                // Do not expose exception messages: encoders / storage providers may include the payload.
                panel.Children.Add(new TextBlock { Text = loc["share.error"], TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            }
            finally { confirm.IsEnabled = true; }
        };
        try { await dialog.ShowDialog(owner); }
        finally { qr?.Dispose(); }
    }

    private static WriteableBitmap CreateQr(string text)
    {
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 640, 640,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "UTF-8", [EncodeHintType.MARGIN] = 4 });
        var bitmap = new WriteableBitmap(new PixelSize(matrix.Width, matrix.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        var row = new byte[buffer.RowBytes];
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                var value = matrix[x, y] ? (byte)0 : (byte)255;
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = value;
                row[x * 4 + 3] = 255;
            }
            Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }
        return bitmap;
    }
}
