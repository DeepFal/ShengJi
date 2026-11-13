using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using ShengJi.Interop;

namespace ShengJi.Core
{
    /// <summary>全局快捷键（挂在一个隐藏的消息窗口上，与主窗口是否显示无关）。</summary>
    internal sealed class HotkeyManager : IDisposable
    {
        private readonly HwndSource _src;
        private readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();
        private int _nextId = 1;

        public HotkeyManager()
        {
            var p = new HwndSourceParameters("ShengJiHotkeys")
            {
                Width = 0, Height = 0, WindowStyle = 0,
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            };
            _src = new HwndSource(p);
            _src.AddHook(WndProc);
        }

        /// <summary>注册快捷键，返回 null 表示成功，否则返回失败原因。</summary>
        public string Register(string text, Action action, out int id)
        {
            id = 0;
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!TryParse(text, out uint mods, out uint vk)) return "无效";
            id = _nextId++;
            if (!Native.RegisterHotKey(_src.Handle, id, mods | Native.MOD_NOREPEAT, vk))
            {
                id = 0;
                return "已被占用";
            }
            _actions[id] = action;
            return null;
        }

        public void UnregisterAll()
        {
            foreach (var id in _actions.Keys) Native.UnregisterHotKey(_src.Handle, id);
            _actions.Clear();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
            {
                handled = true;
                try { a(); } catch (Exception ex) { Log.Error("快捷键处理出错", ex); }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            UnregisterAll();
            _src.Dispose();
        }

        // ---------------- 文本 <-> 键 ----------------

        public static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            Key key = Key.None;
            foreach (var raw in text.Split('+'))
            {
                string t = raw.Trim();
                switch (t.ToLowerInvariant())
                {
                    case "ctrl": mods |= Native.MOD_CONTROL; break;
                    case "alt": mods |= Native.MOD_ALT; break;
                    case "shift": mods |= Native.MOD_SHIFT; break;
                    case "win": mods |= Native.MOD_WIN; break;
                    default:
                        if (!TryParseKey(t, out key)) return false;
                        break;
                }
            }
            if (key == Key.None || mods == 0) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0;
        }

        private static bool TryParseKey(string t, out Key key)
        {
            key = Key.None;
            if (t.Length == 1 && char.IsDigit(t[0])) return Enum.TryParse("D" + t, out key);
            return Enum.TryParse(t, true, out key);
        }

        public static string Format(ModifierKeys mods, Key key)
        {
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            string k = key.ToString();
            if (key >= Key.D0 && key <= Key.D9) k = k.Substring(1);
            parts.Add(k);
            return string.Join("+", parts);
        }
    }

    internal static class SystemIntegration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "ShengJi";

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = k?.GetValue(RunValue) as string;
                    return v != null && v.IndexOf(Paths.ExePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enable) k.SetValue(RunValue, $"\"{Paths.ExePath}\" --minimized");
                    else if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue, false);
                }
            }
            catch (Exception ex) { Log.Warn("设置开机启动失败: " + ex.Message); }
        }

        private static bool _keepAwake;

        /// <summary>录音时阻止系统进入睡眠（屏幕可以照常关闭）。必须在 UI 线程调用。</summary>
        public static void KeepAwake(bool on)
        {
            if (on == _keepAwake) return;
            _keepAwake = on;
            Native.SetThreadExecutionState(on ? Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED : Native.ES_CONTINUOUS);
        }

        public static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Warn("打开文件夹失败: " + ex.Message); }
        }

        public static void RevealFile(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("定位文件失败: " + ex.Message); }
        }

        public static bool OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return true; }
            catch (Exception ex) { Log.Warn("打开文件失败: " + ex.Message); return false; }
        }
    }

    /// <summary>读取录音文件时长（MP3 按 CBR 码率估算，WAV 读文件头）。</summary>
    internal static class MediaInfo
    {
        private static readonly int[] Mp3Bitrates = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };

        public static TimeSpan? GetDuration(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var head = new byte[Math.Min(8192, (int)Math.Min(fi.Length, 8192))];
                    int n = fs.Read(head, 0, head.Length);
                    if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n < 44) return null;
                        int byteRate = BitConverter.ToInt32(head, 28);
                        return byteRate > 0 ? TimeSpan.FromSeconds((fi.Length - 44) / (double)byteRate) : (TimeSpan?)null;
                    }
                    for (int i = 0; i + 4 <= n; i++)
                    {
                        if (head[i] == 0xFF && (head[i + 1] & 0xFE) == 0xFA)
                        {
                            int br = Mp3Bitrates[head[i + 2] >> 4];
                            if (br > 0) return TimeSpan.FromSeconds((fi.Length - i) * 8.0 / (br * 1000));
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
