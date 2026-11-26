using System.ComponentModel;
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
    private long _nextScan;
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
        if (layout != _lastLayout)
        {
            _lastLayout = layout;
            _scanFailed = false;
            _free = Rectangle.Empty;
            _nextScan = default;
            _generation++;
        }
        if (layout is null)
        {
            foreach (var view in _views.Values.Where(w => w.IsVisible)) view.Hide();
            return;
        }
        if (!_scanning && Environment.TickCount64 >= _nextScan) _ = FindFreeAreaAsync(layout);
        var area = Rectangle.Intersect(_free, layout.Bar);
        bool vertical = layout.Vertical;
        double scale = layout.Scale;
        int gap = Math.Max(1, (int)Math.Round(2 * scale));
        var windows = _views.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToArray();
        int axisLength = vertical ? area.Height : area.Width;
        int length = 0;
        var fitted = new List<(TaskbarWidgetWindow View, Size Size)>();
        var now = DateTimeOffset.Now;
        int minimumWidth = (int)Math.Ceiling(MinimumWidth(vertical) * scale);
        int widthBudget = vertical ? area.Width : WidgetLengthBudget(area.Width, windows.Length, gap, minimumWidth);
        int heightBudget = vertical ? WidgetLengthBudget(area.Height, windows.Length, gap,
            (int)Math.Ceiling(TaskbarWidgetWindow.MinimumHeight * scale)) : area.Height;
        foreach (var view in windows)
        {
            view.RefreshCountdowns(now);
            if (heightBudget <= 0 || widthBudget < minimumWidth)
            {
                if (view.IsVisible) view.Hide();
                continue;
            }
            view.SetLayout(widthBudget / scale, heightBudget / scale);
            var size = new Size(Math.Min(widthBudget, (int)Math.Ceiling(view.Width * scale)),
                Math.Min(heightBudget, (int)Math.Ceiling(view.Height * scale)));
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
            var placement = vertical
                ? (layout.Bar.Left + layout.Bar.Width / 2 < layout.Monitor.Left + layout.Monitor.Width / 2 ? PlacementMode.Right : PlacementMode.Left)
                : (layout.Bar.Top + layout.Bar.Height / 2 < layout.Monitor.Top + layout.Monitor.Height / 2 ? PlacementMode.Bottom : PlacementMode.Top);
            // Anchor beyond the whole bar so the tooltip stays clear of adjacent widgets.
            double offset = placement switch
            {
                PlacementMode.Left => layout.Bar.Left - target.Left,
                PlacementMode.Right => layout.Bar.Right - target.Right,
                PlacementMode.Top => layout.Bar.Top - target.Top,
                _ => layout.Bar.Bottom - target.Bottom
            };
            view.SetToolTipPlacement(placement, offset / scale);
            Move(view, target, layout.Taskbar);
            cursor += (vertical ? size.Height : size.Width) + gap;
        }
    }

    /// <summary>A vertical taskbar has height to spare, so its widgets may stack the logo above narrower rows.</summary>
    internal static double MinimumWidth(bool vertical) =>
        vertical ? TaskbarWidgetWindow.NarrowMinimumWidth : TaskbarWidgetWindow.MinimumReadableWidth;

    internal static int WidgetLengthBudget(int length, int count, int gap, int minimumLength)
    {
        // Reserve the edge gaps and divide in physical pixels. Rounding up a fractional
        // DIP budget independently for each HWND can make the last widget appear not to fit.
        int fitting = Math.Min(count, (length - gap) / (minimumLength + gap));
        return fitting <= 0 ? 0 : (length - (fitting + 1) * gap) / fitting;
    }

    private static void Move(TaskbarWidgetWindow view, Rectangle target, IntPtr taskbar)
    {
        // Owned top-level windows stay above their owner's surface when Explorer takes
        // focus. Changing only our own HWND's owner avoids repeatedly raising it.
        var interop = new WindowInteropHelper(view);
        var hwnd = interop.Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (interop.Owner != taskbar) interop.Owner = taskbar;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1400) { return; } // Explorer replaced the HWND mid-placement.
        bool show = !view.IsVisible;
        if (!show && IsIconic(hwnd)) ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
        bool nativeHidden = !IsWindowVisible(hwnd);
        if (!show && !nativeHidden && Bounds(hwnd) == target) return;

        const uint keepZOrderAndFocus = 0x0004 | 0x0010;
        // Explorer can hide an owned HWND without changing its WPF Visibility.
        uint flags = !show && nativeHidden ? keepZOrderAndFocus | 0x0040 /* SWP_SHOWWINDOW */ : keepZOrderAndFocus;
        SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, flags);
        if (show)
        {
            view.Show();
            if (Bounds(hwnd) != target)
                SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, keepZOrderAndFocus);
        }
    }

    private sealed record Layout(IntPtr Taskbar, Rectangle Bar, Rectangle Monitor, bool Vertical, bool Far, double Scale);

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
            // A maximized window's invisible resize borders can cover the monitor
            // even while the taskbar is usable. Only full-screen content suppresses it.
            var origin = new Point();
            if (GetClientRect(foreground, out var client) && ClientToScreen(foreground, ref origin)
                && new Rectangle(origin, new Size(client.Right, client.Bottom)).Contains(monitor))
            {
                var className = new StringBuilder(128);
                GetClassName(foreground, className, className.Capacity);
                if (className.ToString() is not ("Progman" or "WorkerW" or "Shell_SecondaryTrayWnd")) return null;
            }
        }
        var tray = Bounds(FindWindowEx(handle, IntPtr.Zero, "TrayNotifyWnd", null));
        bool far = tray.IsEmpty || TrayAtFarEnd(tray, bar, vertical);
        uint dpi = GetDpiForWindow(handle);
        if (dpi == 0) return null; // Explorer destroyed the handle while its bounds were being read.
        return new(handle, bar, monitor, vertical, far, dpi / 96.0);
    }

    private static bool TrayAtFarEnd(Rectangle tray, Rectangle bar, bool vertical) => vertical
        ? tray.Top + tray.Height / 2 >= bar.Top + bar.Height / 2
        : tray.Left + tray.Width / 2 >= bar.Left + bar.Width / 2;

    private async Task FindFreeAreaAsync(Layout layout)
    {
        _scanning = true;
        var generation = _generation;
        Rectangle? area = null;
        try
        {
            // UI Automation can cross process boundaries. The dashboard must not wait for Explorer.
            area = await Task.Run(() => ReadFreeArea(layout));
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException
            or UnauthorizedAccessException or TimeoutException or Win32Exception)
        {
            // An unavailable UIA element during a shell transition is equivalent to an incomplete tree.
        }
        finally { _scanning = false; }

        if (generation != _generation) return;
        _nextScan = Environment.TickCount64 + (area.HasValue ? 2000 : 1000);
        var nextArea = area ?? (_scanFailed ? Rectangle.Empty : _free);
        _scanFailed = !area.HasValue;
        if (_free == nextArea) return;
        _free = nextArea;
        Place();
    }

    private static Rectangle? ReadFreeArea(Layout layout)
    {
        var rebar = FindWindowEx(layout.Taskbar, IntPtr.Zero, "ReBarWindow32", null);
        var pane = rebar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        var list = pane == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(pane, IntPtr.Zero, "MSTaskListWClass", null);
        Rectangle? classicGap = list == IntPtr.Zero ? null : ReadFreeArea(layout, list);
        if (classicGap is { Height: > 0 } gap && gap.Width >= MinimumWidth(layout.Vertical) * layout.Scale &&
            (!layout.Vertical || gap.Height >= TaskbarWidgetWindow.MinimumHeight * layout.Scale))
            return classicGap;
        // Some versions retain a classic compatibility HWND while the actual controls
        // live in the XAML tree. Inspect it for space before hiding the widgets.
        return ReadFreeArea(layout, IntPtr.Zero) ?? classicGap;
    }

    private static Rectangle? ReadFreeArea(Layout layout, IntPtr list)
    {
        bool modern = list == IntPtr.Zero;
        var available = modern ? layout.Bar : Rectangle.Intersect(Bounds(list), layout.Bar);
        if (available.Width <= 0 || available.Height <= 0) return null;
        bool far = layout.Far;
        var root = AutomationElement.FromHandle(modern ? layout.Taskbar : list);
        var cached = new CacheRequest();
        cached.Add(AutomationElement.ProcessIdProperty);
        cached.Add(AutomationElement.BoundingRectangleProperty);
        cached.Add(AutomationElement.ControlTypeProperty);
        cached.Add(AutomationElement.ClassNameProperty);
        cached.Add(AutomationElement.AutomationIdProperty);
        cached.Add(AutomationElement.IsInvokePatternAvailableProperty);
        using var active = cached.Activate();
        var nodes = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
        if (modern)
        {
            var tray = Bounds(FindWindowEx(layout.Taskbar, IntPtr.Zero, "TrayNotifyWnd", null));
            if (tray.IsEmpty)
            {
                var frame = nodes.Cast<AutomationElement>().FirstOrDefault(n =>
                    n.Cached.ClassName.EndsWith("SystemTrayFrame", StringComparison.OrdinalIgnoreCase));
                if (frame == null || frame.Cached.BoundingRectangle.IsEmpty) return null;
                var rect = frame.Cached.BoundingRectangle;
                tray = Rectangle.FromLTRB((int)rect.Left, (int)rect.Top, (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
            }
            if (!tray.IntersectsWith(available)) return null;
            far = TrayAtFarEnd(tray, layout.Bar, layout.Vertical);
            available = layout.Vertical
                ? Rectangle.FromLTRB(available.Left, far ? available.Top : tray.Bottom, available.Right, far ? tray.Top : available.Bottom)
                : Rectangle.FromLTRB(far ? available.Left : tray.Right, available.Top, far ? tray.Left : available.Right, available.Bottom);
        }
        if (available.Width <= 0 || available.Height <= 0) return Rectangle.Empty;
        var occupiedRects = new List<Rectangle>();
        bool found = false;
        foreach (AutomationElement node in nodes)
        {
            if (node.Cached.ProcessId == Environment.ProcessId) continue;
            var control = node.Cached.ControlType;
            var className = node.Cached.ClassName;
            // Layout containers can cover the entire modern taskbar; only their content
            // or individually interactive buttons may establish or constrain the gap.
            if (modern && (control == ControlType.Pane || control == ControlType.Group || control == ControlType.ToolBar ||
                control == ControlType.List || control == ControlType.Window) &&
                !Equals(node.GetCachedPropertyValue(AutomationElement.IsInvokePatternAvailableProperty), true)) continue;
            var bounds = node.Cached.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
            var occupied = Rectangle.Intersect(available, Rectangle.FromLTRB((int)Math.Floor(bounds.Left),
                (int)Math.Floor(bounds.Top), (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom)));
            if (occupied.Width <= 0 || occupied.Height <= 0) continue;
            if (!modern || ((control == ControlType.Button || control == ControlType.ListItem) &&
                (className.Contains("TaskList", StringComparison.OrdinalIgnoreCase) ||
                 node.Cached.AutomationId.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)))) found = true;
            occupiedRects.Add(occupied);
        }
        return found ? LargestFreeArea(available, occupiedRects, layout.Vertical, far) : null;
    }

    internal static Rectangle LargestFreeArea(Rectangle available, IReadOnlyList<Rectangle> occupied, bool vertical, bool far)
    {
        int Start(Rectangle r) => vertical ? r.Top : r.Left;
        int End(Rectangle r) => vertical ? r.Bottom : r.Right;
        int cursor = Start(available), bestStart = cursor, bestLength = 0;
        void ConsiderGap(int end)
        {
            int length = end - cursor;
            if (length > bestLength || (far && length == bestLength))
            {
                bestStart = cursor;
                bestLength = length;
            }
        }
        foreach (var rect in occupied.OrderBy(Start))
        {
            ConsiderGap(Start(rect));
            cursor = Math.Max(cursor, End(rect));
        }
        ConsiderGap(End(available));
        if (bestLength == 0) return Rectangle.Empty;
        return vertical ? new Rectangle(available.Left, bestStart, available.Width, bestLength)
                        : new Rectangle(bestStart, available.Top, bestLength, available.Height);
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
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
