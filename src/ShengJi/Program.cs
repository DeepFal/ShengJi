using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using ShengJi.Core;

namespace ShengJi
{
    internal static class Program
    {
        private const string MutexName = "Local\\ShengJi.SingleInstance.v1";
        public const string ActivateEventName = "Local\\ShengJi.Activate.v1";

        [STAThread]
        private static int Main(string[] args)
        {
            // 自检辅助：播放一个 WAV 后退出（用来模拟“某个正在发声的应用”）
            int tone = Array.IndexOf(args, "--play-tone");
            if (tone >= 0 && tone + 1 < args.Length)
            {
                using (var p = new System.Media.SoundPlayer(args[tone + 1])) p.PlaySync();
                return 0;
            }

            // 隐藏的自检模式：验证音频链路（开发/排障用）
            int st = Array.IndexOf(args, "--selftest");
            if (st >= 0)
            {
                string dir = st + 1 < args.Length ? args[st + 1] : Path.Combine(Path.GetTempPath(), "ShengJiSelfTest");
                return SelfTest.Run(dir, args.Contains("--tone"));
            }

            // 开发用：界面截图模式（不占用单实例锁，不保存设置）
            int shot = Array.IndexOf(args, "--ui-shot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                var shotApp = new App { UiShotDir = args[shot + 1] };
                shotApp.InitializeComponent();
                return shotApp.Run();
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // 已经在运行：通知它显示主窗口
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(ActivateEventName)) ev.Set();
                    }
                    catch { }
                    return 0;
                }

                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    Log.Error("未处理的异常", e.ExceptionObject as Exception);

                Log.Info($"启动 声记 {typeof(Program).Assembly.GetName().Version} " +
                         $"Windows {Interop.Native.OSVersion} {(Paths.IsPortable ? "便携版" : "安装版")} " +
                         $"{(Environment.Is64BitProcess ? "x64" : "x86")}");

                var app = new App { StartMinimized = args.Contains("--minimized") };
                app.InitializeComponent();
                int code = app.Run();
                GC.KeepAlive(mutex);
                return code;
            }
        }
    }
}
