using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ShengJi.Core;
using ShengJi.Interop;

namespace ShengJi.Audio
{
    internal enum SourceMode
    {
        /// <summary>系统默认设备（跟随 Windows 切换）</summary>
        Default,
        /// <summary>所有输出设备（仅电脑声音）</summary>
        AllOutputs,
        /// <summary>指定设备</summary>
        Device,
        /// <summary>指定应用（仅电脑声音）</summary>
        App,
    }

    /// <summary>用户选择的音源。序列化格式："default" / "all" / "dev:{id}" / "app:{exe}"。</summary>
    internal sealed class SourceSpec
    {
        public SourceMode Mode;
        public string Id;      // 设备 ID 或小写 exe 名
        public string Label;   // 显示名（设备/应用不在时也能显示）

        public static SourceSpec Parse(string s, string label)
        {
            if (string.IsNullOrEmpty(s) || s == "default") return new SourceSpec { Mode = SourceMode.Default };
            if (s == "all") return new SourceSpec { Mode = SourceMode.AllOutputs };
            if (s.StartsWith("dev:")) return new SourceSpec { Mode = SourceMode.Device, Id = s.Substring(4), Label = label };
            if (s.StartsWith("app:")) return new SourceSpec { Mode = SourceMode.App, Id = s.Substring(4).ToLowerInvariant(), Label = label };
            return new SourceSpec { Mode = SourceMode.Default };
        }

        public override string ToString()
        {
            switch (Mode)
            {
                case SourceMode.AllOutputs: return "all";
                case SourceMode.Device: return "dev:" + Id;
                case SourceMode.App: return "app:" + Id;
                default: return "default";
            }
        }
    }

    internal enum GroupState { Ok, Fallback, Waiting, NoDevice, Error, Unsupported }

    internal sealed class GroupStatus
    {
        public GroupState State;
        /// <summary>补充说明，例如当前实际在录的设备名、错误原因。</summary>
        public string Detail;
        /// <summary>已经成功开始采集的源数量。</summary>
        public int ActiveSources;
        /// <summary>Fallback 时：true = 指定设备在但打不开；false = 指定设备没连接。</summary>
        public bool FallbackBroken;
    }

    internal enum NoticeKind { Info, Ok, Warn }

    /// <summary>混音输出的接收者（录音器）。在混音线程上被调用。</summary>
    internal interface IAudioSink
    {
        /// <summary>时间轴区间 [pos, pos+frames) 的数据。mix/sys/mic 都是立体声交错。</summary>
        void Write(long pos, float[] mix, float[] sys, float[] mic, int frames);
        bool IsFinished { get; }
        /// <summary>引擎停止或出错时调用，接收者应立即收尾。</summary>
        void EngineStopped();
    }

    internal sealed class SourceStat
    {
        public string Key;
        public SourceKind Kind;
        public bool Started;
        public long Packets;
        public double SecondsSinceData;
    }

    /// <summary>
    /// 录音引擎：
    ///  · 调度线程：根据用户选择 + 设备/进程变化，决定需要哪些采集源，并在出错/拔插/卡死时自动重建；
    ///  · 混音线程：按统一时钟从各采集源读取、混音、限幅，输出给录音器，并统计电平。
    /// </summary>
    internal sealed class AudioEngine
    {
        /// <summary>混音相对实时的延迟。给采集数据留出到达和对齐的余量。</summary>
        public const int LatencyFrames = EngineClock.SampleRate / 4; // 250ms
        private const int ChunkFrames = 2400;                          // 50ms

        // 采集源健康检查
        private const double StartTimeoutSec = 8;      // 这么久还没开始采集 → 认为卡住
        private const double MicStallSec = 3;          // 麦克风开始后这么久没有数据 → 认为卡住（共享模式麦克风静音时也会持续给数据）
        private const double StableSec = 10;           // 稳定运行这么久才清除失败计数
        private const int BrokenFailures = 3;          // 指定设备连续失败这么多次 → 暂时改录默认设备
        private const double BrokenWindowSec = 30;

        /// <summary>
        /// 进程回环在应用不出声时是否也持续给数据。为 true 时才对它做“没数据 = 卡死”检测。
        /// 实测（Win11 26200，自检第 12 项）：目标进程静音时每 10ms 仍有一个数据包，与麦克风相同。
        /// </summary>
        internal static bool ProcessLoopbackIsContinuous = true;

        private static readonly CaptureSource[] Empty = new CaptureSource[0];
        private static readonly IAudioSink[] NoSinks = new IAudioSink[0];

        // ---- 配置（UI 线程写，工作线程读）----
        private volatile SourceSpec _sysSpec = new SourceSpec { Mode = SourceMode.Default };
        private volatile SourceSpec _micSpec = new SourceSpec { Mode = SourceMode.Default };
        public volatile float SysGain = 1f, MicGain = 1f;
        public volatile bool SysEnabled = true, MicEnabled = true;

        // ---- 采集源（调度线程写，混音线程读整组数组）----
        private volatile CaptureSource[] _sys = Empty, _mic = Empty;

        private sealed class Backoff
        {
            public DateTime Until;
            public int Count;
            public DateTime LastFail;
            public string Reason;
        }
        private readonly Dictionary<string, Backoff> _backoff = new Dictionary<string, Backoff>();

        // ---- 线程 ----
        private Thread _supervisor, _mixer;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile bool _running;
        private volatile bool _devicesChanged;
        private volatile bool _specChanged = true;

        // ---- 混音状态 ----
        private long _produced;
        private float _limEnv, _limEnvSys, _limEnvMic;
        private readonly object _lvlLock = new object();
        private float _sysPeak, _micPeak;

        // ---- 录音输出（可同时有多个：上一段收尾时下一段已开始）----
        private readonly object _sinkLock = new object();
        private volatile IAudioSink[] _sinks = NoSinks;

        // ---- 状态 ----
        public volatile GroupStatus SysStatus = new GroupStatus { State = GroupState.Ok };
        public volatile GroupStatus MicStatus = new GroupStatus { State = GroupState.Ok };
        private string _lastSysResolve, _lastMicResolve;
        private GroupState _lastSysState = GroupState.Ok, _lastMicState = GroupState.Ok;

        public event Action<string, NoticeKind> Notice;
        public event Action StatusChanged;

        public bool IsRunning => _running;

        // ==================================================================
        //  公共接口
        // ==================================================================

        public void Start()
        {
            if (_running) return;
            _running = true;
            Interlocked.Exchange(ref _produced, (long)EngineClock.NowFrames - LatencyFrames);
            _specChanged = true;

            _mixer = NewMixerThread();
            _supervisor = new Thread(SupervisorLoop) { IsBackground = true, Name = "AudioSupervisor" };
            _supervisor.SetApartmentState(ApartmentState.MTA);
            _mixer.Start();
            _supervisor.Start();
            Log.Info("音频引擎启动");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _wake.Set();
            _supervisor?.Join(8000);
            _mixer?.Join(3000);
            IAudioSink[] sinks;
            lock (_sinkLock) { sinks = _sinks; _sinks = NoSinks; }
            foreach (var s in sinks) { try { s.EngineStopped(); } catch { } }
            Log.Info("音频引擎停止");
        }

        public void SetSources(SourceSpec sys, SourceSpec mic)
        {
            _sysSpec = sys;
            _micSpec = mic;
            _specChanged = true;
            _wake.Set();
        }

        /// <summary>请求调度线程立即重新检查（例如用户刚打开下拉框）。</summary>
        public void Poke() => _wake.Set();

        /// <summary>读取并清零自上次读取以来的峰值（线性幅度，已乘增益）。</summary>
        public void ReadLevels(out float sys, out float mic)
        {
            lock (_lvlLock)
            {
                sys = _sysPeak; mic = _micPeak;
                _sysPeak = 0; _micPeak = 0;
            }
        }

        public void AttachSink(IAudioSink sink)
        {
            lock (_sinkLock) _sinks = _sinks.Concat(new[] { sink }).ToArray();
        }

        public void DetachSink(IAudioSink sink)
        {
            lock (_sinkLock) _sinks = _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();
        }

        /// <summary>当前“现在”在时间轴上的位置（录音开始/停止点用它）。</summary>
        public static long NowPosition => (long)EngineClock.NowFrames;

        /// <summary>诊断用：各采集源的状态。</summary>
        public List<SourceStat> GetSourceStats() =>
            _sys.Concat(_mic).Select(s => new SourceStat
            {
                Key = s.Key, Kind = s.Kind, Started = s.Started, Packets = s.Packets, SecondsSinceData = s.SecondsSinceData,
            }).ToList();

        // ==================================================================
        //  调度线程
        // ==================================================================

        private void SupervisorLoop()
        {
            IMMDeviceEnumerator en = null;
            NotificationClient nc = new NotificationClient(() => { _devicesChanged = true; _wake.Set(); });
            int consecutiveErrors = 0;

            while (_running)
            {
                _wake.WaitOne(NextWaitMs());
                if (!_running) break;
                if (_devicesChanged)
                {
                    // 设备变化往往是一串通知，稍等一下让它们结束
                    Thread.Sleep(200);
                    _devicesChanged = false;
                }

                try
                {
                    if (en == null)
                    {
                        en = CoreAudioUtil.CreateEnumerator();
                        int hr = en.RegisterEndpointNotificationCallback(nc);
                        if (hr < 0) Log.Warn($"注册设备通知失败 (0x{hr:X8})");
                    }
                    Reconcile(en);
                    consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    // 枚举器可能已失效（例如音频服务重启）：丢弃，下一轮重建；本轮不改动任何采集源
                    consecutiveErrors++;
                    if (consecutiveErrors <= 3 || consecutiveErrors % 60 == 0) Log.Error("音源调度出错，将重建设备枚举器", ex);
                    DropEnumerator(ref en, nc);
                    if (consecutiveErrors > 1) Thread.Sleep(Math.Min(5000, 300 * consecutiveErrors));
                }

                // 看门狗：混音线程意外退出时重新拉起（不应发生，但录音工具要能自愈）
                if (_running && _mixer != null && !_mixer.IsAlive)
                {
                    Log.Warn("混音线程已退出，重新启动");
                    Interlocked.Exchange(ref _produced, (long)EngineClock.NowFrames - LatencyFrames);
                    _mixer = NewMixerThread();
                    _mixer.Start();
                }
            }

            DropEnumerator(ref en, nc);
            StopAll(ref _sys);
            StopAll(ref _mic);
        }

        private static void DropEnumerator(ref IMMDeviceEnumerator en, NotificationClient nc)
        {
            if (en == null) return;
            try { en.UnregisterEndpointNotificationCallback(nc); } catch { }
            CoreAudioUtil.Release(en);
            en = null;
        }

        /// <summary>下一次检查的等待时间：默认 1 秒，有退避即将到期时提前醒来。</summary>
        private int NextWaitMs()
        {
            double ms = 1000;
            var now = DateTime.UtcNow;
            foreach (var b in _backoff.Values)
            {
                double left = (b.Until - now).TotalMilliseconds;
                if (left > 0 && left < ms) ms = left;
            }
            return Math.Max(50, (int)ms + 10);
        }

        private Thread NewMixerThread()
        {
            var t = new Thread(MixerLoop) { IsBackground = true, Name = "Mixer", Priority = ThreadPriority.AboveNormal };
            t.SetApartmentState(ApartmentState.MTA);
            return t;
        }

        private struct Want
        {
            public SourceKind Kind;
            public string DeviceId;
            public int Pid;
            public string Key => CaptureSource.MakeKey(Kind, DeviceId, Pid);
        }

        private void Reconcile(IMMDeviceEnumerator en)
        {
            bool specChanged = _specChanged;
            _specChanged = false;
            var sysSpec = _sysSpec;
            var micSpec = _micSpec;

            // ---------- 电脑声音 ----------
            var sysWant = new List<Want>();
            var sysStatus = new GroupStatus();
            string sysResolve;
            switch (sysSpec.Mode)
            {
                case SourceMode.AllOutputs:
                {
                    var outs = DeviceCatalog.ListDevices(en, EDataFlow.Render, null, strict: true);
                    foreach (var d in outs) sysWant.Add(new Want { Kind = SourceKind.Loopback, DeviceId = d.Id });
                    sysStatus.State = outs.Count > 0 ? GroupState.Ok : GroupState.NoDevice;
                    sysStatus.Detail = outs.Count > 0 ? $"共 {outs.Count} 个播放设备" : "未找到播放设备";
                    sysResolve = "all:" + string.Join("|", outs.Select(o => o.Id));
                    break;
                }
                case SourceMode.App:
                {
                    if (!Native.SupportsProcessLoopback)
                    {
                        sysStatus.State = GroupState.Unsupported;
                        sysStatus.Detail = "需要 Windows 10 2004 或更新版本";
                        sysResolve = "unsupported";
                        break;
                    }
                    var roots = ProcessTable.FindRoots(ProcessTable.Snapshot(), sysSpec.Id);
                    foreach (int pid in roots) sysWant.Add(new Want { Kind = SourceKind.Process, Pid = pid });
                    sysStatus.State = roots.Count > 0 ? GroupState.Ok : GroupState.Waiting;
                    sysStatus.Detail = roots.Count > 0 ? null : "等待启动";
                    sysResolve = roots.Count > 0 ? "app:" + string.Join(",", roots) : "waiting";
                    break;
                }
                default:
                    sysResolve = ResolveDevice(en, EDataFlow.Render, sysSpec, sysWant, sysStatus, SourceKind.Loopback);
                    break;
            }

            // ---------- 麦克风 ----------
            var micWant = new List<Want>();
            var micStatus = new GroupStatus();
            var micSpecEff = micSpec.Mode == SourceMode.Device ? micSpec : new SourceSpec { Mode = SourceMode.Default };
            string micResolve = ResolveDevice(en, EDataFlow.Capture, micSpecEff, micWant, micStatus, SourceKind.Microphone);

            // ---------- 应用到采集源 ----------
            var sys = _sys; Apply(ref sys, sysWant, sysStatus); _sys = sys;
            var mic = _mic; Apply(ref mic, micWant, micStatus); _mic = mic;
            CleanupBackoff(sysWant.Concat(micWant).Select(w => w.Key));

            // ---------- 通知 ----------
            if (!specChanged)
            {
                AnnounceSys(sysSpec, sysStatus, sysResolve);
                AnnounceMic(micSpecEff, micStatus, micResolve);
            }
            _lastSysResolve = sysResolve; _lastSysState = sysStatus.State;
            _lastMicResolve = micResolve; _lastMicState = micStatus.State;

            bool changed = !SameStatus(SysStatus, sysStatus) || !SameStatus(MicStatus, micStatus);
            SysStatus = sysStatus;
            MicStatus = micStatus;
            if (changed || specChanged) StatusChanged?.Invoke();
        }

        /// <summary>
        /// 解析“默认设备 / 指定设备”。指定设备没连接、或在但反复打不开时，暂时改录默认设备。
        /// 返回一个代表解析结果的字符串，用于判断是否变化。
        /// </summary>
        private string ResolveDevice(IMMDeviceEnumerator en, EDataFlow flow, SourceSpec spec,
                                     List<Want> want, GroupStatus status, SourceKind kind)
        {
            string noneText = flow == EDataFlow.Render ? "未找到播放设备" : "未找到麦克风";
            string defId = DeviceCatalog.GetDefaultIdStrict(en, flow, ERole.Console);

            if (spec.Mode == SourceMode.Device)
            {
                bool active = DeviceCatalog.IsDeviceActiveStrict(en, spec.Id);
                string key = CaptureSource.MakeKey(kind, spec.Id, 0);
                bool broken = active && IsBroken(key) && defId != null && defId != spec.Id;
                if (active && !broken)
                {
                    want.Add(new Want { Kind = kind, DeviceId = spec.Id });
                    status.State = GroupState.Ok;
                    status.Detail = DeviceCatalog.GetDeviceName(en, spec.Id);
                    return "dev:" + spec.Id;
                }
                if (defId == null)
                {
                    status.State = GroupState.NoDevice;
                    status.Detail = noneText;
                    return "none";
                }
                want.Add(new Want { Kind = kind, DeviceId = defId });
                status.State = GroupState.Fallback;
                status.FallbackBroken = broken;
                status.Detail = DeviceCatalog.GetDeviceName(en, defId);
                return (broken ? "broken:" : "fallback:") + defId;
            }

            if (defId == null)
            {
                status.State = GroupState.NoDevice;
                status.Detail = noneText;
                return "none";
            }
            want.Add(new Want { Kind = kind, DeviceId = defId });
            status.State = GroupState.Ok;
            status.Detail = DeviceCatalog.GetDeviceName(en, defId);
            return "default:" + defId;
        }

        private bool IsBroken(string key) =>
            _backoff.TryGetValue(key, out var b) && b.Count >= BrokenFailures &&
            (DateTime.UtcNow - b.LastFail).TotalSeconds < BrokenWindowSec;

        private void RecordFailure(string key, string reason)
        {
            var now = DateTime.UtcNow;
            if (!_backoff.TryGetValue(key, out var b)) { b = new Backoff(); _backoff[key] = b; }
            b.Count = Math.Min(b.Count + 1, 50);
            b.LastFail = now;
            b.Reason = reason ?? b.Reason;
            double wait = Math.Min(10, 0.5 * Math.Pow(2, Math.Min(b.Count, 6) - 1)); // 0.5,1,2,4,8,10 秒
            b.Until = now.AddSeconds(wait);
        }

        private void CleanupBackoff(IEnumerable<string> wantedKeys)
        {
            var wanted = new HashSet<string>(wantedKeys);
            var now = DateTime.UtcNow;
            foreach (var k in _backoff.Keys.ToList())
                if (!wanted.Contains(k) && (now - _backoff[k].LastFail).TotalSeconds > 120) _backoff.Remove(k);
        }

        private void Apply(ref CaptureSource[] current, List<Want> want, GroupStatus status)
        {
            var now = DateTime.UtcNow;
            var keep = new List<CaptureSource>();
            bool changed = false;

            foreach (var s in current)
            {
                bool wanted = want.Any(w => w.Key == s.Key);
                if (!wanted) { s.Stop(); changed = true; continue; }

                // 失败（线程已退出）
                if (s.Failed)
                {
                    RecordFailure(s.Key, s.FailReason);
                    s.Stop();
                    changed = true;
                    continue;
                }
                // 卡住：迟迟没能开始
                if (!s.Started && s.SecondsSinceCreated > StartTimeoutSec)
                {
                    s.Abandon("打开设备超时");
                    RecordFailure(s.Key, "打开设备超时");
                    changed = true;
                    continue;
                }
                // 卡住：开始了但不再给数据（只对“静音时也会持续给数据”的源有效）
                bool continuous = s.Kind == SourceKind.Microphone || (s.Kind == SourceKind.Process && ProcessLoopbackIsContinuous);
                if (s.Started && continuous && s.SecondsSinceData > MicStallSec)
                {
                    s.Abandon($"{s.SecondsSinceData:F1} 秒没有收到数据");
                    RecordFailure(s.Key, "设备没有响应");
                    changed = true;
                    continue;
                }
                // 稳定运行一段时间后清除失败计数
                if (s.Started && s.SecondsSinceCreated > StableSec) _backoff.Remove(s.Key);
                keep.Add(s);
            }

            // 启动缺少的
            foreach (var w in want)
            {
                if (keep.Any(s => s.Key == w.Key)) continue;
                if (_backoff.TryGetValue(w.Key, out var b) && b.Until > now) continue;
                var src = new CaptureSource(w.Kind, w.DeviceId, w.Pid, Interlocked.Read(ref _produced))
                {
                    OnFailed = () => _wake.Set(),
                };
                src.Start();
                keep.Add(src);
                changed = true;
            }

            // 状态：只把真正开始采集的源算作“在录”
            int started = keep.Count(s => s.Started);
            int starting = keep.Count(s => !s.Started);
            status.ActiveSources = started;
            bool expectRecording = status.State == GroupState.Ok || status.State == GroupState.Fallback;
            if (expectRecording && want.Count > 0 && started == 0 && starting == 0)
            {
                // 所有想要的源都没能开始：连续失败 2 次以上才报错（一次偶发失败通常马上就恢复了）
                var failing = want.Select(w => _backoff.TryGetValue(w.Key, out var bb) ? bb : null).Where(bb => bb != null).ToList();
                if (failing.Count > 0 && failing.Max(bb => bb.Count) >= 2)
                {
                    status.State = GroupState.Error;
                    status.Detail = FriendlyError(failing.OrderByDescending(bb => bb.LastFail).First().Reason);
                }
            }

            if (changed) current = keep.ToArray();
        }

        private static string FriendlyError(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "无法打开设备";
            if (raw.Contains("0x88890004")) return "设备已断开";
            if (raw.Contains("0x80070005")) return "无权访问（Windows 隐私设置 → 麦克风）";
            if (raw.Contains("0x8889000A")) return "设备被占用";
            if (raw.Contains("超时") || raw.Contains("没有响应")) return "设备无响应";
            return "无法打开设备";
        }

        private void AnnounceSys(SourceSpec spec, GroupStatus st, string resolve)
        {
            if (resolve == _lastSysResolve && st.State == _lastSysState) return;
            switch (spec.Mode)
            {
                case SourceMode.App:
                    if (st.State == GroupState.Ok && _lastSysState == GroupState.Waiting)
                        Raise($"已连接「{spec.Label ?? spec.Id}」", NoticeKind.Ok);
                    else if (st.State == GroupState.Waiting && _lastSysState == GroupState.Ok)
                        Raise($"「{spec.Label ?? spec.Id}」已退出", NoticeKind.Warn);
                    break;
                case SourceMode.Device:
                    if (st.State == GroupState.Fallback && _lastSysState != GroupState.Fallback)
                        Raise(st.FallbackBroken
                            ? $"「{spec.Label}」无法打开，已改用「{st.Detail}」"
                            : $"「{spec.Label}」已断开，已改用「{st.Detail}」", NoticeKind.Warn);
                    else if (st.State == GroupState.Ok && _lastSysState == GroupState.Fallback)
                        Raise($"「{spec.Label}」已恢复", NoticeKind.Ok);
                    break;
                case SourceMode.Default:
                    if (st.State == GroupState.Ok && _lastSysState == GroupState.Ok && resolve != _lastSysResolve)
                        Raise($"播放设备已切换为「{st.Detail}」", NoticeKind.Info);
                    break;
            }
            AnnounceCommon("电脑声音", "播放设备", st, _lastSysState);
        }

        private void AnnounceMic(SourceSpec spec, GroupStatus st, string resolve)
        {
            if (resolve == _lastMicResolve && st.State == _lastMicState) return;
            string label = _micSpec.Label;
            if (spec.Mode == SourceMode.Device)
            {
                if (st.State == GroupState.Fallback && _lastMicState != GroupState.Fallback)
                    Raise(st.FallbackBroken
                        ? $"「{label}」无法打开，已改用「{st.Detail}」"
                        : $"「{label}」已断开，已改用「{st.Detail}」", NoticeKind.Warn);
                else if (st.State == GroupState.Ok && _lastMicState == GroupState.Fallback)
                    Raise($"「{label}」已恢复", NoticeKind.Ok);
            }
            else if (st.State == GroupState.Ok && _lastMicState == GroupState.Ok && resolve != _lastMicResolve)
            {
                Raise($"麦克风已切换为「{st.Detail}」", NoticeKind.Info);
            }
            AnnounceCommon("麦克风", "麦克风", st, _lastMicState);
        }

        private void AnnounceCommon(string group, string deviceWord, GroupStatus st, GroupState last)
        {
            if (st.State == GroupState.NoDevice && last != GroupState.NoDevice)
                Raise($"未找到{deviceWord}", NoticeKind.Warn);
            else if (st.State == GroupState.Error && last != GroupState.Error)
                Raise($"{group}：{st.Detail}", NoticeKind.Warn);
            else if (last == GroupState.Error && (st.State == GroupState.Ok || st.State == GroupState.Fallback))
                Raise($"{group}已恢复", NoticeKind.Ok);
            else if (last == GroupState.NoDevice && st.State == GroupState.Ok)
                Raise($"{group}已恢复", NoticeKind.Ok);
        }

        private void Raise(string msg, NoticeKind kind)
        {
            Log.Info("提示: " + msg);
            try { Notice?.Invoke(msg, kind); } catch (Exception ex) { Log.Error("Notice 回调出错", ex); }
        }

        private static bool SameStatus(GroupStatus a, GroupStatus b) =>
            a != null && b != null && a.State == b.State && a.Detail == b.Detail &&
            a.ActiveSources == b.ActiveSources && a.FallbackBroken == b.FallbackBroken;

        private static void StopAll(ref CaptureSource[] arr)
        {
            var old = arr;
            arr = Empty;
            foreach (var s in old) s.Stop();
        }

        // ==================================================================
        //  混音线程
        // ==================================================================

        private void MixerLoop()
        {
            IntPtr mmcss = IntPtr.Zero;
            try { int ti = 0; mmcss = Native.AvSetMmThreadCharacteristicsW("Audio", ref ti); } catch { }

            var sysBuf = new float[ChunkFrames * 2];
            var micBuf = new float[ChunkFrames * 2];
            var mixBuf = new float[ChunkFrames * 2];
            const float limitCeil = 0.97f;
            float release = (float)Math.Exp(-1.0 / (0.08 * EngineClock.SampleRate)); // 80ms 释放

            try
            {
                while (_running)
                {
                    Thread.Sleep(10);
                    long target = (long)EngineClock.NowFrames - LatencyFrames;
                    long produced = Interlocked.Read(ref _produced);

                    if (target - produced > EngineClock.SampleRate * 2)
                    {
                        // 线程长时间没被调度（系统休眠、卡顿）：跳过这段，不补几小时的静音
                        Log.Warn($"混音落后 {(target - produced) / 48}ms，跳过");
                        produced = target - ChunkFrames;
                        Interlocked.Exchange(ref _produced, produced);
                    }

                    while (produced < target && _running)
                    {
                        int n = (int)Math.Min(target - produced, ChunkFrames);
                        int len = n * 2;
                        Array.Clear(sysBuf, 0, len);
                        Array.Clear(micBuf, 0, len);

                        foreach (var s in _sys) s.Timeline.ReadAdd(produced, sysBuf, n, 1f);
                        foreach (var s in _mic) s.Timeline.ReadAdd(produced, micBuf, n, 1f);

                        float gS = SysEnabled ? SysGain : 0f;
                        float gM = MicEnabled ? MicGain : 0f;
                        float pS = 0, pM = 0;
                        if (!IsFinite(_limEnv)) _limEnv = 0;
                        if (!IsFinite(_limEnvSys)) _limEnvSys = 0;
                        if (!IsFinite(_limEnvMic)) _limEnvMic = 0;

                        for (int i = 0; i < len; i += 2)
                        {
                            float sl = sysBuf[i] * gS, sr = sysBuf[i + 1] * gS;
                            // 麦克风统一成单声道放在两边（有些麦只有一个声道有声音）
                            float m = (micBuf[i] + micBuf[i + 1]) * 0.5f * gM;

                            float al = Math.Abs(sl), ar = Math.Abs(sr), am = Math.Abs(m);
                            if (al > pS) pS = al;
                            if (ar > pS) pS = ar;
                            if (am > pM) pM = am;

                            // 混音 + 峰值限幅（瞬时压缩、平滑释放），保证不爆音
                            float l = sl + m, r = sr + m;
                            float g = Limit(ref _limEnv, Math.Max(Math.Abs(l), Math.Abs(r)), limitCeil, release);
                            mixBuf[i] = l * g;
                            mixBuf[i + 1] = r * g;

                            // 分轨同样限幅（音量拉到 100% 以上时也不削波）
                            float gs = Limit(ref _limEnvSys, Math.Max(al, ar), limitCeil, release);
                            float gm = Limit(ref _limEnvMic, am, limitCeil, release);
                            sysBuf[i] = sl * gs; sysBuf[i + 1] = sr * gs;
                            micBuf[i] = m * gm; micBuf[i + 1] = m * gm;
                        }

                        var sinks = _sinks;
                        foreach (var sink in sinks)
                        {
                            try
                            {
                                sink.Write(produced, mixBuf, sysBuf, micBuf, n);
                                if (sink.IsFinished) DetachSink(sink);
                            }
                            catch (Exception ex)
                            {
                                Log.Error("录音写入出错", ex);
                                DetachSink(sink);
                                try { sink.EngineStopped(); } catch { }
                            }
                        }

                        produced += n;
                        Interlocked.Exchange(ref _produced, produced);
                        lock (_lvlLock)
                        {
                            if (pS > _sysPeak) _sysPeak = pS;
                            if (pM > _micPeak) _micPeak = pM;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("混音线程异常退出", ex);
                // 录音器不应因此卡住：通知它们收尾，已录内容会保存
                IAudioSink[] sinks;
                lock (_sinkLock) { sinks = _sinks; _sinks = NoSinks; }
                foreach (var s in sinks) { try { s.EngineStopped(); } catch { } }
            }
            finally
            {
                if (mmcss != IntPtr.Zero) { try { Native.AvRevertMmThreadCharacteristics(mmcss); } catch { } }
            }
        }

        private static float Limit(ref float env, float peak, float ceil, float release)
        {
            env = peak > env ? peak : env * release + peak * (1 - release);
            return env > ceil ? ceil / env : 1f;
        }

        private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        // ==================================================================

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class NotificationClient : IMMNotificationClient
        {
            private readonly Action _changed;
            public NotificationClient(Action changed) { _changed = changed; }
            public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
            public void OnDeviceAdded(string deviceId) => _changed();
            public void OnDeviceRemoved(string deviceId) => _changed();
            public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId)
            {
                if (role == ERole.Console) _changed();
            }
            public void OnPropertyValueChanged(string deviceId, PROPERTYKEY key) { }
        }
    }
}
