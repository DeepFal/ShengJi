using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ShengJi.Core;
using ShengJi.Interop;

namespace ShengJi.UI
{
    /// <summary>
    /// 动态托盘图标：
    ///   待机 = 深灰底、蓝绿两条电平；录音中 = 红底、白色电平；暂停 = 橙底。
    ///   两条竖条分别是「电脑声音」和「麦克风」，有声音时会跟着跳。
    /// 右键菜单：显示主窗口 / 开始·停止录音 / 暂停 / 打开文件夹 / 退出。
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _ni;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miShow, _miRecord, _miPause, _miFolder, _miExit;
        private readonly Dictionary<int, Icon> _cache = new Dictionary<int, Icon>();
        private int _currentKey = -1;
        private string _currentText;
        private readonly int _size;

        public event Action ShowRequested;
        public event Action RecordRequested;
        public event Action PauseRequested;
        public event Action FolderRequested;
        public event Action ExitRequested;

        private const int Steps = 7;

        public TrayIcon()
        {
            _size = Math.Max(16, SystemInformation.SmallIconSize.Width);

            _menu = new ContextMenuStrip
            {
                Renderer = new DarkRenderer(),
                ShowImageMargin = false,
                Font = new Font("Microsoft YaHei UI", 9f),
                BackColor = Color.FromArgb(0x27, 0x2B, 0x33),
                ForeColor = Color.FromArgb(0xE9, 0xEB, 0xEF),
                Padding = new Padding(2, 4, 2, 4),
            };
            _miShow = Item("打开声记", () => ShowRequested?.Invoke());
            _miShow.Font = new Font(_menu.Font, FontStyle.Bold);
            _miRecord = Item("开始录音", () => RecordRequested?.Invoke());
            _miPause = Item("暂停", () => PauseRequested?.Invoke());
            _miFolder = Item("录音文件夹", () => FolderRequested?.Invoke());
            _miExit = Item("退出", () => ExitRequested?.Invoke());
            _menu.Items.AddRange(new ToolStripItem[]
            {
                _miShow, new ToolStripSeparator(), _miRecord, _miPause, new ToolStripSeparator(), _miFolder,
                new ToolStripSeparator(), _miExit,
            });

            _ni = new NotifyIcon { ContextMenuStrip = _menu, Visible = false };
            _ni.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowRequested?.Invoke(); };
            _ni.BalloonTipClicked += (s, e) => ShowRequested?.Invoke();
            Update(RecState.Idle, 0, 0, TimeSpan.Zero, null);
            _ni.Visible = true;
        }

        private ToolStripMenuItem Item(string text, Action a)
        {
            var mi = new ToolStripMenuItem(text) { Padding = new Padding(4, 3, 4, 3) };
            mi.Click += (s, e) => a();
            return mi;
        }

        /// <summary>刷新图标和提示文字。level 为 0..1 的电平显示位置。</summary>
        public void Update(RecState state, double sysLevel, double micLevel, TimeSpan elapsed, string hotkey)
        {
            int sysStep = Quantize(sysLevel), micStep = Quantize(micLevel);
            int key = ((int)state * Steps + sysStep) * Steps + micStep;
            if (key != _currentKey)
            {
                _currentKey = key;
                if (!_cache.TryGetValue(key, out var icon))
                {
                    icon = Draw(state, sysStep, micStep, _size);
                    _cache[key] = icon;
                }
                _ni.Icon = icon;
            }

            string text = state == RecState.Recording ? $"声记 · 录音中 {MainWindow.FormatClock(elapsed)}"
                        : state == RecState.Paused ? $"声记 · 已暂停 {MainWindow.FormatClock(elapsed)}"
                        : "声记";
            if (text.Length > 63) text = text.Substring(0, 63);
            if (text != _currentText) { _currentText = text; _ni.Text = text; }

            string hk = string.IsNullOrEmpty(hotkey) ? "" : "\t" + hotkey;
            _miRecord.Text = (state == RecState.Idle ? "开始录音" : "停止并保存") + hk;
            _miPause.Text = state == RecState.Paused ? "继续录音" : "暂停";
            _miPause.Enabled = state != RecState.Idle;
        }

        private static int Quantize(double level)
        {
            // 低于约 -48dB 视为没声音
            if (level < 0.2) return 0;
            return Math.Min(Steps - 1, 1 + (int)((level - 0.2) / 0.8 * (Steps - 1)));
        }

        public void ShowBalloon(string title, string text, bool warn)
        {
            try { _ni.ShowBalloonTip(4000, title, text, warn ? ToolTipIcon.Warning : ToolTipIcon.Info); } catch { }
        }

        // ------------------------------------------------------------------

        private static Icon Draw(RecState state, int sysStep, int micStep, int s)
        {
            using (var bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    Color bg = state == RecState.Recording ? Color.FromArgb(0xFF, 0x47, 0x57)
                             : state == RecState.Paused ? Color.FromArgb(0xF5, 0xA6, 0x23)
                             : Color.FromArgb(0x3A, 0x40, 0x4B);
                    float r = s * 0.24f;
                    using (var path = RoundRect(0.5f, 0.5f, s - 1f, s - 1f, r))
                    using (var b = new SolidBrush(bg))
                        g.FillPath(b, path);

                    Color cSys, cMic;
                    if (state == RecState.Idle) { cSys = Color.FromArgb(0x8F, 0xB0, 0xFF); cMic = Color.FromArgb(0x5F, 0xE3, 0xA9); }
                    else if (state == RecState.Paused) { cSys = cMic = Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF); }
                    else { cSys = cMic = Color.White; }

                    float barW = Math.Max(2f, s * 0.2f);
                    float gap = Math.Max(1.5f, s * 0.12f);
                    float cx = s / 2f;
                    DrawBar(g, cx - gap / 2 - barW, barW, sysStep, s, cSys);
                    DrawBar(g, cx + gap / 2, barW, micStep, s, cMic);
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }

        private static void DrawBar(Graphics g, float x, float w, int step, int s, Color c)
        {
            float minH = w;                       // 没声音时是一个小圆点
            float maxH = s * 0.66f;
            float h = step == 0 ? minH : minH + (maxH - minH) * step / (Steps - 1);
            float y = (s - h) / 2f;
            using (var path = RoundRect(x, y, w, h, w / 2))
            using (var b = new SolidBrush(c))
                g.FillPath(b, path);
        }

        private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            r = Math.Min(r, Math.Min(w, h) / 2);
            float d = r * 2;
            if (d <= 0.01f) { p.AddRectangle(new RectangleF(x, y, w, h)); return p; }
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>开发用：把几种状态的托盘图标画到一张预览图里（16/20/24/32px）。</summary>
        public static void SavePreview(string path)
        {
            var states = new[]
            {
                (RecState.Idle, 0, 0), (RecState.Idle, 4, 2), (RecState.Recording, 0, 0),
                (RecState.Recording, 6, 3), (RecState.Recording, 2, 5), (RecState.Paused, 0, 0),
            };
            int[] sizes = { 16, 20, 24, 32 };
            int cell = 44;
            using (var bmp = new Bitmap(states.Length * cell + 8, sizes.Length * cell + 8))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(0x20, 0x20, 0x20));
                for (int si = 0; si < sizes.Length; si++)
                    for (int i = 0; i < states.Length; i++)
                        using (var ico = Draw(states[i].Item1, states[i].Item2, states[i].Item3, sizes[si]))
                            g.DrawIcon(ico, 8 + i * cell, 8 + si * cell);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        public void Dispose()
        {
            _ni.Visible = false;
            _ni.Dispose();
            _menu.Dispose();
            foreach (var i in _cache.Values) i.Dispose();
            _cache.Clear();
        }

        // ------------------------------------------------------------------
        //  深色菜单
        // ------------------------------------------------------------------

        private sealed class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Color.FromArgb(0xE9, 0xEB, 0xEF) : Color.FromArgb(0x6E, 0x75, 0x81);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (var p = new Pen(Color.FromArgb(0x3A, 0x3F, 0x48)))
                    e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
            }
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            private static readonly Color Bg = Color.FromArgb(0x27, 0x2B, 0x33);
            private static readonly Color Hover = Color.FromArgb(0x35, 0x3A, 0x44);
            private static readonly Color Border = Color.FromArgb(0x3A, 0x3F, 0x48);
            public override Color ToolStripDropDownBackground => Bg;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
        }
    }
}
