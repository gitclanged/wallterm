using System.Runtime.InteropServices;

namespace WallTerm;

/// <summary>
/// Finds the part of the work area that other windows leave uncovered, so the terminal can shrink into it instead of
/// hiding under them. A terminal grid is rectangular, so the result is the largest rectangle that no avoided window
/// overlaps.
/// </summary>
internal static class FreeArea
{
    /// <summary>Edges within this many pixels of the work area count as touching it (snap gaps, rounding).</summary>
    private const int SnapTolerance = 12;

    // Shell surfaces that are visible top-level windows but never "windows" to the user.
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.Internal.Shell.TabProxyWindow",
    };

    /// <param name="mode">Snapped or All (anything else returns the whole work area).</param>
    /// <param name="self">Our own window, skipped.</param>
    /// <param name="desktop">Enumeration stops here: windows below the desktop (Show Desktop) are hidden anyway.</param>
    public static Native.RECT Compute(Native.RECT work, string mode, IntPtr self, IntPtr desktop)
    {
        if (mode is not ("Snapped" or "All")) return work;
        bool snappedOnly = mode == "Snapped";
        long workArea = Area(work);
        var obstacles = new List<Native.RECT>();

        Native.EnumWindows((hwnd, _) =>
        {
            if (hwnd == desktop) return false;
            if (hwnd == self || !IsCandidate(hwnd)) return true;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT frame, Marshal.SizeOf<Native.RECT>()) != 0)
                return true;
            var r = Intersect(frame, work);
            // Too small to matter, or (nearly) the whole screen: there is no room to make either way.
            if (r.Right - r.Left < 48 || r.Bottom - r.Top < 48 || Area(r) * 10 >= workArea * 9) return true;
            if (snappedOnly && !IsSnapped(frame, work)) return true;
            obstacles.Add(r);
            return true;
        }, IntPtr.Zero);

        if (obstacles.Count == 0) return work;
        var best = LargestEmpty(work, obstacles);
        // Only slivers left (e.g. two windows snapped side by side): covering the screen beats a uselessly tiny terminal.
        return Area(best) * 8 < workArea ? work : best;
    }

    private static bool IsCandidate(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.IsZoomed(hwnd)) return false;
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        // Topmost covers the taskbar, Start, notifications and overlays; tool windows are palettes and helper popups.
        if ((ex & (Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT)) != 0) return false;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return !IgnoredClasses.Contains(Native.GetClassName(hwnd));
    }

    /// <summary>
    /// Snapped (Snap, Snap layouts, FancyZones-style tiling): spans the full height or width of the work area, or sits
    /// in one of its corners.
    /// </summary>
    private static bool IsSnapped(Native.RECT f, Native.RECT w)
    {
        bool left = Math.Abs(f.Left - w.Left) <= SnapTolerance, right = Math.Abs(f.Right - w.Right) <= SnapTolerance;
        bool top = Math.Abs(f.Top - w.Top) <= SnapTolerance, bottom = Math.Abs(f.Bottom - w.Bottom) <= SnapTolerance;
        return (top && bottom) || (left && right) || ((left || right) && (top || bottom));
    }

    /// <summary>
    /// Largest rectangle inside <paramref name="area"/> that overlaps none of <paramref name="obstacles"/>. Its left and
    /// right edges lie on obstacle or area edges, so trying every such pair of columns and taking the tallest free run
    /// in each is exact: O(n² · n log n), trivial for the handful of windows on screen.
    /// </summary>
    internal static Native.RECT LargestEmpty(Native.RECT area, List<Native.RECT> obstacles)
    {
        var xs = obstacles.SelectMany(o => new[] { o.Left, o.Right }).Append(area.Left).Append(area.Right)
            .Where(x => x >= area.Left && x <= area.Right).Distinct().Order().ToArray();
        var best = new Native.RECT();
        long bestArea = 0;
        var spans = new List<(int Top, int Bottom)>();
        for (int i = 0; i < xs.Length; i++)
        for (int j = i + 1; j < xs.Length; j++)
        {
            int x1 = xs[i], x2 = xs[j];
            long width = x2 - x1;
            if (width * (area.Bottom - area.Top) <= bestArea) continue;
            spans.Clear();
            foreach (var o in obstacles)
                if (o.Left < x2 && o.Right > x1) spans.Add((o.Top, o.Bottom));
            spans.Sort();
            int y = area.Top;
            foreach (var (top, bottom) in spans.Append((area.Bottom, area.Bottom)))
            {
                if (top > y && width * (top - y) > bestArea)
                {
                    bestArea = width * (top - y);
                    best = new Native.RECT { Left = x1, Top = y, Right = x2, Bottom = top };
                }
                y = Math.Max(y, bottom);
            }
        }
        return best;
    }

    private static Native.RECT Intersect(Native.RECT a, Native.RECT b) => new()
    {
        Left = Math.Max(a.Left, b.Left), Top = Math.Max(a.Top, b.Top),
        Right = Math.Min(a.Right, b.Right), Bottom = Math.Min(a.Bottom, b.Bottom),
    };

    private static long Area(Native.RECT r) => Math.Max(0L, r.Right - r.Left) * Math.Max(0L, r.Bottom - r.Top);
}
