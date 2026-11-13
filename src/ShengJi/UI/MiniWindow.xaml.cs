using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ShengJi.UI
{
    /// <summary>迷你模式：一条置顶的小悬浮条，可拖动。</summary>
    public partial class MiniWindow : Window
    {
        private readonly AppController _c;
        private readonly App _app;
        private RecState? _shown;
        private string _lastClock;

        internal MiniWindow(AppController controller, App app)
        {
            _c = controller;
            _app = app;
            InitializeComponent();

            BtnPause.Click += (s, e) => _c.TogglePause();
            BtnRecord.Click += (s, e) => _c.ToggleRecord();
            BtnExpand.Click += (s, e) => _app.ExitMini();
            Pill.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
                try { DragMove(); } catch { }
                SavePosition();
            };
            MouseDoubleClick += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
                _app.ExitMini();
            };

            _c.StateChanged += UpdateState;
            _c.LevelsUpdated += OnLevels;
            RestorePosition();
            UpdateState();
        }

        private void RestorePosition()
        {
            var s = _c.Settings;
            var wa = SystemParameters.WorkArea;
            if (!double.IsNaN(s.MiniLeft) && !double.IsNaN(s.MiniTop) &&
                s.MiniLeft > SystemParameters.VirtualScreenLeft - 50 &&
                s.MiniLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                s.MiniTop > SystemParameters.VirtualScreenTop - 20 &&
                s.MiniTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
            {
                Left = s.MiniLeft; Top = s.MiniTop;
            }
            else
            {
                // 默认放在右下角任务栏上方
                Left = wa.Right - 354 - 12;
                Top = wa.Bottom - 76 - 12;
            }
        }

        private void SavePosition()
        {
            _c.Settings.MiniLeft = Left;
            _c.Settings.MiniTop = Top;
        }

        private void OnLevels()
        {
            if (!IsVisible) return;
            SysMeter.Level = _c.SysMeter.Level; SysMeter.Peak = _c.SysMeter.Peak;
            MicMeter.Level = _c.MicMeter.Level; MicMeter.Peak = _c.MicMeter.Peak;
            string clock = MainWindow.FormatClock(_c.Elapsed);
            if (clock != _lastClock) { _lastClock = clock; TimeText.Text = clock; }
        }

        private void UpdateState()
        {
            var st = _c.State;
            if (_shown == st) return;
            _shown = st;

            BtnPause.Visibility = st == RecState.Idle ? Visibility.Hidden : Visibility.Visible;
            PauseGlyph.Kind = st == RecState.Paused ? "play" : "pause";
            BtnPause.ToolTip = st == RecState.Paused ? "继续" : "暂停";
            BtnRecord.ToolTip = st == RecState.Idle ? "开始录音" : "停止并保存";

            Dot.BeginAnimation(OpacityProperty, null);
            Dot.Opacity = 1;
            switch (st)
            {
                case RecState.Recording:
                    Dot.Fill = (Brush)FindResource("Rec");
                    Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(600))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                    TimeText.Foreground = Brushes.White;
                    Pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0x47, 0x57));
                    break;
                case RecState.Paused:
                    Dot.Fill = (Brush)FindResource("Warn");
                    TimeText.Foreground = (Brush)FindResource("WarnText");
                    Pill.BorderBrush = (Brush)FindResource("Line2");
                    break;
                default:
                    Dot.Fill = (Brush)FindResource("Text3");
                    TimeText.Foreground = (Brush)FindResource("Text2");
                    TimeText.Text = _lastClock = "00:00:00";
                    Pill.BorderBrush = (Brush)FindResource("Line2");
                    break;
            }
            bool rec = st != RecState.Idle;
            RecCore.Width = RecCore.Height = rec ? 11 : 12;
            RecCore.RadiusX = RecCore.RadiusY = rec ? 2 : 6;
        }

        private static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }
    }
}
