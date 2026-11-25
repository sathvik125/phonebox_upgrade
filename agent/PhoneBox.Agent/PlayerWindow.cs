#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// The window of the app that plays the current media session. The session names its app by an AppUserModelID
/// ("Chrome", "Brave.XYZ", "308046B0AF4A39CB" for Firefox, "Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo") or an
/// exe name ("Spotify.exe"). Among the visible top-level windows, one whose own AppUserModelID or process matches wins;
/// a window whose title holds the track title (a browser shows the playing tab's title) beats the others, then the larger.
/// </summary>
internal static class PlayerWindow
{
    public static nint Find(string appId, string? title)
    {
        var id = appId.Trim();
        var idLower = id.ToLowerInvariant();
        var exeHint = idLower.EndsWith(".exe") ? idLower[..^4] : idLower.Split('!')[0].Split('_')[0].Split('.')[0];
        var aliases = Aliases(idLower);
        var wanted = (title ?? "").Trim();
        nint best = 0; long bestScore = 0;
        var self = Environment.ProcessId;
        var names = new Dictionary<uint, string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || GetWindow(hwnd, GW_OWNER) != 0 || Cloaked(hwnd)) return true;
            if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left < 160 || r.Bottom - r.Top < 120) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;
            if (!names.TryGetValue(pid, out var exe))
            {
                try { using var p = Process.GetProcessById((int)pid); exe = p.ProcessName.ToLowerInvariant(); } catch { exe = ""; }
                names[pid] = exe;
            }
            var own = WindowAppId(hwnd)?.ToLowerInvariant();
            long score = 0;
            if (own is not null && (own == idLower || idLower.StartsWith(own) || own.StartsWith(idLower))) score = 4;
            else if (exe.Length > 0 && (exe == exeHint || aliases.Contains(exe) || idLower.Contains(exe))) score = 3;
            if (score == 0) return true;
            var text = WindowText(hwnd);
            if (wanted.Length > 0 && text.Contains(wanted, StringComparison.OrdinalIgnoreCase)) score += 4;
            score = score * 100_000_000L + (long)(r.Right - r.Left) * (r.Bottom - r.Top) / 100;
            if (score > bestScore) { bestScore = score; best = hwnd; }
            return true;
        }, 0);
        return best;
    }

    /// <summary>
    /// The part of the window that shows the page, for a Chromium browser (Brave, Chrome, Edge, Opera, Vivaldi): its
    /// page area as a share of the window's captured frame (left, top, width, height, each 0..1), so the phone gets the
    /// tab's content without the browser's tabs and toolbar. Null for other apps, or when the page fills the window.
    /// </summary>
    public static (double X, double Y, double W, double H)? ContentArea(nint hwnd)
    {
        // physical pixels for both rectangles: the frame bounds always are, GetWindowRect only in a DPI-aware thread
        var previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try { return ContentAreaCore(hwnd); }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    private static (double X, double Y, double W, double H)? ContentAreaCore(nint hwnd)
    {
        if (DwmGetWindowRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) != 0) return null;
        double fw = frame.Right - frame.Left, fh = frame.Bottom - frame.Top;
        if (fw < 50 || fh < 50) return null;
        RECT best = default; long bestArea = 0;
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (!IsWindowVisible(child) || ClassName(child) != "Chrome_RenderWidgetHostHWND") return true;
            if (!GetWindowRect(child, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = r; }
            return true;
        }, 0);
        if (bestArea == 0) return null;
        var x = Math.Clamp((best.Left - frame.Left) / fw, 0, 1);
        var y = Math.Clamp((best.Top - frame.Top) / fh, 0, 1);
        var w = Math.Clamp((best.Right - best.Left) / fw, 0, 1 - x);
        var h = Math.Clamp((best.Bottom - best.Top) / fh, 0, 1 - y);
        if (w < 0.2 || h < 0.2) return null; // a tiny page area is not the tab (a popup, a side panel)
        if (x < 0.005 && y < 0.005 && w > 0.99 && h > 0.99) return null; // full screen already
        return (x, y, w, h);
    }

    private static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>Exe names that play under an AppUserModelID that doesn't name them.</summary>
    private static HashSet<string> Aliases(string id)
    {
        var set = new HashSet<string>();
        if (id.Contains("308046b0af4a39cb") || id.Contains("firefox")) set.Add("firefox");
        if (id.Contains("msedge") || id == "microsoftedge") set.Add("msedge");
        if (id.Contains("chrome")) set.Add("chrome");
        if (id.Contains("brave")) set.Add("brave");
        if (id.Contains("opera")) set.Add("opera");
        if (id.Contains("vivaldi")) set.Add("vivaldi");
        if (id.Contains("zunevideo") || id.Contains("zunemusic") || id.Contains("media")) { set.Add("video.ui"); set.Add("microsoft.media.player"); set.Add("applicationframehost"); }
        if (id.Contains("vlc")) set.Add("vlc");
        return set;
    }

    private static string WindowText(nint hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool Cloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The AppUserModelID set on the window itself (UWP apps, browsers with their own), if any.</summary>
    private static string? WindowAppId(nint hwnd)
    {
        if (SHGetPropertyStoreForWindow(hwnd, IID_IPropertyStore, out var store) != 0 || store == 0) return null;
        try
        {
            var key = PKEY_AppUserModel_ID;
            var value = new PROPVARIANT();
            unsafe
            {
                // IPropertyStore::GetValue is the sixth method: IUnknown's three, GetCount, GetAt, GetValue
                var getValue = (delegate* unmanaged[Stdcall]<nint, PROPERTYKEY*, PROPVARIANT*, int>)(*(void***)store)[5];
                if (getValue(store, &key, &value) != 0) return null;
            }
            try { return value.vt == VT_LPWSTR && value.p != 0 ? Marshal.PtrToStringUni(value.p) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.Release(store); }
    }

    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const ushort VT_LPWSTR = 31;
    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public nint p; public nint p2; }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowRect(nint hwnd, int attribute, out RECT value, int size);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);
    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int max);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint hwnd, in Guid iid, out nint store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT value);
}
#endif
