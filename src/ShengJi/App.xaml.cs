using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ShengJi.Audio;
using ShengJi.Core;
using ShengJi.Interop;
using ShengJi.UI;

namespace ShengJi
{
    public partial class App : Application
    {
        public bool StartMinimized { get; set; }
        /// <summary>开发用：把界面渲染成图片后退出。</summary>
        public string UiShotDir { get; set; }

        internal AppController Controller { get; private set; }
        public bool IsExiting { get; private set; }

        private MainWindow _main;
        private MiniWindow _mini;
        private TrayIcon _tray;
        private bool _inMini;
        private DispatcherTimer _trayTimer;
        private EventWaitHandle _activateEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, ex) =>
            {
                Log.Error("界面异常", ex.Exception);
                ex.Handled = true;
            };

            var settings = AppSettings.Load();
            if (UiShotDir != null) UiShot.PrepareSettings(settings, UiShotDir);

            Controller = new AppController(settings);
            Controller.Notice += Notify;
            Controller.Initialize();

            _tray = new TrayIcon();
            _tray.ShowRequested += ShowMain;
            _tray.RecordRequested += () => Controller.ToggleRecord();
            _tray.PauseRequested += () => Controller.TogglePause();
            _tray.FolderRequested += () => SystemIntegration.OpenFolder(Controller.Settings.EffectiveOutputDir);
            _tray.ExitRequested += RequestExit;
            _trayTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(125) };
            _trayTimer.Tick += (s, ev) => UpdateTray();
            _trayTimer.Start();
            Controller.StateChanged += UpdateTray;

            _main = new MainWindow(Controller, this);
            _main.Closing += (s, ev) =>
            {
                // Alt+F4 等方式关闭窗口：同样只是收到托盘
                if (!IsExiting) { ev.Cancel = true; HideMainToTray(); }
            };
            _mini = new MiniWindow(Controller, this);
            _mini.Closing += (s, ev) => { if (!IsExiting) { ev.Cancel = true; _mini.Hide(); _inMini = false; } };

            if (!StartMinimized) _main.Show();
            StartActivationListener();
            SessionEnding += OnSessionEnding;

            if (UiShotDir != null) UiShot.Run(this, _main, _mini, UiShotDir);
        }

        private void UpdateTray()
        {
            if (_tray == null) return;
            _tray.Update(Controller.State, Controller.SysMeter.Level, Controller.MicMeter.Level,
                         Controller.Elapsed, Controller.Settings.HotkeyToggle);
        }

        // ------------------------------------------------------------------
        //  提示：窗口可见时用窗口内提示条，否则重要消息用托盘气泡
        // ------------------------------------------------------------------

        internal void Notify(string msg, NoticeKind kind)
        {
            bool mainVisible = _main != null && _main.IsVisible && _main.WindowState != WindowState.Minimized;
            if (mainVisible) _main.ShowToast(msg, kind);
            else if (kind != NoticeKind.Info) _tray?.ShowBalloon("声记", msg, kind == NoticeKind.Warn);
        }

        // ------------------------------------------------------------------
        //  窗口切换
        // ------------------------------------------------------------------

        public void ShowMain()
        {
            if (IsExiting) return;
            if (_inMini) { _mini.Hide(); _inMini = false; }
            if (!_main.IsVisible) _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            _main.Topmost = true;
            _main.Topmost = false;
            var h = new WindowInteropHelper(_main).Handle;
            if (h != IntPtr.Zero) Native.SetForegroundWindow(h);
            _main.RefreshRecent();
        }

        public void HideMainToTray()
        {
            _main.PrepareForHide();
            _main.Hide();
            if (!Controller.Settings.TrayTipShown)
            {
                Controller.Settings.TrayTipShown = true;
                Controller.Settings.Save();
                _tray.ShowBalloon("声记仍在运行",
                    "右键托盘图标可退出", false);
            }
        }

        public void EnterMini()
        {
            _main.PrepareForHide();
            _main.Hide();
            _mini.Show();
            _mini.Activate();
            _inMini = true;
        }

        public void ExitMini()
        {
            _mini.Hide();
            _inMini = false;
            ShowMain();
        }

        // ------------------------------------------------------------------
        //  退出
        // ------------------------------------------------------------------

        public void RequestExit()
        {
            if (IsExiting) return;
            if (Controller.IsRecording)
            {
                var owner = _main.IsVisible ? (Window)_main : _inMini ? _mini : null;
                bool ok = ConfirmDialog.Ask(owner, "正在录音",
                    $"退出前会停止并保存录音（已录 {UI.MainWindow.FormatDuration(Controller.Elapsed)}）。",
                    "保存并退出", "取消", true);
                if (!ok) return;
            }
            ExitNow();
        }

        internal void ExitNow()
        {
            IsExiting = true;
            Log.Info("退出程序");
            if (!Controller.StopAndWait(6000)) Log.Warn("退出时录音未能在时限内保存完毕");
            _trayTimer?.Stop();
            try { _main?.PrepareForHide(); } catch { }
            Controller.Shutdown();
            _tray?.Dispose();
            _tray = null;
            try { _activateEvent?.Set(); } catch { }
            Log.Flush();
            Shutdown();
        }

        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            // Windows 注销/关机：保存正在进行的录音
            Log.Info("系统会话结束: " + e.ReasonSessionEnding);
            if (!IsExiting) ExitNow();
        }

        // ------------------------------------------------------------------
        //  第二个实例启动时唤出主窗口
        // ------------------------------------------------------------------

        private void StartActivationListener()
        {
            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
                var t = new Thread(() =>
                {
                    while (!IsExiting)
                    {
                        if (_activateEvent.WaitOne(1000) && !IsExiting)
                            Dispatcher.BeginInvoke(new Action(ShowMain));
                    }
                }) { IsBackground = true, Name = "ActivationListener" };
                t.Start();
            }
            catch (Exception ex) { Log.Warn("无法创建激活事件: " + ex.Message); }
        }
    }
}
