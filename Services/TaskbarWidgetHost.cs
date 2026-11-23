using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using VibeMeters.Models;
using Forms = System.Windows.Forms;

namespace VibeMeters.Services;

/// <summary>Positions independent, nonactivating windows in screen pixels. Explorer is only queried.</summary>
internal sealed class TaskbarWidgetHost : IDisposable
{
    private readonly Action _openMain;
    private readonly Dictionary<UsageProviderKind, TaskbarWidgetWindow> _views = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Layout? _lastLayout;
    private Rectangle _free;
    private bool _scanning;
    private DateTime _nextScan;
    private int _generation;

    public TaskbarWidgetHost(Action openMain)
    {
        _openMain = openMain;
        _timer.Tick += (_, _) => Place();
    }

    public void Update(AppSettings settings, IReadOnlyDictionary<UsageProviderKind, UsageSnapshot> selectedWidgets)
    {
        if (selectedWidgets.Count == 0) { Clear(); return; }
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
                    _views.Remove(kind);
                    if (_views.Count == 0) _timer.Stop();
                };
                NonActivatingWindow(new WindowInteropHelper(view).EnsureHandle());
            }
            view.Update([snapshot], settings);
        }
        _timer.Start();
        Place();
    }

    public void Clear()
    {
        _timer.Stop();
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
            _free = Rectangle.Empty;
            _nextScan = default;
            _generation++;
        }
        if (!_scanning && DateTime.UtcNow >= _nextScan) _ = FindFreeAreaAsync(layout);
        var (bar, monitor, work, vertical, far, scale) = (layout.Bar, layout.Monitor, layout.Work, layout.Vertical, layout.Far, layout.Scale);
        int gap = Math.Max(1, (int)Math.Round(2 * scale));
        var windows = _views.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToArray();
        var sizes = new List<Size>();
        var now = DateTimeOffset.Now;
        foreach (var view in windows)
        {
            view.RefreshCountdowns(now);
            view.SetLayout(vertical, Math.Max(96, (vertical ? bar.Width : monitor.Width) / scale),
                Math.Max(1, work.Height / scale));
            sizes.Add(new Size((int)Math.Ceiling(view.Width * scale), (int)Math.Ceiling(view.Height * scale)));
        }
        var length = sizes.Sum(s => vertical ? s.Height : s.Width) + gap * (sizes.Count - 1);
        var depth = sizes.Max(s => vertical ? s.Width : s.Height);
        bool inside = vertical
            ? _free.Height >= length + 2 * gap && _free.Width >= depth
            : _free.Width >= length + 2 * gap && _free.Height >= depth;
        var area = inside ? _free : work;
        // Some custom/auto-hidden taskbars do not reserve a working area. Explicitly exclude their rectangle.
        var nearEdge = vertical ? bar.Left < monitor.Left + monitor.Width / 2 : bar.Top < monitor.Top + monitor.Height / 2;
        if (!inside)
        {
            area = vertical
                ? (nearEdge ? Rectangle.FromLTRB(Math.Max(work.Left, bar.Right), work.Top, work.Right, work.Bottom)
                            : Rectangle.FromLTRB(work.Left, work.Top, Math.Min(work.Right, bar.Left), work.Bottom))
                : (nearEdge ? Rectangle.FromLTRB(work.Left, Math.Max(work.Top, bar.Bottom), work.Right, work.Bottom)
                            : Rectangle.FromLTRB(work.Left, work.Top, work.Right, Math.Min(work.Bottom, bar.Top)));
        }
        int axisStart = vertical ? area.Top : area.Left;
        int axisLength = vertical ? area.Height : area.Width;
        int crossStart = vertical ? area.Left : area.Top;
        int crossEnd = vertical ? area.Right : area.Bottom;
        int inset = gap;
        for (int first = 0; first < windows.Length;)
        {
            int next = first, lineLength = 0, lineDepth = 0;
            while (next < sizes.Count)
            {
                var size = sizes[next];
                int tileLength = vertical ? size.Height : size.Width;
                int candidate = lineLength + (next == first ? 0 : gap) + tileLength;
                if (next > first && candidate + 2 * gap > axisLength) break;
                lineLength = candidate;
                lineDepth = Math.Max(lineDepth, vertical ? size.Width : size.Height);
                next++;
            }
            int cursor = far ? Math.Max(axisStart, axisStart + axisLength - gap - lineLength) : axisStart + gap;
            for (int i = first; i < next; i++)
            {
                var size = sizes[i];
                int crossSize = vertical ? size.Width : size.Height;
                int cross = inside ? crossStart + (crossEnd - crossStart - crossSize) / 2
                    : nearEdge ? crossStart + inset : crossEnd - crossSize - inset;
                var target = vertical ? new Rectangle(cross, cursor, size.Width, size.Height)
                                      : new Rectangle(cursor, cross, size.Width, size.Height);
                Move(windows[i], target);
                cursor += (vertical ? size.Height : size.Width) + gap;
            }
            inset += lineDepth + gap;
            first = next;
        }
    }

    private static void Move(TaskbarWidgetWindow view, Rectangle target)
    {
        var hwnd = new WindowInteropHelper(view).Handle;
        bool show = !view.IsVisible;
        if (!show && Bounds(hwnd) == target)
        {
            // Explorer can raise its topmost taskbar when the user interacts with it.
            // Only reclaim our position if the taskbar itself now covers this tile.
            var center = new Point(target.Left + target.Width / 2, target.Top + target.Height / 2);
            var coveringWindow = GetAncestor(WindowFromPoint(center), 2);
            if (coveringWindow == hwnd || coveringWindow != FindWindow("Shell_TrayWnd", null)) return;
        }
        const uint noActivate = 0x0010;
        SetWindowPos(hwnd, new IntPtr(-1), target.X, target.Y, target.Width, target.Height, noActivate);
        if (show)
        {
            view.Show();
            SetWindowPos(hwnd, new IntPtr(-1), target.X, target.Y, target.Width, target.Height, noActivate);
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
            if (_generation == generation && _views.Count > 0 && area != _free)
            {
                _free = area;
                Place();
            }
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException or UnauthorizedAccessException)
        { if (_generation == generation) _free = Rectangle.Empty; }
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
        // live in the XAML tree. Inspect it before using the adjacent work-area fallback.
        return ReadFreeArea(layout, IntPtr.Zero);
    }

    private static Rectangle ReadFreeArea(Layout layout, IntPtr list)
    {
        bool modern = list == IntPtr.Zero;
        var available = modern ? layout.Bar : Rectangle.Intersect(Bounds(list), layout.Bar);
        var root = AutomationElement.FromHandle(modern ? layout.Taskbar : list);
        var cached = new CacheRequest();
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
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
