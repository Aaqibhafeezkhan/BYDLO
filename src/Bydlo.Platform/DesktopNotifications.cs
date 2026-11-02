using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bydlo.Core.Processes;

namespace Bydlo.Platform;

/// <summary>Native desktop notifications; absence of a notification daemon is non-fatal.</summary>
public sealed class DesktopNotifications(IProcessRunner runner) : IDisposable
{
    private static readonly SemaphoreSlim NotificationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    public async Task ShowAsync(nint window, string title, string message)
    {
        var ct = _shutdown.Token;
        await NotificationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                await ShowWindowsAsync(window, title, message, ct).ConfigureAwait(false);
                return;
            }

            var spec = OperatingSystem.IsMacOS()
                ? new ProcessSpec
                {
                    FileName = "/usr/bin/osascript",
                    Arguments = ["-e", "on run argv\ndisplay notification (item 2 of argv) with title (item 1 of argv)\nend run", "--", title, message],
                    AffectsNetwork = false,
                }
                : new ProcessSpec
                {
                    FileName = "notify-send", Arguments = ["--app-name=BYDLO", "--", title, message], AffectsNetwork = false,
                };
            var result = await runner.RunToCompletionAsync(spec, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (result.ExitCode != 0) { throw new IOException("Desktop notification service rejected the notification."); }
        }
        finally { NotificationGate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task ShowWindowsAsync(nint window, string title, string message, CancellationToken ct)
    {
        if (window == 0) { return; }
        var data = new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 0x4259444C,
            Flags = 2 | 4 | 16, Icon = LoadIcon(0, 32512), Tip = "BYDLO",
            Info = message.Length > 255 ? message[..255] : message,
            InfoTitle = title.Length > 63 ? title[..63] : title, InfoFlags = 1,
        };
        // A separate, short-lived balloon icon avoids relying on Avalonia's private native tray ID.
        if (!ShellNotifyIcon(0, ref data)) { throw new IOException("Windows rejected the desktop notification."); }
        try { await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
        finally { ShellNotifyIcon(2, ref data); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint operation, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern nint LoadIcon(nint instance, nint name);

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
