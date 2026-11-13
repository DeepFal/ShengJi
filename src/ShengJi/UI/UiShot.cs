using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShengJi.Core;

namespace ShengJi.UI
{
    /// <summary>
    /// 开发用：--ui-shot &lt;dir&gt;，依次把各个界面状态渲染成 PNG 后退出。
    /// 此模式下不保存任何设置，录音写到 &lt;dir&gt;\rec。
    /// </summary>
    internal static class UiShot
    {
        public static void PrepareSettings(AppSettings s, string dir)
        {
            AppSettings.SaveDisabled = true;
            s.OutputDir = Path.Combine(dir, "rec");
            s.TrayTipShown = true;
            s.MainLeft = s.MainTop = double.NaN;
        }

        public static async void Run(App app, MainWindow main, MiniWindow mini, string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                await Task.Delay(2500);
                Render(main, Path.Combine(dir, "01_idle.png"));

                main.ShotOpenPicker(true);
                await Task.Delay(1500);
                Render(main.PickerSurface, Path.Combine(dir, "02_picker_sys.png"));
                main.ShotClosePicker();
                await Task.Delay(500);

                main.ShotOpenPicker(false);
                await Task.Delay(1200);
                Render(main.PickerSurface, Path.Combine(dir, "03_picker_mic.png"));
                main.ShotClosePicker();
                await Task.Delay(400);

                // 先开始录音，录音过程中才填备注 → 保存时应自动改名为带备注的文件名
                app.Controller.StartRecording();
                await Task.Delay(1200);
                main.ShotSetNote("订单8823");
                await Task.Delay(1400);
                Render(main, Path.Combine(dir, "04_recording.png"));

                main.ShotSettings(true);
                await Task.Delay(900);
                Render(main, Path.Combine(dir, "05_settings.png"));
                main.ShotSettings(false);
                await Task.Delay(600);

                app.EnterMini();
                await Task.Delay(1200);
                Render(mini, Path.Combine(dir, "06_mini.png"));
                app.ExitMini();
                await Task.Delay(600);

                TrayIcon.SavePreview(Path.Combine(dir, "07_tray.png"));

                app.Controller.StopRecording();
                await Task.Delay(1800);
                Render(main, Path.Combine(dir, "08_saved.png"));
            }
            catch (Exception ex) { Log.Error("界面截图失败", ex); }
            finally { app.ExitNow(); }
        }

        private static void Render(FrameworkElement el, string path)
        {
            if (el == null || el.ActualWidth <= 0) return;
            var dpi = VisualTreeHelper.GetDpi(el);
            int w = (int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX);
            int h = (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }
    }
}
