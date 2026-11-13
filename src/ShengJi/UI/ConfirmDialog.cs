using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using ShengJi.Interop;

namespace ShengJi.UI
{
    /// <summary>与主界面风格一致的确认框。</summary>
    internal sealed class ConfirmDialog : Window
    {
        private ConfirmDialog(string title, string message, string okText, string cancelText, bool danger)
        {
            Title = title;
            Width = 340;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = true;
            UseLayoutRounding = true;
            Background = (Brush)FindResource("WinBg");
            Foreground = (Brush)FindResource("Text");
            FontFamily = (FontFamily)FindResource("UiFont");
            FontSize = 13;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 40, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0, 0, 0, 1),
                UseAeroCaptionButtons = false,
            });

            var root = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
            var head = new DockPanel();
            var icon = new Glyph
            {
                Kind = danger ? "warn" : "info", Width = 18, Height = 18,
                Foreground = (Brush)FindResource(danger ? "Warn" : "Accent"), Margin = new Thickness(0, 1, 10, 0),
            };
            head.Children.Add(icon);
            head.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            root.Children.Add(head);
            root.Children.Add(new TextBlock
            {
                Text = message, Margin = new Thickness(28, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("Text2"), LineHeight = 20,
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var cancel = new Button { Content = cancelText, Style = (Style)FindResource("DialogButton"), IsCancel = true, MinWidth = 80 };
            cancel.Click += (s, e) => { DialogResult = false; };
            var ok = new Button
            {
                Content = okText, Style = (Style)FindResource(danger ? "DangerDialogButton" : "DialogButton"),
                Margin = new Thickness(10, 0, 0, 0), MinWidth = 96,
            };
            ok.Click += (s, e) => { DialogResult = true; };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);

            Content = new Border { BorderBrush = (Brush)FindResource("Line2"), BorderThickness = new Thickness(0), Child = root };
            Loaded += (s, e) => cancel.Focus();
            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                Native.TrySetDwm(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
                Native.TrySetDwm(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
                Native.TrySetDwm(h, Native.DWMWA_BORDER_COLOR, Native.ColorRef(0x3A, 0x3E, 0x47));
            };
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };
        }

        public static bool Ask(Window owner, string title, string message, string okText, string cancelText, bool danger)
        {
            var dlg = new ConfirmDialog(title, message, okText, cancelText, danger);
            if (owner != null && owner.IsVisible)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            return dlg.ShowDialog() == true;
        }
    }
}
