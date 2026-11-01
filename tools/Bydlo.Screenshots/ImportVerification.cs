using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using ZXing;

internal static class ImportVerification
{
    // Uses a headless clipboard, fake share links and isolated appdata. Never starts any engine.
    public static void Run(MainViewModel shell, string output)
    {
        const string link = "vless://11111111-2222-3333-4444-555555555555@example.invalid:443#Import%20test";
        var window = new Window { Width = 1180, Height = 780, Content = new MainView { DataContext = shell } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var count = shell.Profiles.Count;

        Wait(shell.PreviewExternalAsync([link]));
        Require(shell.HasImportPreview && shell.CanConfirmImport, "Share-link preview");
        Require(shell.Profiles.Count == count, "Preview must not persist profiles");
        Require(shell.ImportPreviewItems.All(x => !x.Contains("11111111", StringComparison.Ordinal)), "Preview hides credentials");
        Capture("import-preview", 1180, 780);
        Capture("import-preview-compact", 400, 700);
        shell.CancelImportCommand.Execute(null);
        Require(!shell.HasImportPreview && shell.Profiles.Count == count, "Cancel does not import");

        Wait(shell.PreviewExternalAsync(["https://example.invalid/sub?token=secret", "https://example.invalid/sub2?token=secret"]));
        Require(shell.ImportPreviewItems.Count == 2 && shell.CanConfirmImport, "Multiple subscriptions preview without network");
        Require(shell.Services.Profiles.Subscriptions.Count == 0, "Preview must not create subscriptions");
        shell.CancelImportCommand.Execute(null);

        Wait(shell.PreviewExternalAsync([link + ".conf"]));
        Require(shell.CanConfirmImport, "Links ending in file extensions are still links");
        shell.CancelImportCommand.Execute(null);

        var config = Path.Combine(output, "import-test.ovpn");
        File.WriteAllText(config, "client\nremote example.invalid 1194\nproto udp\ndev tun\n");
        Wait(shell.PreviewExternalAsync([config, config]));
        Require(shell.ImportPreviewItems.Count == 2 && shell.CanConfirmImport, "Multiple dropped config paths");
        shell.CancelImportCommand.Execute(null);

        Wait(shell.PreviewExternalAsync(["unsupported"]));
        Require(shell.HasImportPreview && !shell.CanConfirmImport, "Unsupported input cannot be confirmed");
        shell.CancelImportCommand.Execute(null);

        var clipboard = window.Clipboard!;
        Wait(clipboard.SetTextAsync(link));
        Wait(shell.CheckClipboardAsync());
        Require(shell.ClipboardOffer is not null && !shell.ClipboardOffer.Contains("11111111", StringComparison.Ordinal), "Safe clipboard banner");
        Capture("import-clipboard", 1180, 780);
        shell.DismissClipboardOfferCommand.Execute(null);
        Wait(shell.CheckClipboardAsync());
        Require(shell.ClipboardOffer is null, "Dismissed links are not reoffered");
        shell.Settings.SuggestClipboardLinks = false;
        Wait(clipboard.SetTextAsync(link + "2"));
        Wait(shell.CheckClipboardAsync());
        Require(shell.ClipboardOffer is null, "Disabled clipboard suggestions");
        shell.Settings.SuggestClipboardLinks = true;
        Wait(shell.CheckClipboardAsync());
        Require(shell.ClipboardOffer is not null, "Reenabled clipboard suggestions");
        Wait(shell.AcceptClipboardOfferCommand.ExecuteAsync(null));
        Require(shell.HasImportPreview && shell.ClipboardOffer is null, "Banner acceptance opens preview");
        shell.CancelImportCommand.Execute(null);

        using var bitmap = MakeQr(link);
        Wait(clipboard.SetBitmapAsync(bitmap));
        Wait(shell.PasteImportCommand.ExecuteAsync(null));
        Require(shell.HasImportPreview && shell.CanConfirmImport, "Clipboard QR image decoded");
        Require(shell.Profiles.Count == count, "QR paste requires confirmation");
        Wait(shell.ConfirmImportCommand.ExecuteAsync(null));
        Require(!shell.HasImportPreview && shell.Profiles.Count == count + 1, "Confirm persists profile");

        shell.IsDragOver = true;
        Capture("import-drop", 1180, 780);
        shell.IsDragOver = false;
        shell.Navigate("settings");
        Capture("import-settings", 1180, 900);
        Capture("import-settings-compact", 400, 860);
        window.Close();
        Console.WriteLine("Import verification passed: preview/cancel/confirm, files, subscriptions, clipboard dedup/settings, QR image.");

        void Capture(string name, double width, double height)
        {
            window.Width = width;
            window.Height = height;
            for (var i = 0; i < 20; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(15);
            }

            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(output, name + ".png"));
        }
    }

    private static WriteableBitmap MakeQr(string payload)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions { Width = 480, Height = 480, Margin = 4 },
        };
        var pixels = writer.Write(payload);
        var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        Marshal.Copy(pixels.Pixels, 0, buffer.Address, pixels.Pixels.Length);
        return bitmap;
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Import verification failed: " + message);
        }
    }
}
