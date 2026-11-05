using Bydlo.Core.Models;
using Bydlo.Engines.Zapret;

namespace Bydlo.Tests;

public class LinuxZapretDomainsTests
{
    [Fact]
    public void Merge_removes_comments_blank_lines_and_case_insensitive_duplicates()
    {
        Assert.Equal("youtube.com\nnew.example\n", NativeZapretDomains.Merge(
            "youtube.com\n", "# comment\r\n YouTube.COM \r\n\nnew.example # note\n"));
    }

    [Theory]
    [InlineData("native-split")]
    [InlineData("native-disorder")]
    public void Both_profiles_use_bundled_domains_and_preserve_separate_user_lists(string strategy)
    {
        var install = new ZapretInstallation(TestDirs.New("linux-domains"));
        install.WriteUserList(ZapretUserList.General, "personal.example");
        install.WriteUserList(ZapretUserList.Exclude, "excluded.example");
        var args = NativeZapretStrategy.Build(OsFamily.Linux, install, strategy, "disabled");
        var domains = File.ReadAllLines(Path.Combine(install.UserListsDirectory, "default-hosts.txt"));
        Assert.Contains("youtube.com", domains);
        Assert.Contains("accounts.spotify.com", domains);
        Assert.Contains("7tv.app", domains);
        Assert.Equal(domains.Length, domains.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(domains, line => line.StartsWith('#') || string.IsNullOrWhiteSpace(line));
        Assert.Equal(2, args.Count(arg => arg == "--hostlist=default-hosts.txt"));
        Assert.Equal(2, args.Count(arg => arg == "--hostlist=list-general-user.txt"));
        Assert.Equal(2, args.Count(arg => arg == "--hostlist-exclude=list-exclude-user.txt"));
        Assert.Equal(2, args.Count(arg => arg == "--ipset-exclude=ipset-exclude-user.txt"));
        Assert.Contains("--filter-tcp=80,443", args);
        Assert.Contains("--filter-udp=443", args);
        Assert.Equal("personal.example\n", install.ReadUserList(ZapretUserList.General));
        Assert.Equal("excluded.example\n", install.ReadUserList(ZapretUserList.Exclude));
    }
}
