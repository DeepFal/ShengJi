using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace ShengJi.Core
{
    /// <summary>用户设置。字段名即 XML 节点名，新增字段时给好默认值即可向后兼容。</summary>
    public class AppSettings
    {
        // 音源
        public string SysSource = "default";
        public string SysSourceLabel;
        public string MicSource = "default";
        public string MicSourceLabel;
        public bool SysEnabled = true;
        public bool MicEnabled = true;
        public int SysVolume = 100;
        public int MicVolume = 100;

        // 录音
        public string Format = "mp3";          // mp3 | wav
        public int Bitrate = 128;              // kbps
        public bool SplitTracks = false;
        public int SegmentMinutes = 0;         // 0 = 不分段

        // 文件
        public string OutputDir;
        public string FileNameTemplate = "{日期}_{时间}_{备注}";

        // 常规
        public string HotkeyToggle = "Ctrl+Alt+R";
        public string HotkeyPause = "Ctrl+Alt+P";
        public bool PreventSleep = true;
        public bool TrayTipShown = false;

        // 窗口位置
        public double MainLeft = double.NaN;
        public double MainTop = double.NaN;
        public double MiniLeft = double.NaN;
        public double MiniTop = double.NaN;

        [XmlIgnore]
        public string EffectiveOutputDir => string.IsNullOrWhiteSpace(OutputDir) ? Paths.DefaultOutputDir : OutputDir;

        // ------------------------------------------------------------------

        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(AppSettings));

        /// <summary>开发截图模式下不写入设置文件。</summary>
        [XmlIgnore]
        public static bool SaveDisabled;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    using (var fs = File.OpenRead(Paths.SettingsFile))
                    {
                        var s = (AppSettings)Serializer.Deserialize(fs);
                        s.Normalize();
                        return s;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("读取设置失败，使用默认设置: " + ex.Message); }
            var d = new AppSettings();
            d.Normalize();
            return d;
        }

        public void Save()
        {
            if (SaveDisabled) return;
            try
            {
                Paths.EnsureDataDir();
                string tmp = Paths.SettingsFile + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    Serializer.Serialize(w, this);
                    w.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(Paths.SettingsFile))
                    File.Replace(tmp, Paths.SettingsFile, null);
                else
                    File.Move(tmp, Paths.SettingsFile);
            }
            catch (Exception ex) { Log.Warn("保存设置失败: " + ex.Message); }
        }

        private void Normalize()
        {
            SysVolume = Clamp(SysVolume, 0, 200);
            MicVolume = Clamp(MicVolume, 0, 200);
            if (Format != "mp3" && Format != "wav") Format = "mp3";
            if (!new[] { 64, 96, 128, 160, 192 }.Contains(Bitrate)) Bitrate = 128;
            if (!new[] { 0, 30, 60, 120 }.Contains(SegmentMinutes)) SegmentMinutes = 0;
            if (string.IsNullOrWhiteSpace(FileNameTemplate)) FileNameTemplate = "{日期}_{时间}_{备注}";
            if (string.IsNullOrWhiteSpace(HotkeyToggle)) HotkeyToggle = "";
            if (string.IsNullOrWhiteSpace(HotkeyPause)) HotkeyPause = "";
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>录音文件命名。</summary>
    internal static class FileNaming
    {
        private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.Trim())
                if (Array.IndexOf(Invalid, c) < 0 && c != '.') sb.Append(c);
            string r = sb.ToString().Trim();
            return r.Length > 40 ? r.Substring(0, 40) : r;
        }

        /// <summary>按模板生成文件名主体（不含扩展名）。</summary>
        public static string Build(string template, DateTime time, string note)
        {
            string date = time.ToString("yyyyMMdd");
            string clock = time.ToString("HHmmss");
            string s = (template ?? "")
                .Replace("{日期}", date)
                .Replace("{时间}", clock)
                .Replace("{备注}", Sanitize(note));
            s = Sanitize(s);
            // 去掉空备注留下的多余连接符
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                bool sep = c == '_' || c == '-' || c == ' ';
                if (sep && (sb.Length == 0 || IsSep(sb[sb.Length - 1]))) continue;
                sb.Append(c);
            }
            while (sb.Length > 0 && IsSep(sb[sb.Length - 1])) sb.Length--;
            string r = sb.ToString();
            return r.Length == 0 ? $"{date}_{clock}" : r;
        }

        private static bool IsSep(char c) => c == '_' || c == '-' || c == ' ';

        /// <summary>如果文件已存在，追加 (2)、(3)…</summary>
        public static string Unique(string dir, string stem, string ext)
        {
            string p = Path.Combine(dir, stem + ext);
            for (int i = 2; File.Exists(p) && i < 1000; i++)
                p = Path.Combine(dir, $"{stem} ({i}){ext}");
            return p;
        }

        /// <summary>
        /// 为一组文件（主文件 + 分轨，后缀如 "" / "_电脑声音" / "_麦克风"）找一个共同可用的名字主体：
        /// 组内任何一个文件已存在都会换成 “主体 (2)” 再试，保证一组文件名字一致。
        /// </summary>
        public static string UniqueGroup(string dir, string stem, string[] suffixes, string ext)
        {
            string candidate = stem;
            for (int i = 2; i < 1000; i++)
            {
                bool clash = false;
                foreach (var s in suffixes)
                    if (File.Exists(Path.Combine(dir, candidate + s + ext))) { clash = true; break; }
                if (!clash) return candidate;
                candidate = $"{stem} ({i})";
            }
            return stem + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }
    }
}
