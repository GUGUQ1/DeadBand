using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DeadBand
{
    /// <summary>一台物理显示器（屏幕 + 它的硬件标识）。</summary>
    internal sealed class MonitorInfo
    {
        public string ScreenDevice;   // "\\.\DISPLAY5"
        public string MonitorId;      // "MONITOR\HKC1234\{guid}\0001" —— 热插拔靠它认人
        public string Description;    // "HKC T2752Q"
    }

    internal static class Win32
    {
        // ---------------- DPI ----------------
        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        // ---------------- 显示器枚举 ----------------
        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        private const int EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum,
            ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        public static List<MonitorInfo> GetMonitors()
        {
            var list = new List<MonitorInfo>();
            uint i = 0;
            while (true)
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                i++;

                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

                var mi = new MonitorInfo();
                mi.ScreenDevice = dd.DeviceName;
                mi.Description = dd.DeviceString;
                mi.MonitorId = dd.DeviceID;

                var mon = new DISPLAY_DEVICE();
                mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (EnumDisplayDevices(dd.DeviceName, 0, ref mon, EDD_GET_DEVICE_INTERFACE_NAME))
                {
                    if (!string.IsNullOrEmpty(mon.DeviceID)) mi.MonitorId = mon.DeviceID;
                    if (!string.IsNullOrEmpty(mon.DeviceString)) mi.Description = mon.DeviceString;
                }
                list.Add(mi);
            }
            return list;
        }

        /// <summary>
        /// 把显示器 id 归一成「硬件型号段」，用来做热插拔识别。
        /// 两种常见格式都要能吃下：
        ///   MONITOR\HKC1234\{guid}\0001
        ///   \\?\DISPLAY#HKC2773#5&amp;23d2173c&amp;0&amp;UID4352#{guid}
        /// 只保留型号段，这样拔下来再插上（换了 UID / 接口路径）也能认回同一台。
        /// </summary>
        public static string NormalizeMonitorId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string[] parts = id.Split(new char[] { '\\', '#' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p == "?") continue;
                if (string.Equals(p, "DISPLAY", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(p, "MONITOR", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.StartsWith("{")) continue;
                return p.Trim().ToUpperInvariant();
            }
            return id.Trim().ToUpperInvariant();
        }

        // ---------------- 任务栏就绪检测 ----------------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        /// <summary>
        /// 任务栏窗口出现了没有。开机自启动时用来等 explorer 准备好 ——
        /// 任务栏还没建好就注册 AppBar，量到的 work area 是错的，区带会贴错位置。
        /// </summary>
        public static bool ShellTrayReady()
        {
            try
            {
                if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) return true;
                if (FindWindow("Shell_SecondaryTrayWnd", null) != IntPtr.Zero) return true;
                return false;
            }
            catch { return false; }
        }

        // ---------------- 前台窗口 ----------------
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // SetWindowPos 的 flags / 特殊 hWndInsertAfter
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
            StringBuilder lpExeName, ref int lpdwSize);

        /// <summary>窗口类名，诊断用（例如 Chrome_WidgetWin_1 / ApplicationFrameWindow）。</summary>
        public static string ClassNameOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(256);
                int n = GetClassName(hwnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString(0, n) : "";
            }
            catch { return ""; }
        }

        /// <summary>窗口所属进程的可执行文件名（不含路径），诊断用。</summary>
        public static string ProcessNameOf(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return "";
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return "";

                const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return "pid" + pid.ToString();
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "pid" + pid.ToString();
                    string full = sb.ToString(0, size);
                    int i = full.LastIndexOf('\\');
                    return i >= 0 ? full.Substring(i + 1) : full;
                }
                finally { CloseHandle(h); }
            }
            catch { return ""; }
        }

        // ---------------- AppBar ----------------
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public int lParam;
        }

        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        public const uint ABM_NEW = 0x00;
        public const uint ABM_REMOVE = 0x01;
        public const uint ABM_QUERYPOS = 0x02;
        public const uint ABM_SETPOS = 0x03;
        public const uint ABE_BOTTOM = 3;
        public const uint ABN_POSCHANGED = 1;
        public const uint ABN_FULLSCREENAPP = 2;
        public const uint ABN_WINDOWARRANGE = 3;

        // ---------------- 屏幕取色 ----------------
        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern uint GetPixel(IntPtr hdc, int x, int y);
    }
}
