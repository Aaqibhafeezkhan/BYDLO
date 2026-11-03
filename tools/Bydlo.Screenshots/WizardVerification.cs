using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

internal static class WizardVerification
{
    private const string Link = "trojan://fake-private-password@server.example.invalid:443#%D0%9C%D0%BE%D0%B9%20VPN";

    public static void Run(AppServices services, string output)
    {
        var handler = new SubscriptionHandler();
        using var http = new HttpClient(handler);
        var shell = new MainViewModel(services, new SubscriptionClient(http, services.Importer)) { IsOnboardingVisible = false };
        services.Settings.Language = Loc.Instance.Language;
        services.Settings.Theme = ThemePreference.Purple;
        services.Theme.Apply(ThemePreference.Purple);
        var wizard = shell.AddServerWizard;
        var window = new Window { Width = 1180, Height = 780, Content = new MainView { DataContext = shell } };
        window.Show();
        shell.Navigate("connections");
        Capture("wizard-connections", 1180, 780);
        shell.OpenAddServerCommand.Execute(null);
        Pump();
        Require(shell.IsAddServerOpen && wizard.IsSource && !shell.CanReceiveActivation, "Wizard owns the current import flow");
        var cards = window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("source-card") && b.IsEffectivelyVisible);
        Require(cards == 3, "Three source cards are shown");
        Capture("wizard-source", 1180, 780);
        Capture("wizard-source-narrow", 400, 860);

        wizard.ChooseLinkCommand.Execute(null);
        wizard.Input = Link;
        Wait(wizard.ContinueCommand.ExecuteAsync(null));
        Require(wizard.IsPreview && services.Profiles.Profiles.Count == 0, "Preview never persists a server");
        Require(wizard.PreviewItems[0].Protocol == "Trojan" && wizard.PreviewItems[0].Endpoint == "server.example.invalid:443", "Preview identifies protocol and endpoint");
        Require(!wizard.PreviewItems.Any(p => (p.Name + p.Endpoint).Contains("fake-private-password", StringComparison.Ordinal)), "Preview hides password");
        Capture("wizard-preview", 1180, 780);
        Capture("wizard-preview-narrow", 400, 860);
        wizard.CloseCommand.Execute(null);
        Require(!shell.IsAddServerOpen && wizard.Input.Length == 0 && services.Profiles.Profiles.Count == 0, "Cancel clears draft and preserves storage");

        shell.OpenAddServerCommand.Execute(null);
        wizard.ChooseLinkCommand.Execute(null);
        wizard.Input = "unsupported://private-data";
        Wait(wizard.ContinueCommand.ExecuteAsync(null));
        Require(wizard.HasError && !wizard.IsPreview && !wizard.Error.Contains("private-data", StringComparison.Ordinal), "Invalid input has a safe error");
        Capture("wizard-error", 400, 700);
        var language = Loc.Instance.Language;
        Loc.Instance.SetLanguage("en");
        Require(wizard.Error == Loc.Instance["wizard.error.input"] && !wizard.Error.Contains("wizard.error", StringComparison.Ordinal), "English error is localized");
        Loc.Instance.SetLanguage(language);
        wizard.Input = Convert.ToBase64String(Encoding.UTF8.GetBytes(Link + "\n" + Link + "\ninvalid"));
        Wait(wizard.ContinueCommand.ExecuteAsync(null));
        Require(wizard.IsPreview && wizard.PreviewItems.Count == 2 && wizard.SkippedCount == 1, "Base64 list auto-detection and partial errors");
        wizard.CloseCommand.Execute(null);

        shell.OpenAddServerCommand.Execute(null);
        wizard.ChooseFileCommand.Execute(null);
        var config = Path.Combine(output, "test.ovpn");
        File.WriteAllText(config, "client\ndev tun\nremote file.example.invalid 1194\n<key>\nfake-private-key\n</key>\n");
        Wait(wizard.PreviewFilesAsync([config]));
        Require(wizard.IsPreview && wizard.PreviewItems.Single().Protocol == "OpenVPN", "Dropped config is recognised");
        Require(services.Profiles.Profiles.Count == 0, "File preview never persists");
        Wait(wizard.AddCommand.ExecuteAsync(null));
        Require(!shell.IsAddServerOpen && services.Profiles.Profiles.Count == 1, "Confirm adds the file exactly once");

        shell.OpenAddServerCommand.Execute(null);
        wizard.ChooseQrCommand.Execute(null);
        using var qr = ImportVerification.MakeQr(Link);
        var qrPath = Path.Combine(output, "test-qr.png");
        qr.Save(qrPath);
        Wait(wizard.PreviewFilesAsync([qrPath]));
        Require(wizard.IsPreview, "QR image file is recognised");
        wizard.BackCommand.Execute(null);
        Wait(window.Clipboard!.SetBitmapAsync(qr));
        Wait(wizard.PasteCommand.ExecuteAsync(null));
        Require(wizard.IsPreview && services.Profiles.Profiles.Count == 1, "Clipboard screenshot requires confirmation");
        wizard.CloseCommand.Execute(null);

        shell.OpenAddServerCommand.Execute(null);
        wizard.ChooseLinkCommand.Execute(null);
        wizard.Input = "https://subscription.example.invalid/list?token=fake-token";
        Wait(wizard.ContinueCommand.ExecuteAsync(null));
        Require(wizard.IsPreview && handler.Requests == 1 && services.Profiles.Subscriptions.Count == 0, "Subscription is downloaded only for explicit preview");
        Require(wizard.PreviewItems.Count == 2 && wizard.SubscriptionDetails.Length > 0, "Subscription count, traffic and expiry are shown");
        Require(!wizard.Summary.Contains("fake-token", StringComparison.Ordinal), "Subscription URL stays private");
        Capture("wizard-subscription", 1180, 900);
        Wait(wizard.AddCommand.ExecuteAsync(null));
        Require(handler.Requests == 1 && services.Profiles.Subscriptions.Count == 1 && services.Profiles.Profiles.Count == 3, "Confirm saves the fetched snapshot without refetching");

        shell.OpenAddServerCommand.Execute(null);
        wizard.ChooseLinkCommand.Execute(null);
        handler.Fail = true;
        wizard.Input = "https://subscription.example.invalid/broken?token=fake-token";
        Wait(wizard.ContinueCommand.ExecuteAsync(null));
        Require(wizard.HasError && !wizard.IsPreview && !wizard.Error.Contains("fake-token", StringComparison.Ordinal), "HTTP errors do not expose tokens");
        handler.Fail = false;
        handler.Delay = true;
        var loading = wizard.ContinueCommand.ExecuteAsync(null);
        Require(wizard.Busy, "Subscription loading is visible");
        wizard.CloseCommand.Execute(null);
        Wait(loading);
        Require(!shell.IsAddServerOpen && !wizard.IsPreview && !wizard.Busy, "Closing cancels subscription loading without opening a late preview");
        handler.Delay = false;

        shell.OpenAddServerCommand.Execute(null);
        wizard.ChooseManualCommand.Execute(null);
        wizard.Protocol = ProtocolKind.Trojan;
        wizard.Host = "manual.example.invalid";
        Wait(wizard.ContinueCommand.ExecuteAsync(null));
        Require(wizard.HasError && !wizard.IsPreview, "Manual input validates required credentials");
        wizard.Password = "fake-manual-password";
        wizard.ServerName = "Ручное подключение";
        Capture("wizard-manual", 400, 860);
        Wait(wizard.ContinueCommand.ExecuteAsync(null));
        Require(wizard.IsPreview, "Manual input opens preview");
        Wait(wizard.AddCommand.ExecuteAsync(null));
        Require(services.Profiles.Profiles.Count == 4 && !shell.IsAddServerOpen, "Manual server is saved only on confirmation");
        shell.OpenAddServerCommand.Execute(null);
        wizard.ChooseManualCommand.Execute(null);
        wizard.Protocol = ProtocolKind.OpenVpn;
        wizard.ServerName = "OpenVPN с паролем";
        wizard.Configuration = "client\ndev tun\nremote auth.example.invalid 1194\nauth-user-pass";
        Require(wizard.UsesConfiguration && wizard.UsesIdentifier && wizard.UsesPassword && wizard.SecretMask == '●', "OpenVPN exposes masked configuration and credential fields");
        Wait(wizard.ContinueCommand.ExecuteAsync(null));
        Require(wizard.HasError && !wizard.IsPreview, "OpenVPN requires credentials before preview");
        wizard.Identifier = "fake-private-user";
        wizard.Password = "fake-private-password";
        wizard.ErrorKey = null;
        Capture("wizard-manual-openvpn", 1180, 900);
        Capture("wizard-manual-openvpn-narrow", 400, 860);
        var secretBoxes = window.GetVisualDescendants().OfType<TextBox>()
            .Where(box => box.IsEffectivelyVisible && box.Text is "fake-private-user" or "fake-private-password").ToArray();
        Require(secretBoxes.Length == 2 && secretBoxes.All(box => box.PasswordChar == '●'), "Both credentials are visible and masked in the rendered form");
        wizard.ShowSecrets = true;
        Pump();
        Require(secretBoxes.All(box => box.PasswordChar == '\0'), "Show secrets reveals the fields");
        wizard.ShowSecrets = false;
        Wait(wizard.ContinueCommand.ExecuteAsync(null));
        Require(wizard.IsPreview, "OpenVPN credentials pass preview validation");
        Wait(wizard.AddCommand.ExecuteAsync(null));
        var vpn = services.Profiles.Profiles.Single(profile => profile.Host == "auth.example.invalid");
        var stored = services.Secrets.LoadAsync(vpn.Id.ToString());
        Wait(stored);
        Require(stored.Result[Keys.Username] == "fake-private-user" && stored.Result[Keys.Password] == "fake-private-password", "OpenVPN credentials reach secret storage");
        Require(!wizard.ShowSecrets && wizard.Password.Length == 0 && wizard.Identifier.Length == 0, "Completing the wizard clears credentials and disclosure");
        services.TopLevel = () => window;
        var editing = ProfileEditorDialog.ShowAsync(services, vpn);
        Pump();
        var editor = window.OwnedWindows.Single();
        var fields = editor.GetVisualDescendants().OfType<TextBox>().ToArray();
        var protectedFields = fields.Where(box => box.Text is "fake-private-user" or "fake-private-password" ||
            box.Text?.Contains("remote auth.example.invalid", StringComparison.Ordinal) == true).ToArray();
        Require(protectedFields.Length == 3 && protectedFields.All(box => box.PasswordChar == '●'), "Editor masks config and OpenVPN credentials by default");
        fields.Single(box => box.Text == vpn.Name).Text = "Изменённый OpenVPN";
        var configuration = fields.Single(box => box.Text?.Contains("remote auth.example.invalid", StringComparison.Ordinal) == true);
        configuration.Text = configuration.Text!.Replace("auth.example.invalid 1194", "edited.example.invalid 443", StringComparison.Ordinal);
        Pump();
        using (var frame = editor.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "profile-editor-openvpn.png")); }
        var save = editor.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Loc.Instance["common.save"]));
        save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Wait(editing);
        Require(editing.Result is { Profile.Name: "Изменённый OpenVPN", Profile.Host: "edited.example.invalid", Profile.Port: 443 }, "Editor returns validated endpoint changes");
        Require(vpn.Host == "auth.example.invalid", "Editing leaves the original profile untouched before save");
        Wait(services.Profiles.UpdateAsync(editing.Result!.Profile, editing.Result.Secrets));
        Require(services.Connections.Primary.Status == Bydlo.Core.Connection.ConnectionStatus.Disconnected, "Import does not start an engine");
        window.Close();
        Console.WriteLine("Wizard verification passed: source cards, preview, cancel, base64, files, QR image/clipboard, subscription snapshot/metadata/errors/cancellation, manual input.");

        void Capture(string name, double width, double height)
        {
            window.Width = width;
            window.Height = height;
            Pump();
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(output, name + ".png"));
        }
    }

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Require(task.IsCompleted, "Operation completes");
        task.GetAwaiter().GetResult();
    }
    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class SubscriptionHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Fail { get; set; }
        public bool Delay { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Delay) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            if (Fail) { throw new HttpRequestException("fake-token must not appear in the UI"); }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Link + "\nvless://11111111-2222-3333-4444-555555555555@second.example.invalid:443#%D0%92%D1%82%D0%BE%D1%80%D0%BE%D0%B9%20%D1%81%D0%B5%D1%80%D0%B2%D0%B5%D1%80") };
            response.Headers.TryAddWithoutValidation("subscription-userinfo", "upload=1048576; download=5242880; total=10737418240; expire=2000000000");
            return response;
        }
    }
}
