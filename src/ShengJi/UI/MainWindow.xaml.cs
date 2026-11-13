using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ShengJi.Audio;
using ShengJi.Core;
using ShengJi.Interop;
using ShengJi.Recording;

namespace ShengJi.UI
{
    public partial class MainWindow : Window
    {
        private readonly AppController _c;
        private readonly App _app;
        private bool _loading = true;
        private readonly DispatcherTimer _toastTimer;
        private readonly DispatcherTimer _slowTimer;
        private string _lastClock;
        private RecState? _shownState;
        private IntPtr _hwnd;

        internal MainWindow(AppController controller, App app)
        {
            _c = controller;
            _app = app;
            InitializeComponent();

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _toastTimer.Tick += (s, e) => HideToast();
            _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _slowTimer.Tick += (s, e) => UpdateStatusBar();
            _slowTimer.Start();

            LoadSettingsToUi();
            WireEvents();
            RestorePosition();

            UpdatePickers();
            UpdateHints();
            UpdateRecordUi();
            UpdateStatusBar();
            RefreshRecent();
            _loading = false;

            // 后台预读一次设备/应用列表，拿到所选应用的图标
            _ = PreloadCatalogAsync();
        }

        private Brush Res(string key) => (Brush)FindResource(key);

        // ==================================================================
        //  窗口
        // ==================================================================

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.TrySetDwm(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            Native.TrySetDwm(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
            UpdateBorderColor();
        }

        private void UpdateBorderColor()
        {
            if (_hwnd == IntPtr.Zero) return;
            bool rec = _c.State == RecState.Recording;
            Native.TrySetDwm(_hwnd, Native.DWMWA_BORDER_COLOR,
                rec ? Native.ColorRef(0xB8, 0x3A, 0x46) : Native.ColorRef(0x3A, 0x3E, 0x47));
        }

        private void RestorePosition()
        {
            var s = _c.Settings;
            var wa = SystemParameters.WorkArea;
            if (!double.IsNaN(s.MainLeft) && !double.IsNaN(s.MainTop) &&
                s.MainLeft > SystemParameters.VirtualScreenLeft - 50 &&
                s.MainLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                s.MainTop > SystemParameters.VirtualScreenTop - 10 &&
                s.MainTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
            {
                Left = s.MainLeft; Top = s.MainTop;
            }
            else
            {
                Left = wa.Left + (wa.Width - Width) / 2;
                Top = Math.Max(wa.Top + 20, wa.Top + (wa.Height - 760) / 2);
            }
        }

        public void SavePosition()
        {
            if (WindowState != WindowState.Normal || !IsLoaded) return;
            _c.Settings.MainLeft = Left;
            _c.Settings.MainTop = Top;
        }

        // ==================================================================
        //  事件绑定
        // ==================================================================

        private void WireEvents()
        {
            BtnClose.Click += (s, e) => _app.HideMainToTray();
            BtnMinimize.Click += (s, e) => WindowState = WindowState.Minimized;
            BtnMini.Click += (s, e) => _app.EnterMini();
            BtnSettings.Click += (s, e) => ShowSettings(true);
            BtnSettingsBack.Click += (s, e) => ShowSettings(false);
            BtnRecord.Click += (s, e) => _c.ToggleRecord();
            BtnPause.Click += (s, e) => _c.TogglePause();
            BtnFolder.Click += (s, e) => SystemIntegration.OpenFolder(_c.Settings.EffectiveOutputDir);
            BtnDir.Click += (s, e) => SystemIntegration.OpenFolder(_c.Settings.EffectiveOutputDir);
            BtnAll.Click += (s, e) => SystemIntegration.OpenFolder(_c.Settings.EffectiveOutputDir);
            LocationChanged += (s, e) => SavePosition();
            Activated += (s, e) => { if (!_loading) RefreshRecent(); };

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (PickerPopup.IsOpen) PickerPopup.IsOpen = false;
                    else if (SettingsPanel.Visibility == Visibility.Visible) ShowSettings(false);
                }
            };

            // 开关与音量
            SysOn.Checked += (s, e) => OnEnabledChanged();
            SysOn.Unchecked += (s, e) => OnEnabledChanged();
            MicOn.Checked += (s, e) => OnEnabledChanged();
            MicOn.Unchecked += (s, e) => OnEnabledChanged();
            SysVol.ValueChanged += (s, e) => OnVolumeChanged();
            MicVol.ValueChanged += (s, e) => OnVolumeChanged();
            SysVol.PreviewMouseUp += (s, e) => _c.Settings.Save();
            MicVol.PreviewMouseUp += (s, e) => _c.Settings.Save();
            SysVol.MouseDoubleClick += (s, e) => { SysVol.Value = 100; _c.Settings.Save(); };
            MicVol.MouseDoubleClick += (s, e) => { MicVol.Value = 100; _c.Settings.Save(); };
            SysVol.PreviewMouseWheel += (s, e) => { SysVol.Value = Math.Max(0, Math.Min(200, SysVol.Value + (e.Delta > 0 ? 5 : -5))); e.Handled = true; };
            MicVol.PreviewMouseWheel += (s, e) => { MicVol.Value = Math.Max(0, Math.Min(200, MicVol.Value + (e.Delta > 0 ? 5 : -5))); e.Handled = true; };

            // 备注
            NoteBox.TextChanged += (s, e) => _c.Note = NoteBox.Text;
            NoteBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };

            // 音源选择：再点一次同一个按钮收起；点窗口其它地方 / 切走 / 拖动窗口也收起
            SysPicker.Click += (s, e) => TogglePicker(true);
            MicPicker.Click += (s, e) => TogglePicker(false);
            PickerPopup.Closed += (s, e) =>
            {
                SysPicker.Tag = null;
                MicPicker.Tag = null;
            };
            PreviewMouseDown += (s, e) =>
            {
                if (!PickerPopup.IsOpen) return;
                var src = e.OriginalSource as DependencyObject;
                if (IsWithin(src, PickerBorder) || IsWithin(src, SysPicker) || IsWithin(src, MicPicker)) return;
                PickerPopup.IsOpen = false;
            };
            Deactivated += (s, e) => PickerPopup.IsOpen = false;
            LocationChanged += (s, e) => PickerPopup.IsOpen = false;
            StateChanged += (s, e) => PickerPopup.IsOpen = false;

            // 控制器事件
            _c.StateChanged += UpdateRecordUi;
            _c.LevelsUpdated += OnLevels;
            _c.SourceStatusChanged += () => { UpdatePickers(); UpdateHints(); UpdateAlignUi(); };
            _c.RecordingSaved += OnRecordingSaved;
            _c.NoteCleared += () => { NoteBox.Text = ""; };

            WireSettings();
        }

        private void OnEnabledChanged()
        {
            if (_loading) return;
            _c.SetEnabled(SysOn.IsChecked == true, MicOn.IsChecked == true);
            UpdateHints();
        }

        private void OnVolumeChanged()
        {
            int sys = (int)Math.Round(SysVol.Value / 5) * 5;
            int mic = (int)Math.Round(MicVol.Value / 5) * 5;
            SysVolText.Text = sys + "%";
            MicVolText.Text = mic + "%";
            if (_loading) return;
            _c.SetVolumes(sys, mic);
        }

        private void OnRecordingSaved(RecordingResult r)
        {
            RefreshRecent();
            if (r.Files.Count == 0) return;
            string name = Path.GetFileName(r.Files[0]);
            if (r.Error == null)
                _app.Notify($"已保存 {name}", NoticeKind.Ok);
        }

        // ==================================================================
        //  电平 / 计时
        // ==================================================================

        private void OnLevels()
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            SysMeter.Level = _c.SysMeter.Level; SysMeter.Peak = _c.SysMeter.Peak;
            MicMeter.Level = _c.MicMeter.Level; MicMeter.Peak = _c.MicMeter.Peak;

            string clock = FormatClock(_c.Elapsed);
            if (clock != _lastClock)
            {
                _lastClock = clock;
                TimerText.Text = clock;
            }
        }

        internal static string FormatClock(TimeSpan t) =>
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

        internal static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

        // ==================================================================
        //  录音状态
        // ==================================================================

        private void UpdateRecordUi()
        {
            UpdateAlignUi();   // 录音中不能校准
            var st = _c.State;
            bool changed = _shownState != st;
            _shownState = st;

            switch (st)
            {
                case RecState.Recording:
                    Chip.Background = Res("RecSoft");
                    ChipDot.Fill = Res("Rec");
                    ChipText.Text = "录制中";
                    ChipText.Foreground = Res("RecText");
                    TimerText.Foreground = Brushes.White;
                    break;
                case RecState.Paused:
                    Chip.Background = Res("WarnSoft");
                    ChipDot.Fill = Res("Warn");
                    ChipText.Text = "已暂停";
                    ChipText.Foreground = Res("WarnText");
                    TimerText.Foreground = Res("WarnText");
                    break;
                default:
                    Chip.Background = Res("Fill2");
                    ChipDot.Fill = Res("Text3");
                    ChipText.Text = "待机";
                    ChipText.Foreground = Res("Text2");
                    TimerText.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD6));
                    TimerText.Text = _lastClock = "00:00:00";
                    break;
            }

            RecFlag.Visibility = st == RecState.Idle ? Visibility.Collapsed : Visibility.Visible;
            BtnPause.IsEnabled = st != RecState.Idle;
            PauseGlyph.Kind = st == RecState.Paused ? "play" : "pause";

            // 说明只放在鼠标悬停提示里（含快捷键）
            BtnRecord.ToolTip = WithKey(st == RecState.Idle ? "开始录音" : "停止并保存", _c.Settings.HotkeyToggle);
            BtnPause.ToolTip = WithKey(st == RecState.Paused ? "继续" : "暂停", _c.Settings.HotkeyPause);

            if (changed)
            {
                AnimateRecordButton(st != RecState.Idle);
                SetBlink(ChipDot, st == RecState.Recording);
                SetPulse(st == RecState.Recording);
                UpdateBorderColor();
            }
        }

        private void AnimateRecordButton(bool recording)
        {
            var dur = new Duration(TimeSpan.FromMilliseconds(280));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            double size = recording ? 24 : 50, radius = recording ? 6 : 25;
            RecCore.BeginAnimation(WidthProperty, new DoubleAnimation(size, dur) { EasingFunction = ease });
            RecCore.BeginAnimation(HeightProperty, new DoubleAnimation(size, dur) { EasingFunction = ease });
            RecCore.BeginAnimation(System.Windows.Shapes.Rectangle.RadiusXProperty, new DoubleAnimation(radius, dur) { EasingFunction = ease });
            RecCore.BeginAnimation(System.Windows.Shapes.Rectangle.RadiusYProperty, new DoubleAnimation(radius, dur) { EasingFunction = ease });
            RecRing.Stroke = recording ? new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0x47, 0x57)) : new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
        }

        private static void SetBlink(UIElement el, bool on)
        {
            if (on)
            {
                var a = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(600))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                el.BeginAnimation(OpacityProperty, a);
            }
            else el.BeginAnimation(OpacityProperty, null);
        }

        private void SetPulse(bool on)
        {
            if (on)
            {
                var d = TimeSpan.FromMilliseconds(1800);
                RecPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 0, d) { RepeatBehavior = RepeatBehavior.Forever });
                PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.35, d) { RepeatBehavior = RepeatBehavior.Forever });
                PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.35, d) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                RecPulse.BeginAnimation(OpacityProperty, null);
                RecPulse.Opacity = 0;
                PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
        }

        // ==================================================================
        //  提示音源状态
        // ==================================================================

        /// <summary>只在出问题时显示一行简短提示；正常状态不打扰。</summary>
        private void UpdateHints()
        {
            var s = _c.Settings;
            var sys = _c.Engine.SysStatus;
            var mic = _c.Engine.MicStatus;

            SysLevelRow.Opacity = s.SysEnabled ? 1 : 0.35;
            SysPicker.Opacity = s.SysEnabled ? 1 : 0.5;
            MicLevelRow.Opacity = s.MicEnabled ? 1 : 0.35;
            MicPicker.Opacity = s.MicEnabled ? 1 : 0.5;

            // 当前实际在用的设备放在悬停提示里
            SysPicker.ToolTip = sys.State == GroupState.Ok || sys.State == GroupState.Fallback ? sys.Detail : null;
            MicPicker.ToolTip = mic.State == GroupState.Ok || mic.State == GroupState.Fallback ? mic.Detail : null;

            SetHint(SysHint, SysHintText, s.SysEnabled ? HintFor(sys) : null);
            SetHint(MicHint, MicHintText, s.MicEnabled ? HintFor(mic) : null);
        }

        private static string HintFor(GroupStatus st)
        {
            switch (st.State)
            {
                case GroupState.Ok: return null;
                case GroupState.Fallback: return $"{(st.FallbackBroken ? "无法打开" : "未连接")}，已改用 {st.Detail}";
                default: return st.Detail;
            }
        }

        private void SetHint(FrameworkElement row, TextBlock text, string msg)
        {
            row.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
            text.Text = msg ?? "";
            text.Foreground = Res("WarnText");
        }

        private static string WithKey(string text, string hotkey) =>
            string.IsNullOrEmpty(hotkey) ? text : $"{text}  {hotkey}";

        // ==================================================================
        //  提示条
        // ==================================================================

        internal void ShowToast(string msg, NoticeKind kind)
        {
            ToastText.Text = msg;
            ToastIcon.Kind = kind == NoticeKind.Warn ? "warn" : kind == NoticeKind.Ok ? "ok" : "info";
            ToastIcon.Foreground = kind == NoticeKind.Warn ? Res("Warn") : kind == NoticeKind.Ok ? Res("Ok") : Res("Accent");
            Toast.Visibility = Visibility.Visible;
            var d = new Duration(TimeSpan.FromMilliseconds(200));
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
            ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, d));
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(kind == NoticeKind.Warn ? 7 : 4);
            _toastTimer.Start();
        }

        private void HideToast()
        {
            _toastTimer.Stop();
            var a = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(250)));
            a.Completed += (s, e) => { if (Toast.Opacity < 0.01) Toast.Visibility = Visibility.Collapsed; };
            Toast.BeginAnimation(OpacityProperty, a);
        }

        // ==================================================================
        //  最近录音 / 状态栏
        // ==================================================================

        private bool _refreshing;

        public async void RefreshRecent()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                var items = await Task.Run(() => _c.GetRecent(3));
                RecentList.Children.Clear();
                foreach (var it in items) RecentList.Children.Add(BuildRecentRow(it));
                RecentEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { Log.Warn("刷新最近录音失败: " + ex.Message); }
            finally { _refreshing = false; }
        }

        private FrameworkElement BuildRecentRow(RecentItem it)
        {
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Background = Brushes.Transparent };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var play = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, ToolTip = "用默认播放器打开" };
            play.Content = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Res("Fill2"),
                Child = new Glyph { Kind = "play", Width = 12, Height = 12 },
            };
            play.Click += (s, e) => SystemIntegration.OpenFile(it.Path);
            g.Children.Add(play);

            var text = new StackPanel { Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = it.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = it.Path });
            string dur = it.Duration.HasValue ? FormatDuration(it.Duration.Value) + " · " : "";
            text.Children.Add(new TextBlock
            {
                Text = $"{dur}{FormatSize(it.Size)} · {FormatWhen(it.Time)}",
                FontSize = 11, Foreground = Res("Text3"),
            });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            var reveal = new Button { Style = (Style)FindResource("IconButton"), ToolTip = "在文件夹中显示", Opacity = 0 };
            reveal.Content = new Glyph { Kind = "folder" };
            reveal.Click += (s, e) => SystemIntegration.RevealFile(it.Path);
            Grid.SetColumn(reveal, 2);
            g.Children.Add(reveal);

            row.Child = g;
            row.MouseEnter += (s, e) => { row.Background = Res("CardBg"); reveal.Opacity = 1; };
            row.MouseLeave += (s, e) => { row.Background = Brushes.Transparent; reveal.Opacity = 0; };
            row.MouseLeftButtonUp += (s, e) => { if (e.ClickCount == 1 && e.OriginalSource is TextBlock) SystemIntegration.RevealFile(it.Path); };
            return row;
        }

        private static string FormatSize(long bytes) =>
            bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1073741824.0:F1} GB" : $"{bytes / 1048576.0:F1} MB";

        private static string FormatWhen(DateTime t)
        {
            var today = DateTime.Today;
            if (t.Date == today) return "今天 " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
            return t.ToString("M月d日 HH:mm");
        }

        private void UpdateStatusBar()
        {
            var s = _c.Settings;
            DirText.Text = s.EffectiveOutputDir;
            DirDesc.Text = s.EffectiveOutputDir;
            DirDesc.ToolTip = s.EffectiveOutputDir;
            string fmt = s.Format == "wav" ? "WAV 无损" : $"MP3 {s.Bitrate}k";
            string free = "";
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(s.EffectiveOutputDir));
                var di = new DriveInfo(root);
                if (di.IsReady)
                {
                    double gb = di.AvailableFreeSpace / 1073741824.0;
                    free = gb >= 10 ? $" · 剩余 {gb:F0} GB" : $" · 剩余 {gb:F1} GB";
                }
            }
            catch { }
            StatusRight.Text = fmt + (s.SplitTracks ? " · 分轨" : "") + free;
        }

        // ==================================================================
        //  设置面板
        // ==================================================================

        private void ShowSettings(bool show)
        {
            PickerPopup.IsOpen = false;
            var d = new Duration(TimeSpan.FromMilliseconds(240));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (show)
            {
                LoadSettingsToUi();
                SettingsPanel.Visibility = Visibility.Visible;
                SettingsShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(ActualWidth, 0, d) { EasingFunction = ease });
            }
            else
            {
                Keyboard.ClearFocus();
                var a = new DoubleAnimation(0, ActualWidth, d) { EasingFunction = ease };
                a.Completed += (s, e) => SettingsPanel.Visibility = Visibility.Collapsed;
                SettingsShift.BeginAnimation(TranslateTransform.XProperty, a);
                _c.Settings.Save();
                UpdateRecordUi();
                UpdateStatusBar();
            }
        }

        public void PrepareForHide()
        {
            PickerPopup.IsOpen = false;
            SavePosition();
            _c.Settings.Save();
        }

        // ---- 截图模式用 ----
        internal FrameworkElement PickerSurface => PickerBorder;
        internal void ShotOpenPicker(bool sys) => OpenPicker(sys);
        internal void ShotClosePicker() => PickerPopup.IsOpen = false;
        internal void ShotSettings(bool show) => ShowSettings(show);
        internal void ShotSetNote(string note) => NoteBox.Text = note;
    }
}
