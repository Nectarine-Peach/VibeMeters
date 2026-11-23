using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using VibeMeters.Models;

namespace VibeMeters.Services;

internal sealed class ProviderTaskbarButtons(Action showMainWindow) : IDisposable
{
    private readonly Dictionary<UsageProviderKind, ButtonWindow> _windows = new();

    public void Update(AppSettings settings, IEnumerable<UsageSnapshot> visible)
    {
        var selected = visible.Where(s => settings.IsProviderVisible(s.Provider)).ToDictionary(s => s.Provider);
        foreach (var removed in _windows.Keys.Except(selected.Keys).ToArray())
            _windows[removed].Close();

        foreach (var (provider, snapshot) in selected)
        {
            if (!_windows.TryGetValue(provider, out var window))
            {
                window = new ButtonWindow(provider, showMainWindow);
                _windows.Add(provider, window);
                window.Closed += (_, _) => _windows.Remove(provider);
            }
            var mode = settings.GetDisplayMode(provider);
            window.Title = TaskbarProviderPresentation.Title(snapshot, mode);
            window.TaskbarItemInfo.Description = Describe(snapshot, mode);
            if (!window.IsVisible) window.Show();
        }
    }

    public void Clear()
    {
        foreach (var window in _windows.Values.ToArray()) window.Close();
    }

    public void Dispose() => Clear();

    private static string Describe(UsageSnapshot snapshot, UsageDisplayMode mode)
    {
        if (snapshot.Status != UsageStatus.Ready || snapshot.Windows.Count == 0)
            return $"{snapshot.Provider} · {Localizer.T("확인 필요")}";
        var lines = new List<string> { $"{snapshot.Provider} · {Localizer.T(mode == UsageDisplayMode.Used ? "사용" : "남음")}" };
        foreach (var quota in snapshot.Windows.Where(w => double.IsFinite(w.UsedPercent)))
        {
            var percent = mode == UsageDisplayMode.Used ? 100 - quota.RemainingPercent : quota.RemainingPercent;
            var reset = quota.ResetsAt is { } at
                ? $"{(Localizer.IsEnglish ? "Reset" : "리셋")} {at.ToLocalTime().ToString("g", Localizer.Culture)}"
                : UsageResetText.Countdown(null, snapshot.CheckedAt);
            lines.Add($"{Localizer.T(quota.Name)} {percent:0}% · {reset}");
        }
        return string.Join("\n", lines);
    }

    private sealed class ButtonWindow : Window
    {
        private readonly Action _showMainWindow;
        private bool _restoreQueued;
        private IntPtr _handle;

        public ButtonWindow(UsageProviderKind provider, Action showMainWindow)
        {
            _showMainWindow = showMainWindow;
            Tag = provider;
            Icon = TaskbarProviderPresentation.Icon(provider);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Width = Height = 1;
            ShowActivated = false;
            ShowInTaskbar = true;
            WindowState = WindowState.Minimized;
            TaskbarItemInfo = new TaskbarItemInfo();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _handle = new WindowInteropHelper(this).Handle;
            WindowTaskbarGroup.Set(_handle, $"NectarinePeach.VibeMeters.{Tag}");
            HwndSource.FromHwnd(_handle)?.AddHook(WindowMessage);
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int wmSysCommand = 0x0112, scRestore = 0xF120;
            if (message == wmSysCommand && ((long)wParam & 0xFFF0) == scRestore)
            {
                handled = true;
                QueueDashboard();
            }
            return IntPtr.Zero;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (IsVisible && WindowState != WindowState.Minimized) QueueDashboard();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (IsVisible) QueueDashboard();
        }

        private void QueueDashboard()
        {
            if (_restoreQueued) return;
            _restoreQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                _restoreQueued = false;
                if (!IsVisible || _handle == IntPtr.Zero) return;
                WindowState = WindowState.Minimized;
                _showMainWindow();
            });
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel || _handle == IntPtr.Zero) return;
            WindowTaskbarGroup.Set(_handle, null);
            _handle = IntPtr.Zero;
        }
    }
}

// An explicit per-HWND AppUserModelID gives each provider its own taskbar group
// while all of them share the dashboard's process and dispatcher.
internal static class WindowTaskbarGroup
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public IntPtr Value, Padding;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant value);
        void SetValue(in PropertyKey key, in PropVariant value);
        void Commit();
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetPropertyStoreForWindow(IntPtr hwnd, in Guid iid, out IPropertyStore store);

    private static readonly PropertyKey AppId = new() { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };

    public static void Set(IntPtr hwnd, string? group)
    {
        IPropertyStore? store = null;
        var value = new PropVariant();
        try
        {
            SHGetPropertyStoreForWindow(hwnd, typeof(IPropertyStore).GUID, out store);
            if (group != null)
            {
                value.Type = (ushort)VarEnum.VT_LPWSTR;
                value.Value = Marshal.StringToCoTaskMemUni(group);
            }
            // VT_EMPTY releases the shell's per-window property before destruction.
            store.SetValue(AppId, value);
        }
        catch (COMException) { /* Shell unavailable: ordinary taskbar grouping still works. */ }
        finally
        {
            Marshal.FreeCoTaskMem(value.Value);
            if (store != null) Marshal.ReleaseComObject(store);
        }
    }
}
