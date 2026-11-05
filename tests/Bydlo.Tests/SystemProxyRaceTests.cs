using Bydlo.Core.Processes;
using Bydlo.Platform;

namespace Bydlo.Tests;

/// <summary>Issue 337: emergency Clear during Set must not leave the system proxy on a dead port.</summary>
public sealed class SystemProxyRaceTests
{
    [Fact]
    public async Task Clear_waits_for_concurrent_set_and_restores_proxy()
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
        using var setBlocked = new ManualResetEventSlim(false);
        using var releaseReads = new ManualResetEventSlim(false);
        var reads = 0;
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                var key = (spec.Arguments[1], spec.Arguments[2]);
                if (spec.Arguments[0] == "get")
                {
                    if (Interlocked.Increment(ref reads) == 1)
                    {
                        setBlocked.Set();
                        releaseReads.Wait(TimeSpan.FromSeconds(10));
                    }
                    return (0, values[key]);
                }
                values[key] = spec.Arguments[3];
                return (0, "");
            },
        };
        var directory = TestDirs.New("proxy-race");
        var proxy = new JournaledSystemProxyService(runner, DesktopProxyKind.Gnome, directory, () => { });
        var journal = Path.Combine(directory, "system-proxy.json");

        // Run Set on the pool: its first snapshot read blocks a pool thread
        // (not the test thread), letting us overlap Clear like the engine-failure path.
        var setting = Task.Run(() => proxy.SetAsync("127.0.0.1", 2080));
        Assert.True(setBlocked.Wait(TimeSpan.FromSeconds(10)));

        // Engine failure handler fires while Set is still gathering its snapshot.
        var clearing = proxy.ClearAsync();
        await Task.Delay(100);
        Assert.False(clearing.IsCompleted);

        releaseReads.Set();
        await setting;
        await clearing;

        Assert.Equal(before, values);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task Clear_without_journal_stays_noop()
    {
        var runner = new FakeProcessRunner();
        var directory = TestDirs.New("proxy-race-noop");
        var proxy = new JournaledSystemProxyService(runner, DesktopProxyKind.Gnome, directory, () => { });
        await proxy.ClearAsync();
        Assert.False(File.Exists(Path.Combine(directory, "system-proxy.json")));
        Assert.Empty(runner.Completed);
    }
}
