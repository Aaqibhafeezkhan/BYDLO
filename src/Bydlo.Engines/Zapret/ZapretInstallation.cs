using Bydlo.Core.Models;

namespace Bydlo.Engines.Zapret;

public enum ZapretUserList
{
    /// <summary><c>lists/list-general-user.txt</c> — extra domains to process.</summary>
    General,

    /// <summary><c>lists/list-exclude-user.txt</c> — domains never to touch.</summary>
    Exclude,

    /// <summary><c>lists/ipset-exclude-user.txt</c> — IPs / subnets never to touch.</summary>
    IpsetExclude,
}

/// <summary>An extracted Flowseal zapret-discord-youtube release folder.</summary>
public sealed class ZapretInstallation(string root, string? userListsDirectory = null, string? nativeExecutable = null, OsFamily os = OsFamily.Windows)
{
    private static readonly string[] WindowsDependencies = ["WinDivert.dll", "WinDivert64.sys", "cygwin1.dll"];

    public string? ReleaseVersion { get; init; }

    public string Root { get; } = root;

    public string BinDirectory => Path.Combine(Root, "bin");

    public string ListsDirectory => Path.Combine(Root, "lists");

    public OsFamily Os { get; } = os;

    public string UserListsDirectory => userListsDirectory ?? ListsDirectory;

    public string ExecutablePath => nativeExecutable ?? WinwsPath;

    public string WinwsPath => Path.Combine(BinDirectory, "winws.exe");

    public bool IsValid => File.Exists(ExecutablePath) && (Os != OsFamily.Windows || (Directory.Exists(ListsDirectory) &&
        WindowsDependencies.All(name => File.Exists(Path.Combine(BinDirectory, name)))));

    /// <summary>Version from <c>service.bat</c> (<c>set "LOCAL_VERSION=x.y.z"</c>), if present.</summary>
    public string? Version
    {
        get
        {
            if (ReleaseVersion is not null) { return ReleaseVersion; }
            var service = Path.Combine(Root, "service.bat");
            if (!File.Exists(service))
            {
                return null;
            }

            foreach (var line in File.ReadLines(service).Take(10))
            {
                const string marker = "LOCAL_VERSION=";
                var i = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    return line[(i + marker.Length)..].Trim('"', ' ');
                }
            }

            return null;
        }
    }

    public IReadOnlyList<ZapretStrategy> Strategies => Os == OsFamily.Windows ? ZapretStrategyParser.Discover(Root) : NativeZapretStrategy.Strategies;

    public string UserListPath(ZapretUserList list) => Path.Combine(UserListsDirectory, list switch
    {
        ZapretUserList.General => "list-general-user.txt",
        ZapretUserList.Exclude => "list-exclude-user.txt",
        _ => "ipset-exclude-user.txt",
    });

    /// <summary>Creates user lists with the same defaults as Flowseal's <c>:load_user_lists</c> (they must never be empty).</summary>
    public void EnsureUserLists()
    {
        Directory.CreateDirectory(UserListsDirectory);
        WriteIfMissing(ZapretUserList.IpsetExclude, "203.0.113.113/32\n");
        WriteIfMissing(ZapretUserList.General, "# Never leave this file empty\ndomain.example.abc\n");
        WriteIfMissing(ZapretUserList.Exclude, "domain.example.abc\n");
    }

    public string ReadUserList(ZapretUserList list)
    {
        EnsureUserLists();
        return File.ReadAllText(UserListPath(list));
    }

    /// <summary>Saves a user list, normalizing entries and keeping a placeholder so winws never sees an empty file.</summary>
    public void WriteUserList(ZapretUserList list, string content)
    {
        EnsureUserLists();
        var lines = content.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!lines.Any(l => !l.StartsWith('#')))
        {
            lines.Add(list == ZapretUserList.IpsetExclude ? "203.0.113.113/32" : "domain.example.abc");
        }

        File.WriteAllText(UserListPath(list), string.Join('\n', lines) + "\n");
    }

    /// <summary>Copies user lists from an older installation into this one (used after updating strategies).</summary>
    public void ImportUserListsFrom(ZapretInstallation previous)
    {
        Directory.CreateDirectory(UserListsDirectory);
        foreach (var list in Enum.GetValues<ZapretUserList>())
        {
            var src = previous.UserListPath(list);
            if (File.Exists(src))
            {
                if (!File.Exists(UserListPath(list)))
                {
                    File.Copy(src, UserListPath(list));
                }
            }
        }
    }

    private void WriteIfMissing(ZapretUserList list, string content)
    {
        var path = UserListPath(list);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content);
        }
    }
}
