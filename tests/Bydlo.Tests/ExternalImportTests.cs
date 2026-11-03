using Bydlo.Core.Import;
using Bydlo.Platform;

namespace Bydlo.Tests;

public class ExternalImportTests
{
    [Theory]
    [InlineData("vless://id@example.com:443", "VLESS")]
    [InlineData("VMESS://encoded", "VMESS")]
    [InlineData("trojan://password@example.com:443", "TROJAN")]
    [InlineData("ss://encoded@example.com:443", "SS")]
    [InlineData("hy2://password@example.com:443", "HY2")]
    [InlineData("tuic://id:password@example.com:443", "TUIC")]
    [InlineData("socks5h://proxy.example:1080", "SOCKS5H")]
    [InlineData("SOCKS5H://proxy.example:1080", "SOCKS5H")]
    [InlineData(" https://example.com/sub?token=secret ", "subscription")]
    [InlineData("https://example.com", null)]
    [InlineData("https://example.com/a\nvless://id@example.com:443", null)]
    [InlineData("file:///config.conf", null)]
    [InlineData("javascript://test", null)]
    [InlineData("vless://", null)]
    public void Recognizes_only_single_supported_links(string input, string? kind) =>
        Assert.Equal(kind, ExternalImportInput.LinkKind(input));

    [Fact]
    public void Disabled_suggestions_do_not_consume_links_and_seen_links_are_not_reoffered()
    {
        var history = new ClipboardSuggestionHistory();
        const string link = "vless://id@example.com:443";
        Assert.False(history.TryOffer(link, false));
        Assert.True(history.TryOffer(link, true));
        Assert.False(history.TryOffer(" " + link + " ", true));
        Assert.False(history.TryOffer("unrelated clipboard", true));
        Assert.False(history.TryOffer("https://example.com/sub", true));
        Assert.False(history.TryOffer(link, true));
    }

    [Theory]
    [InlineData("https://example.org/docs")]
    [InlineData("https://example.org/?lang=ru")]
    [InlineData("https://example.org")]
    [InlineData("http://example.org/docs")]
    [InlineData(" https://example.org/sub?token=secret ")]
    [InlineData("https://example.org/api/v1/client/subscribe?token=secret")]
    public void Clipboard_does_not_offer_web_URLs_but_manual_subscription_import_remains_available(string input)
    {
        var history = new ClipboardSuggestionHistory();

        Assert.False(history.TryOffer(input, true));
        Assert.True(ProfileImporter.LooksLikeSubscriptionUrl(input, allowRoot: true));
    }

    [Theory]
    [InlineData("vless://id@example.com:443")]
    [InlineData("VMESS://encoded")]
    [InlineData("trojan://password@example.com:443")]
    [InlineData("ss://encoded@example.com:443")]
    [InlineData("hy2://password@example.com:443")]
    [InlineData("tuic://id:password@example.com:443")]
    [InlineData("wg://encoded")]
    [InlineData("socks5://user:password@example.com:1080")]
    [InlineData("socks5h://proxy.example:1080")]
    [InlineData("SOCKS5H://proxy.example:1080")]
    public void Clipboard_still_offers_explicit_protocol_links_once(string input)
    {
        var history = new ClipboardSuggestionHistory();

        Assert.True(history.TryOffer(input, true));
        Assert.False(history.TryOffer(" " + input + " ", true));
    }

    [Fact]
    public void Launch_arguments_ignore_flags_and_unrelated_files()
    {
        var inputs = ExternalImportInput.LaunchInputs(["--mock", "--unknown", "a.exe", "a.CONF", "b.ovpn", "qr.PNG", "vless://id@example.com:443"]).ToArray();
        Assert.Equal(["a.CONF", "b.ovpn", "qr.PNG", "vless://id@example.com:443"], inputs);
    }

    [Theory]
    [InlineData("socks5h://proxy.example:1080")]
    [InlineData("SOCKS5H://proxy.example:1080")]
    public void Launch_arguments_preserve_socks5h_links_for_import(string input)
    {
        var inputs = ExternalImportInput.LaunchInputs(["--mock", "unrelated", input]).ToArray();

        Assert.Equal([input], inputs);
        var result = new ProfileImporter().ImportText(Assert.Single(inputs));
        Assert.Empty(result.Errors);
        var profile = Assert.Single(result.Profiles).Profile;
        Assert.Equal(Bydlo.Core.Models.ProtocolKind.Socks5, profile.Protocol);
        Assert.Equal("proxy.example", profile.Host);
        Assert.Equal(1080, profile.Port);
    }

    [Fact]
    public void Windows_command_quotes_both_executable_and_input()
    {
        var path = Path.GetFullPath(Path.Combine("folder with spaces", "BYDLO.exe"));
        Assert.Equal($"\"{path}\" \"%1\"", WindowsImportAssociations.OpenCommand(path));
        Assert.Throws<ArgumentException>(() => WindowsImportAssociations.OpenCommand("BYDLO.exe"));
        Assert.Throws<ArgumentException>(() => WindowsImportAssociations.OpenCommand(path + "\"extra"));
    }
}
