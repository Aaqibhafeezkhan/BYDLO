using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Bydlo.Platform;

public sealed record WindowsApplication(string Name, byte[]? Icon);

public static partial class WindowsApplicationCatalog
{
    public const int IconSize = 16;

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<WindowsApplication> ListApplications()
    {
        var applications = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var file = name + ".exe";
                        applications.TryAdd(file, null);
                        if (applications[file] is null)
                        {
                            applications[file] = process.MainModule?.FileName;
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // A process can exit or reject a query while the list is being collected.
                }
            }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var apps = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (apps is null)
                {
                    continue;
                }
                foreach (var name in apps.GetSubKeyNames().Where(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    applications.TryAdd(name, null);
                    if (applications[name] is null)
                    {
                        using var entry = apps.OpenSubKey(name);
                        applications[name] = (entry?.GetValue("") as string)?.Trim('"');
                    }
                }
            }
        }

        return applications.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new WindowsApplication(pair.Key, ReadIcon(pair.Value))).ToArray();
    }

    private static byte[]? ReadIcon(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || ExtractIconEx(path, 0, 0, out var icon, 1) != 1 || icon == 0)
        {
            return null;
        }

        var dc = CreateCompatibleDC(0);
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            if (dc == 0)
            {
                return null;
            }
            var info = new BitmapInfoHeader
            {
                Size = 40, Width = IconSize, Height = -IconSize, Planes = 1, BitsPerPixel = 32,
            };
            bitmap = CreateDIBSection(dc, ref info, 0, out var pixels, 0, 0);
            if (bitmap == 0)
            {
                return null;
            }
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1 ||
                !DrawIconEx(dc, 0, 0, icon, IconSize, IconSize, 0, 0, 3))
            {
                return null;
            }
            var bytes = new byte[IconSize * IconSize * 4];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (previous != 0 && previous != -1) { SelectObject(dc, previous); }
            if (bitmap != 0) { DeleteObject(bitmap); }
            if (dc != 0) { DeleteDC(dc); }
            DestroyIcon(icon);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitsPerPixel;
        public uint Compression;
        public uint ImageSize;
        public int PixelsPerMeterX;
        public int PixelsPerMeterY;
        public uint ColorsUsed;
        public uint ImportantColors;
    }

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(string file, int index, nint large, out nint small, uint count);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint pixels, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
}
