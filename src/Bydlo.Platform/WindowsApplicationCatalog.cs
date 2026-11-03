using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Bydlo.Platform;

public static class WindowsApplicationCatalog
{
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> ListExecutableNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name + ".exe");
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process may exit while the list is being collected.
                }
            }
        }

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var apps = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (apps is not null)
                {
                    names.UnionWith(apps.GetSubKeyNames().Where(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        return names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
