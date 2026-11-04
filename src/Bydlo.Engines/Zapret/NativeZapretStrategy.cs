using Bydlo.Core.Models;
using Bydlo.Core.Engines;

namespace Bydlo.Engines.Zapret;

/// <summary>BYDLO-owned presets using upstream nfqws/tpws options; never executes a batch or shell script.</summary>
public static class NativeZapretStrategy
{
    public const int QueueNumber = 21770;
    public const int ProxyPort = 21771;
    public const string PacketMark = "0x40000000";
    public const string DefaultHosts = "youtube.com\ngooglevideo.com\nytimg.com\nyoutubei.googleapis.com\ndiscord.com\ndiscord.gg\ndiscordapp.com\ndiscordapp.net\ndiscord.media\n";

    public static IReadOnlyList<ZapretStrategy> Strategies { get; } =
        [new("native-split", "TCP split / QUIC fake"), new("native-disorder", "TCP disorder / QUIC fake")];

    public static string TcpPorts(string gameFilter) => gameFilter is "all" or "tcp" ? "80,443,1024-65535" : "80,443";

    public static string UdpPorts(string gameFilter) => gameFilter is "all" or "udp" ? "443,1024-65535" : "443";

    public static IReadOnlyList<string> Build(OsFamily os, ZapretInstallation install, string strategy, string gameFilter)
    {
        if (!Strategies.Any(s => s.FileName == strategy))
        {
            throw new EngineException("Unknown native Zapret strategy.", localizationKey: "error.engine.zapret.strategy.unknown");
        }

        var hosts = Path.Combine(install.UserListsDirectory, "default-hosts.txt");
        File.WriteAllText(hosts, DefaultHosts);
        string ListPath(string path) => os == OsFamily.Linux ? Path.GetFileName(path) : path;
        string[] lists = [$"--hostlist={ListPath(hosts)}", $"--hostlist={ListPath(install.UserListPath(ZapretUserList.General))}",
            $"--hostlist-exclude={ListPath(install.UserListPath(ZapretUserList.Exclude))}", $"--ipset-exclude={ListPath(install.UserListPath(ZapretUserList.IpsetExclude))}"];
        if (os == OsFamily.MacOS)
        {
            // /dev/pf requires root for NAT lookups on macOS. PF excludes root to prevent recursion.
            return ["--user=root", "--bind-addr=127.0.0.1", $"--port={ProxyPort}", "--split-pos=1,midsld",
                .. (strategy == "native-disorder" ? new[] { "--disorder" } : []), .. lists];
        }

        return [$"--qnum={QueueNumber}", $"--dpi-desync-fwmark={PacketMark}", $"--filter-tcp={TcpPorts(gameFilter)}",
            .. lists, strategy == "native-disorder" ? "--dpi-desync=multidisorder" : "--dpi-desync=multisplit",
            "--dpi-desync-split-pos=1,midsld", "--new", $"--filter-udp={UdpPorts(gameFilter)}", "--filter-l7=quic",
            .. lists, "--dpi-desync=fake", "--dpi-desync-repeats=6"];
    }
}
