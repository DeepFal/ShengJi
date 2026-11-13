using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShengJi.Core;

namespace ShengJi.UI
{
    public partial class MainWindow
    {
        private void LoadSettingsToUi()
        {
            bool old = _loading;
            _loading = true;
            var s = _c.Settings;

            SysOn.IsChecked = s.SysEnabled;
            MicOn.IsChecked = s.MicEnabled;
            SysVol.Value = s.SysVolume;
            MicVol.Value = s.MicVolume;
            OnVolumeChanged();

            FmtMp3.IsChecked = s.Format != "wav";
            FmtWav.IsChecked = s.Format == "wav";
            Br64.IsChecked = s.Bitrate == 64;
            Br128.IsChecked = s.Bitrate == 128 || (s.Bitrate != 64 && s.Bitrate != 192);
            Br192.IsChecked = s.Bitrate == 192;
            SplitSwitch.IsChecked = s.SplitTracks;
            Seg0.IsChecked = s.SegmentMinutes == 0;
            Seg30.IsChecked = s.SegmentMinutes == 30;
            Seg60.IsChecked = s.SegmentMinutes == 60;
            Seg120.IsChecked = s.SegmentMinutes == 120;
            TemplateBox.Text = s.FileNameTemplate;
            SleepSwitch.IsChecked = s.PreventSleep;
            AutoStartSwitch.IsChecked = SystemIntegration.IsAutoStartEnabled();

            HkToggle.Text = HotkeyText(s.HotkeyToggle);
            HkPause.Text = HotkeyText(s.HotkeyPause);
            UpdateHotkeyErrors();

            var ver = typeof(MainWindow).Assembly.GetName().Version;
            AboutTitle.Text = $"声记 {ver.Major}.{ver.Minor}.{ver.Build}" + (Paths.IsPortable ? " · 便携版" : "");

            UpdateFormatUi();
            _loading = old;
        }

        private void WireSettings()
        {
            RoutedEventHandler fmt = (s, e) =>
            {
                if (_loading) return;
                _c.Settings.Format = FmtWav.IsChecked == true ? "wav" : "mp3";
                SettingChanged();
            };
            FmtMp3.Checked += fmt; FmtWav.Checked += fmt;

            RoutedEventHandler br = (s, e) =>
            {
                if (_loading) return;
                _c.Settings.Bitrate = Br64.IsChecked == true ? 64 : Br192.IsChecked == true ? 192 : 128;
                SettingChanged();
            };
            Br64.Checked += br; Br128.Checked += br; Br192.Checked += br;

            RoutedEventHandler seg = (s, e) =>
            {
                if (_loading) return;
                _c.Settings.SegmentMinutes = Seg30.IsChecked == true ? 30 : Seg60.IsChecked == true ? 60 : Seg120.IsChecked == true ? 120 : 0;
                SettingChanged();
            };
            Seg0.Checked += seg; Seg30.Checked += seg; Seg60.Checked += seg; Seg120.Checked += seg;

            SplitSwitch.Checked += (s, e) => { if (!_loading) { _c.Settings.SplitTracks = true; SettingChanged(); } };
            SplitSwitch.Unchecked += (s, e) => { if (!_loading) { _c.Settings.SplitTracks = false; SettingChanged(); } };

            SleepSwitch.Checked += (s, e) => { if (!_loading) { _c.Settings.PreventSleep = true; ApplySleep(); } };
            SleepSwitch.Unchecked += (s, e) => { if (!_loading) { _c.Settings.PreventSleep = false; ApplySleep(); } };

            AutoStartSwitch.Checked += (s, e) => { if (!_loading) SystemIntegration.SetAutoStart(true); };
            AutoStartSwitch.Unchecked += (s, e) => { if (!_loading) SystemIntegration.SetAutoStart(false); };

            TemplateBox.TextChanged += (s, e) =>
            {
                if (!_loading && !string.IsNullOrWhiteSpace(TemplateBox.Text))
                    _c.Settings.FileNameTemplate = TemplateBox.Text;
                UpdateTemplatePreview();
            };
            TemplateBox.LostKeyboardFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(TemplateBox.Text)) TemplateBox.Text = _c.Settings.FileNameTemplate = "{日期}_{时间}_{备注}";
                _c.Settings.Save();
            };

            BtnChangeDir.Click += (s, e) => ChooseOutputDir();
            BtnLogs.Click += (s, e) => SystemIntegration.OpenFolder(Paths.DataDir);

            WireHotkeyBox(HkToggle, true);
            WireHotkeyBox(HkPause, false);
        }

        private void SettingChanged()
        {
            _c.Settings.Save();
            UpdateFormatUi();
            UpdateStatusBar();
            if (_c.IsRecording) ShowToast("下次录音生效", Audio.NoticeKind.Info);
        }

        private void ApplySleep()
        {
            _c.Settings.Save();
            if (_c.IsRecording) SystemIntegration.KeepAwake(_c.Settings.PreventSleep);
        }

        private void UpdateFormatUi()
        {
            bool wav = _c.Settings.Format == "wav";
            RowBitrate.IsEnabled = !wav;
            RowBitrate.Opacity = wav ? 0.45 : 1;
            UpdateTemplatePreview();
        }

        private void UpdateTemplatePreview()
        {
            string ext = _c.Settings.Format == "wav" ? ".wav" : ".mp3";
            TemplatePreview.Text = FileNaming.Build(TemplateBox.Text, DateTime.Now, "备注") + ext;
        }

        private void ChooseOutputDir()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.ShowNewFolderButton = true;
                dlg.SelectedPath = _c.Settings.EffectiveOutputDir;
                var owner = new System.Windows.Forms.NativeWindow();
                owner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                try
                {
                    if (dlg.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;
                }
                finally { owner.ReleaseHandle(); }

                string dir = dlg.SelectedPath;
                if (!CanWrite(dir))
                {
                    ShowToast("无法写入该文件夹", Audio.NoticeKind.Warn);
                    return;
                }
                _c.Settings.OutputDir = dir;
                _c.Settings.Save();
                UpdateStatusBar();
                RefreshRecent();
                if (_c.IsRecording) ShowToast("下次录音生效", Audio.NoticeKind.Info);
            }
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = System.IO.Path.Combine(dir, ".shengji_write_test_" + Guid.NewGuid().ToString("N"));
                System.IO.File.WriteAllText(probe, "");
                System.IO.File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        //  快捷键录入
        // ------------------------------------------------------------------

        private static string HotkeyText(string hk) => string.IsNullOrEmpty(hk) ? "—" : hk.Replace("+", " + ");

        private void WireHotkeyBox(TextBox box, bool isToggle)
        {
            box.GotKeyboardFocus += (s, e) =>
            {
                _c.SuspendHotkeys();   // 录入时先停用全局快捷键，否则按下组合键会直接触发录音
                box.Text = "…";
            };
            box.LostKeyboardFocus += (s, e) =>
            {
                box.Text = HotkeyText(isToggle ? _c.Settings.HotkeyToggle : _c.Settings.HotkeyPause);
                _c.RegisterHotkeys();
                UpdateHotkeyErrors();
                UpdateRecordUi();
            };
            box.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (!box.IsKeyboardFocused) { box.Focus(); e.Handled = true; }
            };
            box.PreviewKeyDown += (s, e) =>
            {
                e.Handled = true;
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape || key == Key.Tab || key == Key.Enter) { Keyboard.ClearFocus(); return; }
                if (key == Key.Back || key == Key.Delete)
                {
                    Assign(isToggle, "");
                    Keyboard.ClearFocus();
                    return;
                }
                if (IsModifier(key)) return;

                var mods = Keyboard.Modifiers;
                if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
                if (mods == ModifierKeys.None || mods == ModifierKeys.Shift)
                {
                    box.Text = "需含 Ctrl 或 Alt";
                    return;
                }
                string text = HotkeyManager.Format(mods, key);
                string other = isToggle ? _c.Settings.HotkeyPause : _c.Settings.HotkeyToggle;
                if (string.Equals(text, other, StringComparison.OrdinalIgnoreCase))
                {
                    box.Text = "已被使用";
                    return;
                }
                Assign(isToggle, text);
                Keyboard.ClearFocus();
            };
        }

        private void Assign(bool isToggle, string text)
        {
            if (isToggle) _c.Settings.HotkeyToggle = text; else _c.Settings.HotkeyPause = text;
            _c.Settings.Save();
        }

        private static bool IsModifier(Key k) =>
            k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftAlt || k == Key.RightAlt ||
            k == Key.LeftShift || k == Key.RightShift || k == Key.LWin || k == Key.RWin;

        private void UpdateHotkeyErrors()
        {
            SetErr(HkToggleErr, _c.HotkeyToggleError);
            SetErr(HkPauseErr, _c.HotkeyPauseError);
        }

        private static void SetErr(TextBlock tb, string err)
        {
            tb.Text = err ?? "";
            tb.Visibility = string.IsNullOrEmpty(err) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
