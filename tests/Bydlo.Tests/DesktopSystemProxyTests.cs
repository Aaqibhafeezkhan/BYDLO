using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class DesktopSystemProxyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("host")]
    [InlineData("port")]
    public async Task Gnome_restores_the_previous_proxy_after_a_failed_cleanup(string? foreignKey)
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
        if (foreignKey is not null)
        {
            values[(prefix + ".http", foreignKey)] = foreignKey == "host" ? "'foreign.invalid'" : "3128";
            before[(prefix, "mode")] = "'manual'";
            foreach (var key in new[] { "host", "port", "use-authentication" })
            {
                before[(prefix + ".http", key)] = values[(prefix + ".http", key)];
            }
        }
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

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public async Task Kde_restores_missing_keys_and_preserves_a_foreign_proxy(int version, bool foreignProxy)
    {
        var values = new Dictionary<string, string>();
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                var args = spec.Arguments;
                var key = args[5];
                Assert.Equal("kioslaverc", args[1]);
                Assert.Equal("Proxy Settings", args[3]);
                if (spec.FileName == "kreadconfig" + version)
                {
                    Assert.False(spec.AffectsNetwork);
                    return (0, values.GetValueOrDefault(key, args[7]));
                }
                Assert.Equal("kwriteconfig" + version, spec.FileName);
                Assert.True(spec.AffectsNetwork);
                if (args.Contains("--delete")) { values.Remove(key); }
                else { values[key] = args[^1]; }
                return (0, "");
            },
        };
        var proxy = new JournaledSystemProxyService(runner, version == 6 ? DesktopProxyKind.Kde6 : DesktopProxyKind.Kde5,
            TestDirs.New("kde-proxy"), () => { });
        await proxy.SetAsync("127.0.0.1", 2080);
        Assert.Equal("1", values["ProxyType"]);
        Assert.Equal("http://127.0.0.1 2080", values["httpProxy"]);
        if (foreignProxy) { values["httpProxy"] = "http://foreign.invalid 3128"; }
        await proxy.ClearAsync();
        if (foreignProxy)
        {
            Assert.Equal("http://foreign.invalid 3128", values["httpProxy"]);
            Assert.Equal("1", values["ProxyType"]);
            Assert.Equal(2, values.Count);
        }
        else { Assert.Empty(values); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Safe_dev_refuses_setup_and_recovery_before_running_commands(int desktop)
    {
        Assert.True(NetworkSafetyGuard.IsLocked);
        var directory = TestDirs.New("desktop-proxy-safety");
        var runner = new FakeProcessRunner();
        var proxy = new JournaledSystemProxyService(runner, (DesktopProxyKind)desktop, directory);
        await Assert.ThrowsAsync<NetworkSafetyException>(() => proxy.SetAsync("127.0.0.1", 2080));
        var journal = Path.Combine(directory, "system-proxy.json");
        Assert.False(File.Exists(journal));
        await File.WriteAllTextAsync(journal, "unread journal");
        await Assert.ThrowsAsync<NetworkSafetyException>(() => proxy.ClearAsync());
        Assert.Equal("unread journal", await File.ReadAllTextAsync(journal));
        Assert.Empty(runner.Completed);
        Assert.Empty(runner.Started);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Partial_kde_setup_recovers_after_failed_rollback_in_a_new_instance(int successfulWrites)
    {
        var directory = TestDirs.New("kde-proxy-partial");
        var journal = Path.Combine(directory, "system-proxy.json");
        var values = new Dictionary<string, string>();
        var writes = 0;
        var fail = true;
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                var args = spec.Arguments;
                var key = args[5];
                if (spec.FileName == "kreadconfig6") { return (0, values.GetValueOrDefault(key, args[7])); }
                Assert.Equal("kwriteconfig6", spec.FileName);
                // Every mutation, including a partial setup, must already have a complete journal.
                Assert.True(File.Exists(journal));
                Assert.Contains("httpsProxy", File.ReadAllText(journal), StringComparison.Ordinal);
                if (fail && writes++ >= successfulWrites) { return (1, "fake failure"); }
                if (args.Contains("--delete")) { values.Remove(key); }
                else { values[key] = args[^1]; }
                return (0, "");
            },
        };
        var proxy = new JournaledSystemProxyService(runner, DesktopProxyKind.Kde6, directory, () => { });
        await Assert.ThrowsAsync<IOException>(() => proxy.SetAsync("127.0.0.1", 2080));
        Assert.Equal(successfulWrites > 0, File.Exists(journal));
        fail = false;
        var next = new JournaledSystemProxyService(runner, DesktopProxyKind.Kde6, directory, () => { });
        await next.ClearAsync();
        Assert.Empty(values);
        Assert.False(File.Exists(journal));
        Assert.Empty(runner.Started);
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
        var directory = TestDirs.New("mac-proxy");
        var proxy = new JournaledSystemProxyService(runner, DesktopProxyKind.MacOs, directory, () => { });
        await proxy.SetAsync("127.0.0.1", 2080);
        Assert.Equal(("127.0.0.1", "2080", true), values["secureweb"]);
        if (failFirstRestore)
        {
            await Assert.ThrowsAsync<IOException>(() => proxy.ClearAsync());
            Assert.Equal(("old.invalid", "8443", true), values["secureweb"]);
            proxy = new JournaledSystemProxyService(runner, DesktopProxyKind.MacOs, directory, () => { });
        }
        await proxy.ClearAsync();
        Assert.Equal(("proxy.invalid", "8888", true), values["web"]);
        Assert.False(values["secureweb"].Enabled);
        if (failFirstRestore)
        {
            Assert.Equal(previousSecure, values["secureweb"]);
        }
        Assert.False(File.Exists(Path.Combine(directory, "system-proxy.json")));
    }
}
