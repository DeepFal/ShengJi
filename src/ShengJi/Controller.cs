using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using ShengJi.Audio;
using ShengJi.Core;
using ShengJi.Recording;

namespace ShengJi
{
    internal enum RecState { Idle, Recording, Paused }

    /// <summary>电平表的显示状态（带峰值保持和平滑下落）。</summary>
    internal sealed class MeterState
    {
        public double Level;   // 0..1
        public double Peak;    // 0..1
        public bool HasSound;  // 最近 0.4 秒内有明显声音
        private double _peakT, _soundT;

        public void Update(float linear, double now)
        {
            double pos = linear <= 1e-6f ? 0 : Math.Max(0, Math.Min(1, (20 * Math.Log10(linear) + 60) / 60));
            Level = pos > Level ? Level + (pos - Level) * 0.7 : Level + (pos - Level) * 0.18;
            if (Level >= Peak) { Peak = Level; _peakT = now; }
            else if (now - _peakT > 0.9) Peak = Math.Max(Level, Peak - 0.012);
            if (pos > 0.25) _soundT = now;           // 高于约 -45dB 视为“有声音”
            HasSound = now - _soundT < 0.4;
        }
    }

    internal sealed class RecentItem
    {
        public string Path;
        public string Name;
        public DateTime Time;
        public long Size;
        public TimeSpan? Duration;
    }

    /// <summary>
    /// 应用控制器：持有设置、音频引擎、当前录音，统一处理开始/暂停/停止/快捷键/电平刷新。
    /// 所有事件都在 UI 线程上触发。
    /// </summary>
    internal sealed class AppController
    {
        public readonly AppSettings Settings;
        public readonly AudioEngine Engine = new AudioEngine();
        public readonly MeterState SysMeter = new MeterState();
        public readonly MeterState MicMeter = new MeterState();

        public Recorder Recorder { get; private set; }
        private readonly List<Recorder> _finishing = new List<Recorder>();   // 已按下停止、正在收尾的录音
        public string Note = "";

        public string HotkeyToggleError { get; private set; }
        public string HotkeyPauseError { get; private set; }

        public event Action StateChanged;
        public event Action<string, NoticeKind> Notice;
        public event Action LevelsUpdated;
        public event Action SourceStatusChanged;
        public event Action<RecordingResult> RecordingSaved;
        public event Action NoteCleared;

        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _timer;
        private HotkeyManager _hotkeys;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        public AppController(AppSettings settings)
        {
            Settings = settings;
            _ui = Dispatcher.CurrentDispatcher;
            _timer = new DispatcherTimer(DispatcherPriority.Render, _ui) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) => Tick();
        }

        public RecState State => Recorder == null ? RecState.Idle : Recorder.IsPaused ? RecState.Paused : RecState.Recording;
        public bool IsRecording => Recorder != null;
        public TimeSpan Elapsed => Recorder?.Duration ?? TimeSpan.Zero;

        public void Initialize()
        {
            Engine.Notice += (msg, kind) => _ui.BeginInvoke(new Action(() => Notice?.Invoke(msg, kind)));
            Engine.StatusChanged += () => _ui.BeginInvoke(new Action(() => SourceStatusChanged?.Invoke()));
            ApplyAudioSettings();
            Engine.Start();
            _timer.Start();
            _hotkeys = new HotkeyManager();
            RegisterHotkeys();
            // 后台预热 MP3 编码器，第一次点“开始录音”时更快
            System.Threading.Tasks.Task.Run(() => Mp3FileWriter.IsAvailable());
        }

        public void Shutdown()
        {
            _timer.Stop();
            _hotkeys?.Dispose();
            Engine.Stop();
            SystemIntegration.KeepAwake(false);
            Settings.Save();
        }

        // ------------------------------------------------------------------
        //  音源与音量
        // ------------------------------------------------------------------

        public void ApplyAudioSettings()
        {
            Engine.SysGain = Settings.SysVolume / 100f;
            Engine.MicGain = Settings.MicVolume / 100f;
            Engine.SysEnabled = Settings.SysEnabled;
            Engine.MicEnabled = Settings.MicEnabled;
            Engine.SetSources(SourceSpec.Parse(Settings.SysSource, Settings.SysSourceLabel),
                              SourceSpec.Parse(Settings.MicSource, Settings.MicSourceLabel));
        }

        public void SetSysSource(string spec, string label)
        {
            Settings.SysSource = spec;
            Settings.SysSourceLabel = label;
            Settings.Save();
            ApplyAudioSettings();
        }

        public void SetMicSource(string spec, string label)
        {
            Settings.MicSource = spec;
            Settings.MicSourceLabel = label;
            Settings.Save();
            ApplyAudioSettings();
        }

        public void SetVolumes(int sys, int mic)
        {
            Settings.SysVolume = sys;
            Settings.MicVolume = mic;
            Engine.SysGain = sys / 100f;
            Engine.MicGain = mic / 100f;
        }

        public void SetEnabled(bool sys, bool mic)
        {
            Settings.SysEnabled = sys;
            Settings.MicEnabled = mic;
            Engine.SysEnabled = sys;
            Engine.MicEnabled = mic;
            Settings.Save();
            SourceStatusChanged?.Invoke();
        }

        // ------------------------------------------------------------------
        //  录音
        // ------------------------------------------------------------------

        public void ToggleRecord()
        {
            if (IsRecording) StopRecording(); else StartRecording();
        }

        public bool StartRecording()
        {
            if (IsRecording) return true;

            // 注意：上一段可能还在收尾（停止后约 0.25 秒内会把“按下停止前”的最后一点录完），
            // 引擎支持同时挂多个录音器，所以这里不需要等它，也不会截断它。
            var opt = new RecordingOptions
            {
                Format = Settings.Format,
                Bitrate = Settings.Bitrate,
                SplitTracks = Settings.SplitTracks,
                SegmentMinutes = Settings.SegmentMinutes,
                OutputDir = Settings.EffectiveOutputDir,
                Template = Settings.FileNameTemplate,
                Note = Note,
                StartTime = DateTime.Now,
            };
            try
            {
                Recorder = Recorder.Start(Engine, opt,
                    w => _ui.BeginInvoke(new Action(() => Notice?.Invoke(w, NoticeKind.Warn))),
                    (rec, result) => _ui.BeginInvoke(new Action(() => OnRecordingCompleted(rec, result))));
            }
            catch (Exception ex)
            {
                Log.Error("开始录音失败", ex);
                Notice?.Invoke("无法开始录音：" + DescribeIoError(ex, opt.OutputDir), NoticeKind.Warn);
                return false;
            }

            if (Settings.PreventSleep) SystemIntegration.KeepAwake(true);
            StateChanged?.Invoke();
            return true;
        }

        public void StopRecording()
        {
            var r = Recorder;
            if (r == null) return;
            r.Stop();
            // 录音过程中填写/修改的备注也属于这次录音：保存后按最终备注重命名文件
            r.FinalNote = Note;
            Note = "";
            NoteCleared?.Invoke();
            lock (_finishing) _finishing.Add(r);
            Recorder = null;
            SystemIntegration.KeepAwake(false);
            StateChanged?.Invoke();
        }

        public void TogglePause()
        {
            var r = Recorder;
            if (r == null) return;
            if (r.IsPaused) r.Resume(); else r.Pause();
            StateChanged?.Invoke();
        }

        /// <summary>退出前调用：停止录音并等待所有文件写完。返回是否在时限内完成。</summary>
        public bool StopAndWait(int ms)
        {
            if (Recorder != null) StopRecording();
            Recorder[] pending;
            lock (_finishing) pending = _finishing.ToArray();
            bool all = true;
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            foreach (var r in pending)
            {
                int left = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                if (r.WaitCompleted(left)) continue;
                r.EngineStopped();
                all &= r.WaitCompleted(5000);
            }
            return all;
        }

        private void OnRecordingCompleted(Recorder rec, RecordingResult result)
        {
            lock (_finishing) _finishing.Remove(rec);
            bool unexpected = ReferenceEquals(Recorder, rec);
            if (unexpected)
            {
                // 不是用户点的停止：录音因错误自行结束（磁盘写满、引擎故障等）
                Recorder = null;
                SystemIntegration.KeepAwake(false);
                StateChanged?.Invoke();
            }

            if (result.Error != null)
                Notice?.Invoke("录音已停止：" + DescribeIoError(result.Error, rec.Options.OutputDir) + "，已保存已录部分", NoticeKind.Warn);
            else if (unexpected)
                Notice?.Invoke("录音意外停止，已保存已录部分", NoticeKind.Warn);

            RenameForFinalNote(rec, result);
            RecordingSaved?.Invoke(result);
        }

        /// <summary>
        /// 备注在录音开始后才填写/修改时，把文件重命名为包含最终备注的名字。
        /// 同一次录音的所有文件（分段、分轨）一起改名，保证名字一致。
        /// </summary>
        private void RenameForFinalNote(Recorder rec, RecordingResult result)
        {
            string finalNote = rec.FinalNote;
            if (finalNote == null || FileNaming.Sanitize(finalNote) == FileNaming.Sanitize(rec.Options.Note)) return;
            string oldStem = FileNaming.Build(rec.Options.Template, rec.Options.StartTime, rec.Options.Note);
            string newStem = FileNaming.Build(rec.Options.Template, rec.Options.StartTime, finalNote);
            if (oldStem == newStem || result.Files.Count == 0) return;

            // 每个文件 = oldStem + 尾巴（如 "_01_麦克风"、" (2)"）+ 扩展名
            var parts = new List<(int index, string dir, string tail, string ext)>();
            for (int i = 0; i < result.Files.Count; i++)
            {
                string path = result.Files[i];
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith(oldStem, StringComparison.Ordinal) && File.Exists(path))
                    parts.Add((i, Path.GetDirectoryName(path), name.Substring(oldStem.Length), Path.GetExtension(path)));
            }
            if (parts.Count == 0) return;

            // 找一个对整组都不冲突的新名字主体
            string chosen = null;
            for (int n = 1; n < 1000 && chosen == null; n++)
            {
                string candidate = n == 1 ? newStem : $"{newStem} ({n})";
                if (parts.All(p => !File.Exists(Path.Combine(p.dir, candidate + p.tail + p.ext)))) chosen = candidate;
            }
            if (chosen == null) return;

            foreach (var p in parts)
            {
                string src = result.Files[p.index];
                string dst = Path.Combine(p.dir, chosen + p.tail + p.ext);
                try { File.Move(src, dst); result.Files[p.index] = dst; }
                catch (Exception ex) { Log.Warn($"按备注重命名失败 {src}: {ex.Message}"); }
            }
        }

        private static string DescribeIoError(Exception ex, string dir)
        {
            if (ex is UnauthorizedAccessException) return $"无权写入「{dir}」";
            if (ex is DirectoryNotFoundException) return $"找不到文件夹「{dir}」";
            if (ex is IOException io && (io.HResult & 0xFFFF) == 112) return "磁盘已满";
            return ex.Message;
        }

        // ------------------------------------------------------------------
        //  快捷键
        // ------------------------------------------------------------------

        /// <summary>暂时停用全局快捷键（例如正在设置里录入新的快捷键）。</summary>
        public void SuspendHotkeys() => _hotkeys?.UnregisterAll();

        public void RegisterHotkeys()
        {
            if (_hotkeys == null) return;
            _hotkeys.UnregisterAll();
            HotkeyToggleError = _hotkeys.Register(Settings.HotkeyToggle, ToggleRecord, out _);
            HotkeyPauseError = _hotkeys.Register(Settings.HotkeyPause, TogglePause, out _);
            if (HotkeyToggleError != null) Log.Warn($"快捷键 {Settings.HotkeyToggle} 注册失败: {HotkeyToggleError}");
            if (HotkeyPauseError != null) Log.Warn($"快捷键 {Settings.HotkeyPause} 注册失败: {HotkeyPauseError}");
        }

        // ------------------------------------------------------------------
        //  电平刷新
        // ------------------------------------------------------------------

        private void Tick()
        {
            Engine.ReadLevels(out float s, out float m);
            double now = _clock.Elapsed.TotalSeconds;
            SysMeter.Update(Settings.SysEnabled ? s : 0, now);
            MicMeter.Update(Settings.MicEnabled ? m : 0, now);
            LevelsUpdated?.Invoke();
        }

        // ------------------------------------------------------------------
        //  最近录音
        // ------------------------------------------------------------------

        public List<RecentItem> GetRecent(int count)
        {
            var list = new List<RecentItem>();
            try
            {
                var dir = new DirectoryInfo(Settings.EffectiveOutputDir);
                if (!dir.Exists) return list;
                var files = dir.EnumerateFiles("*.*")
                    .Where(f => (f.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                 f.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)) &&
                                !f.Name.Contains("_电脑声音.") && !f.Name.Contains("_麦克风."))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(count);
                foreach (var f in files)
                {
                    list.Add(new RecentItem
                    {
                        Path = f.FullName, Name = f.Name, Time = f.CreationTime, Size = f.Length,
                        Duration = MediaInfo.GetDuration(f.FullName),
                    });
                }
            }
            catch (Exception ex) { Log.Warn("读取最近录音失败: " + ex.Message); }
            return list;
        }
    }
}
