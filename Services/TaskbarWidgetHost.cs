using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using VibeMeters.Models;
using Forms = System.Windows.Forms;

namespace VibeMeters.Services;

/// <summary>Positions independent, nonactivating windows in screen pixels. Each widget owns its rendering and uses the taskbar as its native owner.</summary>
internal sealed class TaskbarWidgetHost : IDisposable
{
    private readonly Action _openMain;
    private readonly Dictionary<UsageProviderKind, TaskbarWidgetWindow> _views = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppSettings? _settings;
    private Dictionary<UsageProviderKind, UsageSnapshot> _selected = new();
    private bool _scanFailed;
    private Layout? _lastLayout;
    private Rectangle _free;
    private bool _scanning;
    private DateTime _nextScan;
    private int _generation;

    public TaskbarWidgetHost(Action openMain)
    {
        _openMain = openMain;
        _timer.Tick += (_, _) =>
        {
            // Windows destroys owned windows when Explorer replaces its taskbar HWND.
            if (_settings != null && _views.Count < _selected.Count) SyncViews(_settings, _selected);
            Place();
        };
    }

    public void Update(AppSettings settings, IReadOnlyDictionary<UsageProviderKind, UsageSnapshot> selectedWidgets)
    {
        if (selectedWidgets.Count == 0) { Clear(); return; }
        _settings = settings;
        _selected = new(selectedWidgets);
        SyncViews(settings, _selected);
        _timer.Start();
        Place();
    }

    private void SyncViews(AppSettings settings, IReadOnlyDictionary<UsageProviderKind, UsageSnapshot> selectedWidgets)
    {
        foreach (var kind in _views.Keys.Except(selectedWidgets.Keys).ToArray())
        {
            _views[kind].Close();
            _views.Remove(kind);
        }
        foreach (var (kind, snapshot) in selectedWidgets)
        {
            if (!_views.TryGetValue(kind, out var view))
            {
                view = new TaskbarWidgetWindow(_openMain) { Tag = kind, ShowInTaskbar = false, ShowActivated = false, Topmost = true };
                _views.Add(kind, view);
                view.Closed += (_, _) =>
                {
                    if (_views.TryGetValue(kind, out var current) && current == view) _views.Remove(kind);
                };
                NonActivatingWindow(new WindowInteropHelper(view).EnsureHandle());
            }
            view.Update([snapshot], settings);
        }
    }

    public void Clear()
    {
        _timer.Stop();
        _settings = null;
        _selected.Clear();
        foreach (var view in _views.Values.ToArray()) view.Close();
        _views.Clear();
        _lastLayout = null;
        _free = Rectangle.Empty;
        _nextScan = default;
        _generation++;
    }

    public void Dispose() => Clear();

    private void Place()
    {
        if (_views.Count == 0) return;
        var layout = ReadLayout();
        if (layout is null)
        {
            foreach (var view in _views.Values.Where(w => w.IsVisible)) view.Hide();
            return;
        }
        if (layout != _lastLayout)
        {
            _lastLayout = layout;
            _scanFailed = false;
            _free = Rectangle.Empty;
            _nextScan = default;
            _generation++;
        }
        if (!_scanning && DateTime.UtcNow >= _nextScan) _ = FindFreeAreaAsync(layout);
        var area = Rectangle.Intersect(_free, layout.Bar);
        bool vertical = layout.Vertical;
        double scale = layout.Scale;
        int gap = Math.Max(1, (int)Math.Round(2 * scale));
        var windows = _views.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToArray();
        int axisLength = vertical ? area.Height : area.Width;
        int length = 0;
        var fitted = new List<(TaskbarWidgetWindow View, Size Size)>();
        var now = DateTimeOffset.Now;
        double widthBudget = vertical ? area.Width / scale :
            Math.Max(TaskbarWidgetWindow.MinimumReadableWidth, (area.Width - gap * (windows.Length + 1)) / (scale * windows.Length));
        foreach (var view in windows)
        {
            if (area.Height <= 0 || area.Width / scale < TaskbarWidgetWindow.MinimumReadableWidth)
            {
                if (view.IsVisible) view.Hide();
                continue;
            }
            var placement = vertical
                ? (layout.Bar.Left + layout.Bar.Width / 2 < layout.Monitor.Left + layout.Monitor.Width / 2 ? PlacementMode.Right : PlacementMode.Left)
                : (layout.Bar.Top + layout.Bar.Height / 2 < layout.Monitor.Top + layout.Monitor.Height / 2 ? PlacementMode.Bottom : PlacementMode.Top);
            view.SetToolTipPlacement(placement);
            view.RefreshCountdowns(now);
            view.SetLayout(vertical, widthBudget, area.Height / scale);
            var size = new Size(Math.Min(area.Width, (int)Math.Ceiling(view.Width * scale)),
                Math.Min(area.Height, (int)Math.Ceiling(view.Height * scale)));
            int itemLength = vertical ? size.Height : size.Width;
            int candidate = length + (fitted.Count > 0 ? gap : 0) + itemLength;
            if (candidate + 2 * gap > axisLength)
            {
                if (view.IsVisible) view.Hide();
                continue;
            }
            length = candidate;
            fitted.Add((view, size));
        }
        int cursor = (vertical ? area.Top : area.Left) + (layout.Far ? axisLength - gap - length : gap);
        foreach (var (view, size) in fitted)
        {
            var target = vertical
                ? new Rectangle(area.Left + (area.Width - size.Width) / 2, cursor, size.Width, size.Height)
                : new Rectangle(cursor, area.Top + (area.Height - size.Height) / 2, size.Width, size.Height);
            Move(view, target, layout.Taskbar);
            cursor += (vertical ? size.Height : size.Width) + gap;
        }
    }

    private static void Move(TaskbarWidgetWindow view, Rectangle target, IntPtr taskbar)
    {
        // Owned top-level windows stay above their owner's surface when Explorer takes
        // focus. Changing only our own HWND's owner avoids repeatedly raising it.
        var interop = new WindowInteropHelper(view);
        if (interop.Owner != taskbar) interop.Owner = taskbar;
        var hwnd = interop.Handle;
        bool show = !view.IsVisible;
        if (!show && Bounds(hwnd) == target) return;

        const uint keepZOrderAndFocus = 0x0004 | 0x0010;
        SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, keepZOrderAndFocus);
        if (show)
        {
            view.Show();
            if (Bounds(hwnd) != target)
                SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, keepZOrderAndFocus);
        }
    }

    private sealed record Layout(IntPtr Taskbar, Rectangle Bar, Rectangle Monitor, Rectangle Work, bool Vertical, bool Far, double Scale);

    private static Layout? ReadLayout()
    {
        var handle = FindWindow("Shell_TrayWnd", null);
        var full = Bounds(handle);
        if (full.IsEmpty || !IsWindowVisible(handle)) return null;
        var screen = Forms.Screen.FromHandle(handle);
        var monitor = screen.Bounds;
        var bar = Rectangle.Intersect(full, monitor);
        bool vertical = full.Height > full.Width;
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
        bool autoHide = (SHAppBarMessage(4, ref data) & 1) != 0;
        int thickness = vertical ? bar.Width : bar.Height;
        if (bar.IsEmpty || thickness < 8 || (autoHide && thickness < (vertical ? full.Width : full.Height) - 1)) return null;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != handle)
        {
            var frame = Bounds(foreground);
            if (frame.Contains(monitor))
            {
                var className = new StringBuilder(128);
                GetClassName(foreground, className, className.Capacity);
                if (className.ToString() is not ("Progman" or "WorkerW" or "Shell_SecondaryTrayWnd")) return null;
            }
        }
        var tray = Bounds(FindWindowEx(handle, IntPtr.Zero, "TrayNotifyWnd", null));
        bool far = tray.IsEmpty || (vertical ? tray.Top + tray.Height / 2 >= bar.Top + bar.Height / 2
            : tray.Left + tray.Width / 2 >= bar.Left + bar.Width / 2);
        return new(handle, bar, monitor, screen.WorkingArea, vertical, far, Math.Max(1, GetDpiForWindow(handle)) / 96.0);
    }

    private async Task FindFreeAreaAsync(Layout layout)
    {
        _scanning = true;
        var generation = _generation;
        try
        {
            // UI Automation can cross process boundaries. The dashboard must not wait for Explorer.
            var area = await Task.Run(() => ReadFreeArea(layout));
            if (_generation == generation) _scanFailed = false;
            if (_generation == generation && _views.Count > 0 && area != _free)
            {
                _free = area;
                Place();
            }
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (_generation == generation)
            {
                // A single stale UIA element during a shell focus change is common.
                // If Explorer remains unreadable on the next scan, stop using its gap.
                if (_scanFailed) _free = Rectangle.Empty;
                _scanFailed = true;
            }
        }
        finally { _scanning = false; _nextScan = DateTime.UtcNow.AddSeconds(2); }
    }

    private static Rectangle ReadFreeArea(Layout layout)
    {
        var rebar = FindWindowEx(layout.Taskbar, IntPtr.Zero, "ReBarWindow32", null);
        var pane = rebar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        var list = pane == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(pane, IntPtr.Zero, "MSTaskListWClass", null);
        if (list != IntPtr.Zero)
        {
            var classicGap = ReadFreeArea(layout, list);
            if (classicGap.Width > 0 && classicGap.Height > 0) return classicGap;
        }
        // Some versions retain a classic compatibility HWND while the actual controls
        // live in the XAML tree. Inspect it for space before hiding the widgets.
        return ReadFreeArea(layout, IntPtr.Zero);
    }

    private static Rectangle ReadFreeArea(Layout layout, IntPtr list)
    {
        bool modern = list == IntPtr.Zero;
        var available = modern ? layout.Bar : Rectangle.Intersect(Bounds(list), layout.Bar);
        var root = AutomationElement.FromHandle(modern ? layout.Taskbar : list);
        var cached = new CacheRequest();
        cached.Add(AutomationElement.ProcessIdProperty);
        cached.Add(AutomationElement.BoundingRectangleProperty);
        cached.Add(AutomationElement.ControlTypeProperty);
        cached.Add(AutomationElement.ClassNameProperty);
        cached.Add(AutomationElement.AutomationIdProperty);
        using var active = cached.Activate();
        var nodes = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
        if (modern)
        {
            var tray = Bounds(FindWindowEx(layout.Taskbar, IntPtr.Zero, "TrayNotifyWnd", null));
            if (tray.IsEmpty)
            {
                var frame = nodes.Cast<AutomationElement>().FirstOrDefault(n =>
                    n.Cached.ClassName.EndsWith("SystemTrayFrame", StringComparison.OrdinalIgnoreCase));
                if (frame == null || frame.Cached.BoundingRectangle.IsEmpty) return Rectangle.Empty;
                var rect = frame.Cached.BoundingRectangle;
                tray = Rectangle.FromLTRB((int)rect.Left, (int)rect.Top, (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
            }
            if (!tray.IntersectsWith(available)) return Rectangle.Empty;
            available = layout.Vertical
                ? Rectangle.FromLTRB(available.Left, layout.Far ? available.Top : tray.Bottom, available.Right, layout.Far ? tray.Top : available.Bottom)
                : Rectangle.FromLTRB(layout.Far ? available.Left : tray.Right, available.Top, layout.Far ? tray.Left : available.Right, available.Bottom);
        }
        if (available.Width <= 0 || available.Height <= 0) return Rectangle.Empty;
        int end = layout.Far ? (layout.Vertical ? available.Top : available.Left) : (layout.Vertical ? available.Bottom : available.Right);
        bool found = false;
        foreach (AutomationElement node in nodes)
        {
            if (node.Cached.ProcessId == Environment.ProcessId) continue;
            var control = node.Cached.ControlType;
            var className = node.Cached.ClassName;
            // Layout containers can cover the entire modern taskbar; only their content
            // or individually interactive buttons may establish or constrain the gap.
            if (modern && (control == ControlType.Pane || control == ControlType.Group || control == ControlType.ToolBar ||
                control == ControlType.List || control == ControlType.Window)) continue;
            var bounds = node.Cached.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
            var occupied = Rectangle.Intersect(available, Rectangle.FromLTRB((int)Math.Floor(bounds.Left),
                (int)Math.Floor(bounds.Top), (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom)));
            if (occupied.Width <= 0 || occupied.Height <= 0) continue;
            if (!modern || ((control == ControlType.Button || control == ControlType.ListItem) &&
                (className.Contains("TaskList", StringComparison.OrdinalIgnoreCase) ||
                 node.Cached.AutomationId.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)))) found = true;
            end = layout.Far ? Math.Max(end, layout.Vertical ? occupied.Bottom : occupied.Right)
                             : Math.Min(end, layout.Vertical ? occupied.Top : occupied.Left);
        }
        if (!found) return Rectangle.Empty;
        return layout.Vertical
            ? Rectangle.FromLTRB(available.Left, layout.Far ? end : available.Top, available.Right, layout.Far ? available.Bottom : end)
            : Rectangle.FromLTRB(layout.Far ? end : available.Left, available.Top, layout.Far ? available.Right : end, available.Bottom);
    }

    private static Rectangle Bounds(IntPtr handle) => handle != IntPtr.Zero && GetWindowRect(handle, out var r)
        ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;

    private static void NonActivatingWindow(IntPtr hwnd)
    {
        const int extendedStyle = -20, noActivate = 0x08000000, toolWindow = 0x80, appWindow = 0x40000;
        int style = GetWindowLong(hwnd, extendedStyle);
        SetWindowLong(hwnd, extendedStyle, (style | noActivate | toolWindow) & ~appWindow);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct AppBarData { public uint Size; public IntPtr Window; public uint Callback, Edge; public NativeRect Rect; public IntPtr Param; }
    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint command, ref AppBarData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
