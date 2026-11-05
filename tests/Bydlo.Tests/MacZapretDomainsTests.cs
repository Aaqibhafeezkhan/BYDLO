using Bydlo.Core.Models;
using Bydlo.Engines.Zapret;

namespace Bydlo.Tests;

public class MacZapretDomainsTests
{
    [Theory]
    [InlineData("native-split")]
    [InlineData("native-disorder")]
    public void Tpws_reads_combined_domains_by_absolute_path_and_preserves_user_lists(string strategy)
    {
        var install = new ZapretInstallation(TestDirs.New("mac-domains"));
        install.WriteUserList(ZapretUserList.General, "personal.example");
        install.WriteUserList(ZapretUserList.Exclude, "excluded.example");
        var args = NativeZapretStrategy.Build(OsFamily.MacOS, install, strategy, "disabled");
        var hosts = Assert.Single(args, arg => arg.StartsWith("--hostlist=", StringComparison.Ordinal)
            && arg.EndsWith("default-hosts.txt", StringComparison.Ordinal))["--hostlist=".Length..];
        Assert.True(Path.IsPathRooted(hosts));
        var domains = File.ReadAllLines(hosts);
        Assert.Contains("youtube.com", domains);
        Assert.Contains("accounts.spotify.com", domains);
        Assert.Contains("7tv.app", domains);
        Assert.Equal(domains.Length, domains.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("youtube.com\nnew.example\n", NativeZapretDomains.Merge("youtube.com\n",
            "# comment\r\n YouTube.COM \r\n\nnew.example # note\n"));
        Assert.Contains("--hostlist=" + install.UserListPath(ZapretUserList.General), args);
        Assert.Contains("--hostlist-exclude=" + install.UserListPath(ZapretUserList.Exclude), args);
        Assert.Contains("--ipset-exclude=" + install.UserListPath(ZapretUserList.IpsetExclude), args);
        Assert.Contains("--user=root", args);
        Assert.Contains("--port=21771", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("--filter-udp", StringComparison.Ordinal));
        Assert.Equal("personal.example\n", install.ReadUserList(ZapretUserList.General));
        Assert.Equal("excluded.example\n", install.ReadUserList(ZapretUserList.Exclude));
    }
}
