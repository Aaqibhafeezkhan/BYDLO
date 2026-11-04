using Bydlo.BundleEngines;
using Bydlo.Engines.Zapret;

namespace Bydlo.Tests;

public sealed class ZapretHostListTests
{
    [Fact]
    public void Bundled_strategy_uses_the_extended_list_and_keeps_user_lists_separate()
    {
        var (install, hostList, additions) = CreateBundle();
        const string upstream = "# Flowseal\r\nupstream.example";
        File.WriteAllText(hostList, upstream);
        File.WriteAllText(additions, "# BYDLO\nUPSTREAM.example\n New.Example \nnew.example\n");
        var userList = install.UserListPath(ZapretUserList.General);
        var exclusions = install.UserListPath(ZapretUserList.Exclude);
        File.WriteAllText(userList, "personal.example\n");
        File.WriteAllText(exclusions, "new.example\n");

        ZapretHostList.Apply(install.WinwsPath, additions);
        ZapretHostList.Apply(install.WinwsPath, additions);

        Assert.Equal(upstream + "\r\nnew.example\r\n", File.ReadAllText(hostList));
        Assert.Equal("personal.example\n", File.ReadAllText(userList));
        Assert.Equal("new.example\n", File.ReadAllText(exclusions));
        var strategy = ZapretStrategyParser.Parse(
            "start bin/winws.exe --hostlist=\"%LISTS%list-general.txt\" --hostlist=\"%LISTS%list-general-user.txt\" --hostlist-exclude=\"%LISTS%list-exclude-user.txt\"",
            install.Root, GameFilterValues.From("disabled"), install.UserListsDirectory);
        Assert.Contains("--hostlist=" + hostList, strategy.Arguments);
        Assert.Contains("--hostlist=" + userList, strategy.Arguments);
        Assert.Contains("--hostlist-exclude=" + exclusions, strategy.Arguments);
    }

    [Fact]
    public void Invalid_additions_fail_before_changing_the_upstream_list()
    {
        var (install, hostList, additions) = CreateBundle();
        File.WriteAllText(hostList, "upstream.example\n");
        File.WriteAllText(additions, "new.example\nhttps://bad.example/path\n");

        Assert.Throws<InvalidDataException>(() => ZapretHostList.Apply(install.WinwsPath, additions));
        Assert.Equal("upstream.example\n", File.ReadAllText(hostList));
    }

    [Fact]
    public void A_comment_only_list_preserves_upstream_bytes()
    {
        var (install, hostList, additions) = CreateBundle();
        File.WriteAllText(hostList, "upstream.example\r\n", System.Text.Encoding.UTF8);
        var original = File.ReadAllBytes(hostList);
        File.WriteAllText(additions, "# No confirmed domains yet\n\n");

        ZapretHostList.Apply(install.WinwsPath, additions);
        Assert.Equal(original, File.ReadAllBytes(hostList));
    }

    private static (ZapretInstallation Install, string HostList, string Additions) CreateBundle()
    {
        var root = TestDirs.New("zapret-packaging");
        var install = new ZapretInstallation(Path.Combine(root, "flowseal"), Path.Combine(root, "user"));
        Directory.CreateDirectory(install.ListsDirectory);
        Directory.CreateDirectory(install.UserListsDirectory);
        return (install, Path.Combine(install.ListsDirectory, "list-general.txt"), Path.Combine(root, "additions.txt"));
    }
}
