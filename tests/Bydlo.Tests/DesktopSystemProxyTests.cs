using Bydlo.Core.Processes;
using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class DesktopSystemProxyTests
{
    [Fact]
    public async Task Gnome_restores_the_previous_proxy_after_a_failed_cleanup()
    {
        const string prefix = "org.gnome.system.proxy";
        var values = new Dictionary<(string, string), string>
        {
            [(prefix, "mode")] = "'auto'",
            [(prefix + ".http", "host")] = "'proxy.invalid'",
            [(prefix + ".http", "port")] = "1234",
            [(prefix + ".http", "use-authentication")] = "true",
            [(prefix + ".https", "host")] = "''",
            [(prefix + ".https", "port")] = "0",
        };
        var before = new Dictionary<(string, string), string>(values);
        var fail = false;
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                Assert.Equal("gsettings", spec.FileName);
                var key = (spec.Arguments[1], spec.Arguments[2]);
                if (spec.Arguments[0] == "get")
                {
                    Assert.False(spec.AffectsNetwork);
                    return (0, values[key]);
                }
                Assert.True(spec.AffectsNetwork);
                if (fail) { return (1, "fake failure"); }
                values[key] = spec.Arguments[3];
                return (0, "");
            },
        };
        var directory = TestDirs.New("proxy-recovery");
        var proxy = new JournaledSystemProxyService(runner, DesktopProxyKind.Gnome, directory, () => { });
        await proxy.SetAsync("127.0.0.1", 2080);
        Assert.Equal("'manual'", values[(prefix, "mode")]);
        Assert.Equal("2080", values[(prefix + ".http", "port")]);
        Assert.Equal("false", values[(prefix + ".http", "use-authentication")]);
        fail = true;
        await Assert.ThrowsAsync<IOException>(() => proxy.ClearAsync());
        Assert.True(File.Exists(Path.Combine(directory, "system-proxy.json")));

        fail = false;
        var next = new JournaledSystemProxyService(runner, DesktopProxyKind.Gnome, directory, () => { });
        await next.ClearAsync();
        Assert.Equal(before, values);
        Assert.False(File.Exists(Path.Combine(directory, "system-proxy.json")));
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task Kde_restores_missing_keys_by_deleting_them()
    {
        var values = new Dictionary<string, string>();
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                var args = spec.Arguments;
                var key = args[5];
                if (spec.FileName == "kreadconfig6")
                {
                    return (0, values.GetValueOrDefault(key, args[7]));
                }
                Assert.Equal("kwriteconfig6", spec.FileName);
                if (args.Contains("--delete")) { values.Remove(key); }
                else { values[key] = args[^1]; }
                return (0, "");
            },
        };
        var proxy = new JournaledSystemProxyService(runner, DesktopProxyKind.Kde6, TestDirs.New("kde-proxy"), () => { });
        await proxy.SetAsync("127.0.0.1", 2080);
        Assert.Equal("1", values["ProxyType"]);
        await proxy.ClearAsync();
        Assert.Empty(values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mac_ignores_disabled_services_and_restores_both_web_proxies(bool failFirstRestore)
    {
        var failDisable = failFirstRestore;
        var previousSecure = failFirstRestore ? ("old.invalid", "8443", false) : ("", "0", false);
        var values = new Dictionary<string, (string Host, string Port, bool Enabled)>
        {
            ["web"] = ("proxy.invalid", "8888", true),
            ["secureweb"] = previousSecure,
        };
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                if (spec.FileName == "scutil")
                {
                    Assert.Equal(["--nwi"], spec.Arguments);
                    return (0, "Network interfaces: en0");
                }
                Assert.Equal("networksetup", spec.FileName);
                var args = spec.Arguments;
                if (args[0] == "-listnetworkserviceorder")
                {
                    return (0, "(1) Wi-Fi\n(Hardware Port: Wi-Fi, Device: en0)\n(2) Ethernet\n(Hardware Port: Ethernet, Device: en1)\n(*) Disabled\n(Hardware Port: Disabled, Device: en2)");
                }

                Assert.Equal("Wi-Fi", args[1]);
                var key = args[0].Contains("secureweb", StringComparison.Ordinal) ? "secureweb" : "web";
                var current = values[key];
                if (args[0].StartsWith("-get", StringComparison.Ordinal))
                {
                    return (0, $"Enabled: {(current.Enabled ? "Yes" : "No")}\nServer: {current.Host}\nPort: {current.Port}\nAuthenticated Proxy Enabled: 0");
                }

                if (failDisable && key == "secureweb" && args[0] == "-setsecurewebproxystate" && args[2] == "off")
                {
                    failDisable = false;
                    return (1, "");
                }
                values[key] = args[0].EndsWith("state", StringComparison.Ordinal)
                    ? current with { Enabled = args[2] == "on" } : (args[2], args[3], true);
                return (0, "");
            },
        };
        var proxy = new JournaledSystemProxyService(runner, DesktopProxyKind.MacOs, TestDirs.New("mac-proxy"), () => { });
        await proxy.SetAsync("127.0.0.1", 2080);
        Assert.Equal(("127.0.0.1", "2080", true), values["secureweb"]);
        if (failFirstRestore)
        {
            await Assert.ThrowsAsync<IOException>(() => proxy.ClearAsync());
            Assert.Equal(("old.invalid", "8443", true), values["secureweb"]);
        }
        await proxy.ClearAsync();
        Assert.Equal(("proxy.invalid", "8888", true), values["web"]);
        Assert.False(values["secureweb"].Enabled);
        if (failFirstRestore)
        {
            Assert.Equal(previousSecure, values["secureweb"]);
        }
    }
}
