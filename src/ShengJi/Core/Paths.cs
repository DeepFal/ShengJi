using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace ShengJi.Core
{
    /// <summary>
    /// 程序路径。
    /// 便携版：exe 同目录下存在 "portable" 标记文件（或 Data 文件夹）时，设置和日志都放在 exe 旁边的 Data 文件夹里；
    /// 安装版：放在 %APPDATA%\ShengJi。
    /// </summary>
    internal static class Paths
    {
        public static readonly string ExePath = Assembly.GetEntryAssembly()?.Location ?? "";
        public static readonly string ExeDir = Path.GetDirectoryName(ExePath) ?? AppDomain.CurrentDomain.BaseDirectory;

        public static readonly bool IsPortable =
            File.Exists(Path.Combine(ExeDir, "portable")) || File.Exists(Path.Combine(ExeDir, "portable.txt"));

        public static readonly string DataDir = IsPortable
            ? Path.Combine(ExeDir, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShengJi");

        public static string SettingsFile => Path.Combine(DataDir, "settings.xml");
        public static string LogFile => Path.Combine(DataDir, "log.txt");

        public static string DefaultOutputDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "声记录音");

        public static void EnsureDataDir()
        {
            try { Directory.CreateDirectory(DataDir); } catch { }
        }
    }

    /// <summary>简单的文件日志（超过 1MB 时轮转一份旧日志）。</summary>
    /// <summary>
    /// 文件日志：调用方只把消息放进队列，由后台线程写盘（音频线程不会被磁盘 IO 卡住）。
    /// 超过 1MB 时轮转为 log.txt.old（运行期间也会检查）。
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 1024 * 1024;
        private const int MaxQueue = 5000;
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _queue =
            new System.Collections.Concurrent.ConcurrentQueue<string>();
        private static readonly System.Threading.AutoResetEvent _signal = new System.Threading.AutoResetEvent(false);
        private static readonly System.Threading.ManualResetEvent _idle = new System.Threading.ManualResetEvent(true);
        private static readonly object _startLock = new object();
        private static System.Threading.Thread _thread;
        private static int _dropped;

        private static void Write(string level, string msg)
        {
            EnsureThread();
            if (_queue.Count > MaxQueue) { System.Threading.Interlocked.Increment(ref _dropped); return; }
            _queue.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}");
            _idle.Reset();
            _signal.Set();
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[{level}] {msg}");
#endif
        }

        private static void EnsureThread()
        {
            if (_thread != null) return;
            lock (_startLock)
            {
                if (_thread != null) return;
                _thread = new System.Threading.Thread(Loop) { IsBackground = true, Name = "Log", Priority = System.Threading.ThreadPriority.BelowNormal };
                _thread.Start();
            }
        }

        private static void Loop()
        {
            Paths.EnsureDataDir();
            var sb = new StringBuilder();
            while (true)
            {
                _signal.WaitOne(1000);
                sb.Clear();
                while (_queue.TryDequeue(out var line)) sb.AppendLine(line);
                int dropped = System.Threading.Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0) sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN] 日志过多，丢弃了 {dropped} 条");
                if (sb.Length > 0)
                {
                    try
                    {
                        RotateIfNeeded();
                        File.AppendAllText(Paths.LogFile, sb.ToString(), Encoding.UTF8);
                    }
                    catch { }
                }
                if (_queue.IsEmpty) _idle.Set();
            }
        }

        private static void RotateIfNeeded()
        {
            var fi = new FileInfo(Paths.LogFile);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            string old = Paths.LogFile + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(Paths.LogFile, old);
        }

        /// <summary>等待队列写完（退出前调用）。</summary>
        public static void Flush(int timeoutMs = 2000)
        {
            if (_thread == null) return;
            _signal.Set();
            _idle.WaitOne(timeoutMs);
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", ex == null ? msg : msg + ": " + ex);
    }
}
