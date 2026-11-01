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
        Assert.True(history.TryOffer("https://example.com/sub", true));
        Assert.False(history.TryOffer(link, true));
    }

    [Fact]
    public void Launch_arguments_ignore_flags_and_unrelated_files()
    {
        var inputs = ExternalImportInput.LaunchInputs(["--mock", "--unknown", "a.exe", "a.CONF", "b.ovpn", "qr.PNG", "vless://id@example.com:443"]).ToArray();
        Assert.Equal(["a.CONF", "b.ovpn", "qr.PNG", "vless://id@example.com:443"], inputs);
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
