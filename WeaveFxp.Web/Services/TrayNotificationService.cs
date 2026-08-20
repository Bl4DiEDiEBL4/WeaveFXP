using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Web.Services;

public sealed class TrayNotificationService : IHostedService, IDisposable
{
    private const uint TrayId = 9010;
    private const uint CallbackMessage = WindowMessages.WmApp + 910;
    private const uint MenuOpen = 1001;
    private const uint MenuToggleTransferNotifications = 1002;
    private const uint MenuToggleApiNotifications = 1003;
    private const uint MenuTestNotification = 1004;
    private const uint MenuStopEngine = 1005;
    private const uint MenuQuit = 1006;

    private readonly WeaveEngine _engine;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ConcurrentQueue<TrayNotification> _queue = new();
    private readonly HashSet<string> _notifiedJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private Thread? _trayThread;
    private ManualResetEventSlim? _ready;
    private WindowProc? _windowProc;
    private string _windowClass = "";
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private bool _iconOwned;
    private bool _trayAdded;
    private bool _disposed;
    private string _status = "Not started";

    public TrayNotificationService(WeaveEngine engine, IHostApplicationLifetime lifetime)
    {
        _engine = engine;
        _lifetime = lifetime;
    }

    public string StatusText => _status;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _engine.Changed += OnEngineChanged;
        foreach (var job in _engine.Jobs().Where(j => j.Terminal))
            _notifiedJobs.Add(job.Id);
        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => ProcessNotifications(_workerCts.Token), CancellationToken.None);
        RefreshFromSettings();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _engine.Changed -= OnEngineChanged;
        _workerCts?.Cancel();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { }
        }
        StopTrayThread();
    }

    private void OnEngineChanged()
    {
        RefreshFromSettings();
        NotifyFinishedJobs();
    }

    private void RefreshFromSettings()
    {
        var settings = _engine.Settings(false);
        if (!OperatingSystem.IsWindows())
        {
            SetStatus("Tray icon is Windows-only.");
            return;
        }
        if (!Environment.UserInteractive)
        {
            SetStatus("Tray icon unavailable in this session.");
            return;
        }
        if (!settings.TrayIconEnabled)
        {
            StopTrayThread();
            SetStatus("Tray icon disabled in Settings.");
            return;
        }

        EnsureTrayStarted();
        RefreshTrayIcon();
    }

    private void NotifyFinishedJobs()
    {
        var settings = _engine.Settings(false);
        if (!settings.TrayIconEnabled || !settings.TransferNotificationsEnabled) return;

        foreach (var job in _engine.Jobs().Where(j => j.Terminal).OrderBy(j => j.FinishedAt))
        {
            lock (_gate)
            {
                if (!_notifiedJobs.Add(job.Id)) continue;
                if (_notifiedJobs.Count > 500)
                    _notifiedJobs.Remove(_notifiedJobs.First());
            }

            if (job.State == JobState.Succeeded)
                Queue("Transfer completed", BuildJobBody(job), BalloonIconFlags.Info);
            else if (job.State == JobState.Failed)
                Queue("Transfer failed", BuildJobBody(job), BalloonIconFlags.Error);
            else if (job.State == JobState.Cancelled)
                Queue("Transfer cancelled", BuildJobBody(job), BalloonIconFlags.Warning);
        }
    }

    private void Queue(string title, string body, BalloonIconFlags icon)
    {
        if (!IsSupported) return;
        EnsureTrayStarted();
        _queue.Enqueue(new TrayNotification(title, body, icon));
    }

    private async Task ProcessNotifications(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (_queue.TryDequeue(out var notification))
                    ShowBalloon(notification);
                await Task.Delay(350, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private void EnsureTrayStarted()
    {
        if (!IsSupported || _disposed) return;
        lock (_gate)
        {
            if (_trayThread is not null) return;
            _ready = new ManualResetEventSlim(false);
            _trayThread = new Thread(TrayThreadMain)
            {
                IsBackground = true,
                Name = "WeaveFXP tray"
            };
            if (OperatingSystem.IsWindows())
                TrySetSta(_trayThread);
            _trayThread.Start();
        }
        try { _ready?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    [SupportedOSPlatform("windows")]
    private static void TrySetSta(Thread thread)
    {
        try { thread.SetApartmentState(ApartmentState.STA); } catch { }
    }

    private void StopTrayThread()
    {
        Thread? thread;
        IntPtr window;
        lock (_gate)
        {
            thread = _trayThread;
            window = _windowHandle;
            _trayThread = null;
        }
        if (window != IntPtr.Zero)
            NativeMethods.PostMessage(window, WindowMessages.WmClose, IntPtr.Zero, IntPtr.Zero);
        if (thread is not null && thread.IsAlive)
        {
            try { thread.Join(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private void TrayThreadMain()
    {
        try
        {
            WindowProc windowProc = WndProc;
            _windowProc = windowProc;
            _windowClass = "WeaveFxpTray_" + Guid.NewGuid().ToString("N");
            var instance = NativeMethods.GetModuleHandle(null);
            var wndClass = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(windowProc),
                hInstance = instance,
                lpszClassName = _windowClass
            };

            if (NativeMethods.RegisterClassEx(ref wndClass) == 0)
            {
                SetStatus($"Tray icon failed: RegisterClassEx error {Marshal.GetLastWin32Error()}.");
                _ready?.Set();
                return;
            }

            var window = NativeMethods.CreateWindowEx(0, _windowClass, "WeaveFXP Tray", 0, 0, 0, 0, 0,
                NativeMethods.HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
            lock (_gate) _windowHandle = window;
            if (window == IntPtr.Zero)
            {
                SetStatus($"Tray icon failed: CreateWindowEx error {Marshal.GetLastWin32Error()}.");
                _ready?.Set();
                return;
            }

            _iconHandle = LoadTrayIcon();
            AddTrayIcon(window);
            _ready?.Set();

            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Tray icon failed: {ex.Message}");
            _ready?.Set();
        }
        finally
        {
            RemoveTrayIcon();
            if (_iconHandle != IntPtr.Zero)
            {
                if (_iconOwned) NativeMethods.DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
                _iconOwned = false;
            }
            lock (_gate)
            {
                if (ReferenceEquals(_trayThread, Thread.CurrentThread))
                    _trayThread = null;
                _windowHandle = IntPtr.Zero;
                _trayAdded = false;
                _ready?.Set();
            }
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xffff;
            if (mouseMessage is WindowMessages.WmMouseMove)
                RefreshTrayIcon();
            else if (mouseMessage is WindowMessages.WmLButtonDoubleClick or WindowMessages.NinSelect or WindowMessages.NinKeySelect)
                OpenDashboard();
            else if (mouseMessage is WindowMessages.WmRButtonUp or WindowMessages.WmContextMenu)
                ShowContextMenu(hWnd);
            return IntPtr.Zero;
        }
        if (message == WindowMessages.WmDestroy)
        {
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu(IntPtr window)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            var settings = _engine.Settings(false);
            NativeMethods.AppendMenu(menu, MenuFlags.String, new UIntPtr(MenuOpen), "Open WeaveFXP");
            NativeMethods.AppendMenu(menu, MenuFlags.Separator, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, MenuFlags.String | (settings.TransferNotificationsEnabled ? MenuFlags.Checked : MenuFlags.None), new UIntPtr(MenuToggleTransferNotifications), "Transfer notifications");
            NativeMethods.AppendMenu(menu, MenuFlags.String | (settings.ApiNotificationsEnabled ? MenuFlags.Checked : MenuFlags.None), new UIntPtr(MenuToggleApiNotifications), "API notifications");
            NativeMethods.AppendMenu(menu, MenuFlags.String, new UIntPtr(MenuTestNotification), "Test notification");
            NativeMethods.AppendMenu(menu, MenuFlags.Separator, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, MenuFlags.String, new UIntPtr(MenuStopEngine), "Stop engine");
            NativeMethods.AppendMenu(menu, MenuFlags.String, new UIntPtr(MenuQuit), "Quit WeaveFXP");

            if (!NativeMethods.GetCursorPos(out var point)) return;
            NativeMethods.SetForegroundWindow(window);
            var command = NativeMethods.TrackPopupMenuEx(menu,
                TrackPopupMenuFlags.RightButton | TrackPopupMenuFlags.NoNotify | TrackPopupMenuFlags.ReturnCommand,
                point.X, point.Y, window, IntPtr.Zero);
            if (command != 0) HandleTrayCommand(command);
            NativeMethods.PostMessage(window, WindowMessages.WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private void HandleTrayCommand(uint command)
    {
        switch (command)
        {
            case MenuOpen:
                OpenDashboard();
                break;
            case MenuToggleTransferNotifications:
                ToggleSettings(s => s.TransferNotificationsEnabled = !s.TransferNotificationsEnabled);
                break;
            case MenuToggleApiNotifications:
                ToggleSettings(s => s.ApiNotificationsEnabled = !s.ApiNotificationsEnabled);
                break;
            case MenuTestNotification:
                Queue("WeaveFXP notification", "Tray notifications are active.", BalloonIconFlags.Info);
                break;
            case MenuStopEngine:
            case MenuQuit:
                _lifetime.StopApplication();
                break;
        }
    }

    private void ToggleSettings(Action<AppSettings> update)
    {
        try
        {
            var settings = _engine.Settings(false);
            update(settings);
            _engine.UpdateSettings(settings);
            RefreshTrayIcon();
        }
        catch { }
    }

    private void AddTrayIcon(IntPtr window)
    {
        var data = CreateNotifyIconData(window);
        data.uFlags = NotifyIconFlags.Message | NotifyIconFlags.Icon | NotifyIconFlags.Tip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _iconHandle;
        data.szTip = BuildTooltip();
        if (NativeMethods.ShellNotifyIcon(NotifyIconMessage.Add, ref data))
        {
            _trayAdded = true;
            SetStatus("Tray icon active.");
        }
        else
        {
            SetStatus($"Tray icon failed: Shell_NotifyIcon error {Marshal.GetLastWin32Error()}.");
        }
    }

    private void RemoveTrayIcon()
    {
        lock (_gate)
        {
            if (!_trayAdded || _windowHandle == IntPtr.Zero) return;
            var data = CreateNotifyIconData(_windowHandle);
            NativeMethods.ShellNotifyIcon(NotifyIconMessage.Delete, ref data);
            _trayAdded = false;
        }
    }

    private void ShowBalloon(TrayNotification notification)
    {
        lock (_gate)
        {
            if (!_trayAdded || _windowHandle == IntPtr.Zero) return;
            var data = CreateNotifyIconData(_windowHandle);
            data.uFlags = NotifyIconFlags.Info | NotifyIconFlags.Tip;
            data.szTip = BuildTooltip();
            data.szInfoTitle = Truncate(notification.Title, 63);
            data.szInfo = Truncate(notification.Body, 255);
            data.dwInfoFlags = notification.Icon | BalloonIconFlags.RespectQuietTime;
            NativeMethods.ShellNotifyIcon(NotifyIconMessage.Modify, ref data);
        }
    }

    private void RefreshTrayIcon()
    {
        lock (_gate)
        {
            if (!_trayAdded || _windowHandle == IntPtr.Zero) return;
            var data = CreateNotifyIconData(_windowHandle);
            data.uFlags = NotifyIconFlags.Tip;
            data.szTip = BuildTooltip();
            NativeMethods.ShellNotifyIcon(NotifyIconMessage.Modify, ref data);
        }
    }

    private IntPtr LoadTrayIcon()
    {
        var exeIcon = LoadIconFromExecutable();
        if (exeIcon != IntPtr.Zero)
        {
            _iconOwned = true;
            return exeIcon;
        }
        var iconPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico");
        if (File.Exists(iconPath))
        {
            var loaded = NativeMethods.LoadImage(IntPtr.Zero, iconPath, ImageType.Icon, 0, 0,
                LoadImageFlags.LoadFromFile | LoadImageFlags.DefaultSize);
            if (loaded != IntPtr.Zero)
            {
                _iconOwned = true;
                return loaded;
            }
        }
        _iconOwned = false;
        return NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IdiApplication);
    }

    private static IntPtr LoadIconFromExecutable()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return IntPtr.Zero;
        var extracted = NativeMethods.ExtractIconEx(exePath, 0, out var largeIcon, out var smallIcon, 1);
        if (extracted == 0) return IntPtr.Zero;
        if (smallIcon != IntPtr.Zero)
        {
            if (largeIcon != IntPtr.Zero) NativeMethods.DestroyIcon(largeIcon);
            return smallIcon;
        }
        return largeIcon;
    }

    private void OpenDashboard()
    {
        var settings = _engine.Settings(false);
        var host = settings.WebBindAddress is "0.0.0.0" or "*" ? "127.0.0.1" : settings.WebBindAddress;
        OpenBrowser($"http://{host}:{settings.WebPort}/");
    }

    private static void OpenBrowser(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { }
    }

    private string BuildTooltip()
    {
        var settings = _engine.Settings(false);
        var jobs = _engine.Jobs();
        var running = jobs.Count(j => j.State == JobState.Running);
        var queued = jobs.Count(j => j.State == JobState.Queued);
        return $"WeaveFXP v{_engine.Version}\nEngine: Running\nQueue: {running} running, {queued} queued\nTransfer notifications: {(settings.TransferNotificationsEnabled ? "On" : "Off")}";
    }

    private static string BuildJobBody(Job job)
    {
        var release = string.IsNullOrWhiteSpace(job.Request.Label)
            ? Path.GetFileName(job.Request.SourcePath.TrimEnd('/', '\\'))
            : job.Request.Label;
        var route = $"{job.Request.FromSite} -> {job.Request.ToSite}";
        var detail = job.State == JobState.Failed && !string.IsNullOrWhiteSpace(job.Error)
            ? $" ({job.Error})"
            : "";
        return Truncate($"{route}: {release}{detail}", 255);
    }

    private static NotifyIconData CreateNotifyIconData(IntPtr window) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = window,
        uID = TrayId,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private static bool IsSupported => OperatingSystem.IsWindows() && Environment.UserInteractive;

    private static string Truncate(string value, int max)
    {
        value = (value ?? "").Trim();
        return value.Length <= max ? value : value[..Math.Max(0, max - 3)] + "...";
    }

    private void SetStatus(string status) => _status = status;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        _workerCts?.Dispose();
        _ready?.Dispose();
    }

    private sealed record TrayNotification(string Title, string Body, BalloonIconFlags Icon);

    private static class WindowMessages
    {
        public const uint WmNull = 0x0000;
        public const uint WmClose = 0x0010;
        public const uint WmDestroy = 0x0002;
        public const uint WmMouseMove = 0x0200;
        public const uint WmLButtonDoubleClick = 0x0203;
        public const uint WmRButtonUp = 0x0205;
        public const uint WmContextMenu = 0x007B;
        public const uint WmUser = 0x0400;
        public const uint NinSelect = WmUser;
        public const uint NinKeySelect = WmUser + 1;
        public const uint WmApp = 0x8000;
    }

    private enum NotifyIconMessage : uint { Add = 0, Modify = 1, Delete = 2 }
    [Flags] private enum NotifyIconFlags : uint { Message = 1, Icon = 2, Tip = 4, Info = 16 }
    [Flags] private enum BalloonIconFlags : uint { Info = 1, Warning = 2, Error = 3, RespectQuietTime = 128 }
    private enum ImageType : uint { Icon = 1 }
    [Flags] private enum LoadImageFlags : uint { DefaultSize = 64, LoadFromFile = 16 }
    [Flags] private enum MenuFlags : uint { None = 0, String = 0, Checked = 8, Separator = 2048 }
    [Flags] private enum TrackPopupMenuFlags : uint { RightButton = 2, NoNotify = 128, ReturnCommand = 256 }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public NotifyIconFlags uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public BalloonIconFlags dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    private static class NativeMethods
    {
        public static readonly IntPtr HwndMessage = new(-3);
        public static readonly IntPtr IdiApplication = new(32512);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShellNotifyIcon(NotifyIconMessage dwMessage, ref NotifyIconData lpData);

        [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] public static extern int GetMessage(out Message lpMsg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Message lpMsg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref Message lpMsg);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int nExitCode);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AppendMenu(IntPtr hMenu, MenuFlags flags, UIntPtr id, string? text);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint TrackPopupMenuEx(IntPtr menu, TrackPopupMenuFlags flags, int x, int y, IntPtr hwnd, IntPtr reserved);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadImage(IntPtr hinst, string name, ImageType type, int cx, int cy, LoadImageFlags flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
