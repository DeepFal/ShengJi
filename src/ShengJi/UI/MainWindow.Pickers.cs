using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShengJi.Audio;
using ShengJi.Core;

namespace ShengJi.UI
{
    public partial class MainWindow
    {
        private bool _pickerIsSys;
        private readonly Dictionary<string, ImageSource> _appIcons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private async Task PreloadCatalogAsync()
        {
            try
            {
                var cat = await Task.Run(() => DeviceCatalog.Build());
                RememberIcons(cat);
                UpdatePickers();
            }
            catch (Exception ex) { Log.Warn("预读设备列表失败: " + ex.Message); }
        }

        private void RememberIcons(SourceCatalog cat)
        {
            foreach (var a in cat.Apps)
                if (a.Icon != null) _appIcons[a.ExeName] = a.Icon;
        }

        // ==================================================================
        //  选择器按钮上的显示
        // ==================================================================

        private void UpdatePickers()
        {
            var s = _c.Settings;
            var sys = SourceSpec.Parse(s.SysSource, s.SysSourceLabel);
            switch (sys.Mode)
            {
                case SourceMode.AllOutputs:
                    SysPicker.Content = PickerContent(GlyphIcon("layers"), "全部设备"); break;
                case SourceMode.Device:
                    SysPicker.Content = PickerContent(GlyphIcon("speaker"), sys.Label ?? "指定设备"); break;
                case SourceMode.App:
                    SysPicker.Content = PickerContent(AppIcon(sys.Id), sys.Label ?? sys.Id); break;
                default:
                    SysPicker.Content = PickerContent(GlyphIcon("speaker"), "系统默认"); break;
            }

            var mic = SourceSpec.Parse(s.MicSource, s.MicSourceLabel);
            MicPicker.Content = mic.Mode == SourceMode.Device
                ? PickerContent(GlyphIcon("mic"), mic.Label ?? "指定麦克风")
                : PickerContent(GlyphIcon("mic"), "系统默认");
        }

        private FrameworkElement PickerContent(FrameworkElement icon, string name)
        {
            var dp = new DockPanel { LastChildFill = true };
            icon.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(icon, Dock.Left);
            dp.Children.Add(icon);

            var chev = new Glyph { Kind = "chevron", Foreground = Res("Text3"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(chev, Dock.Right);
            dp.Children.Add(chev);

            dp.Children.Add(new TextBlock
            {
                Text = name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            return dp;
        }

        private FrameworkElement GlyphIcon(string kind) =>
            new Border
            {
                Width = 18, Height = 18,
                Child = new Glyph { Kind = kind, Width = 15, Height = 15, Foreground = Res("Text2") },
            };

        private FrameworkElement AppIcon(string exe)
        {
            if (exe != null && _appIcons.TryGetValue(exe, out var img))
                return new Image { Source = img, Width = 18, Height = 18 }.WithHighQuality();
            return GlyphIcon("app");
        }

        // ==================================================================
        //  下拉列表
        // ==================================================================

        private void TogglePicker(bool isSys)
        {
            if (PickerPopup.IsOpen && _pickerIsSys == isSys) { PickerPopup.IsOpen = false; return; }
            OpenPicker(isSys);
        }

        /// <summary>d 是否就是 ancestor 或在它里面（同时沿可视树和逻辑树向上找，兼容弹出框里的元素）。</summary>
        private static bool IsWithin(DependencyObject d, DependencyObject ancestor)
        {
            while (d != null)
            {
                if (ReferenceEquals(d, ancestor)) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private async void OpenPicker(bool isSys)
        {
            _pickerIsSys = isSys;
            var target = isSys ? SysPicker : MicPicker;
            PickerPopup.PlacementTarget = target;
            PickerBorder.Width = target.ActualWidth;
            PickerList.Children.Clear();
            PickerList.Children.Add(new TextBlock
            {
                Text = "…", FontSize = 12, Foreground = Res("Text3"), Margin = new Thickness(10, 10, 10, 10),
            });
            target.Tag = "open";
            PickerPopup.IsOpen = true;

            SourceCatalog cat;
            try { cat = await Task.Run(() => DeviceCatalog.Build()); }
            catch (Exception ex) { Log.Warn("读取设备失败: " + ex.Message); return; }
            RememberIcons(cat);
            if (!PickerPopup.IsOpen || _pickerIsSys != isSys) return;

            PickerList.Children.Clear();
            if (isSys) FillSysPicker(cat); else FillMicPicker(cat);
            _c.Engine.Poke();
        }

        private void FillSysPicker(SourceCatalog cat)
        {
            var s = _c.Settings;
            string cur = s.SysSource ?? "default";
            var def = cat.Outputs.FirstOrDefault(d => d.IsDefault);

            AddItem(true, "default", GlyphIcon("speaker"), "系统默认", def?.Name, cur, null);
            AddItem(true, "all", GlyphIcon("layers"), "全部设备", null, cur, null, "同时录所有播放设备");

            AddGroup("应用");
            if (!cat.AppCaptureSupported)
            {
                AddNote("需要 Windows 10 2004 或更新版本");
            }
            else
            {
                foreach (var a in cat.Apps)
                    AddItem(true, "app:" + a.ExeName, a.Icon != null ? (FrameworkElement)new Image { Source = a.Icon, Width = 18, Height = 18 }.WithHighQuality() : GlyphIcon("app"),
                        a.DisplayName, a.IsPlaying ? "正在发声" : null, cur, a.DisplayName, "只录这个应用的声音");
                if (cur.StartsWith("app:") && cat.Apps.All(a => "app:" + a.ExeName != cur))
                    AddItem(true, cur, AppIcon(cur.Substring(4)), s.SysSourceLabel ?? cur.Substring(4), "未运行", cur, s.SysSourceLabel);
                if (cat.Apps.Count == 0 && !cur.StartsWith("app:"))
                    AddNote("暂无");
            }

            AddGroup("设备");
            foreach (var d in cat.Outputs)
                AddItem(true, "dev:" + d.Id, GlyphIcon("speaker"), d.Name, null, cur, d.Name);
            if (cur.StartsWith("dev:") && cat.Outputs.All(d => "dev:" + d.Id != cur))
                AddItem(true, cur, GlyphIcon("speaker"), s.SysSourceLabel ?? "之前选的设备", "未连接", cur, s.SysSourceLabel);
        }

        private void FillMicPicker(SourceCatalog cat)
        {
            var s = _c.Settings;
            string cur = s.MicSource ?? "default";
            var def = cat.Inputs.FirstOrDefault(d => d.IsDefault);
            AddItem(false, "default", GlyphIcon("mic"), "系统默认", def?.Name, cur, null);
            if (cat.Inputs.Count > 0 || cur.StartsWith("dev:")) AddGroup("设备");
            foreach (var d in cat.Inputs)
                AddItem(false, "dev:" + d.Id, GlyphIcon("mic"), d.Name, null, cur, d.Name);
            if (cur.StartsWith("dev:") && cat.Inputs.All(d => "dev:" + d.Id != cur))
                AddItem(false, cur, GlyphIcon("mic"), s.MicSourceLabel ?? "之前选的麦克风", "未连接", cur, s.MicSourceLabel);
            if (cat.Inputs.Count == 0)
                AddNote("未找到麦克风");
        }

        private void AddGroup(string text)
        {
            PickerList.Children.Add(new TextBlock
            {
                Text = text, FontSize = 11, Foreground = Res("Text3"), Margin = new Thickness(8, 8, 8, 4),
            });
        }

        private void AddNote(string text)
        {
            PickerList.Children.Add(new TextBlock
            {
                Text = text, FontSize = 11, Foreground = Res("Text3"), Margin = new Thickness(8, 4, 8, 8), TextWrapping = TextWrapping.Wrap,
            });
        }

        private void AddItem(bool isSys, string spec, FrameworkElement icon, string name, string sub, string current, string label, string tip = null)
        {
            bool selected = spec == current;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 10, 0);
            g.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, FontSize = 12.5, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(sub))
                text.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Res("Text3"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            if (selected)
            {
                var check = new Glyph { Kind = "check", Foreground = Res("Accent"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(check, 2);
                g.Children.Add(check);
            }

            var btn = new Button { Style = (Style)FindResource("PickerItem"), Content = g, ToolTip = tip };
            btn.Click += (s, e) =>
            {
                PickerPopup.IsOpen = false;
                if (selected) return;
                if (isSys) _c.SetSysSource(spec, label); else _c.SetMicSource(spec, label);
                UpdatePickers();
                UpdateHints();
            };
            PickerList.Children.Add(btn);
        }
    }

    internal static class ImageExtensions
    {
        public static Image WithHighQuality(this Image img)
        {
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
    }
}
