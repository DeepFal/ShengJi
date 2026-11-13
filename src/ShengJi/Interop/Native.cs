using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ShengJi.Interop
{
    /// <summary>kernel32 / user32 / dwmapi / ntdll 等系统 API。</summary>
    internal static class Native
    {
        // ---------------- 系统版本 ----------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OSVERSIONINFOEXW
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OSVERSIONINFOEXW info);

        private static Version _osVersion;

        /// <summary>真实系统版本（不受兼容性清单影响）。</summary>
        public static Version OSVersion
        {
            get
            {
                if (_osVersion != null) return _osVersion;
                try
                {
                    var v = new OSVERSIONINFOEXW { dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEXW)) };
                    if (RtlGetVersion(ref v) == 0)
                        return _osVersion = new Version(v.dwMajorVersion, v.dwMinorVersion, v.dwBuildNumber);
                }
                catch { }
                return _osVersion = Environment.OSVersion.Version;
            }
        }

        /// <summary>按应用录制（进程回环）需要 Windows 10 2004（19041）及以上。</summary>
        public static bool SupportsProcessLoopback
        {
            get { var v = OSVersion; return v.Major > 10 || (v.Major == 10 && v.Build >= 19041); }
        }

        public static bool IsWindows11 { get { var v = OSVersion; return v.Major >= 10 && v.Build >= 22000; } }

        // ---------------- 进程 ----------------
        public const int TH32CS_SNAPPROCESS = 0x2;
        public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public int dwSize;
            public int cntUsage;
            public int th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public int th32ModuleID;
            public int cntThreads;
            public int th32ParentProcessID;
            public int pcPriClassBase;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(int flags, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder exeName, ref int size);

        public static string GetProcessImagePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        // ---------------- 磁盘空间（同时支持 UNC 网络路径） ----------------
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeBytesAvailable,
                                                       out ulong totalBytes, out ulong totalFreeBytes);

        /// <summary>目录所在磁盘对当前用户可用的剩余空间；获取失败返回 null。</summary>
        public static long? GetFreeSpace(string dir)
        {
            try
            {
                string d = System.IO.Path.GetFullPath(dir);
                // 目录可能还不存在：向上找到一个存在的父目录
                while (!string.IsNullOrEmpty(d) && !System.IO.Directory.Exists(d))
                    d = System.IO.Path.GetDirectoryName(d);
                if (string.IsNullOrEmpty(d)) return null;
                if (!d.EndsWith("\\")) d += "\\";
                return GetDiskFreeSpaceExW(d, out ulong free, out _, out _) ? (long)Math.Min(free, (ulong)long.MaxValue) : (long?)null;
            }
            catch { return null; }
        }

        // ---------------- 电源 ----------------
        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint flags);

        // ---------------- 多媒体线程优先级 ----------------
        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref int taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        // ---------------- 事件 ----------------
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, IntPtr name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

        // ---------------- 全局快捷键 ----------------
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        // ---------------- 图标 ----------------
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        // ---------------- 窗口 ----------------
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        public const int SW_RESTORE = 9;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWCP_ROUND = 2;
        public const int DWMWCP_ROUNDSMALL = 3;

        public static void TrySetDwm(IntPtr hwnd, int attr, int value)
        {
            try { DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)); } catch { }
        }

        /// <summary>COLORREF = 0x00BBGGRR</summary>
        public static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
    }
}
