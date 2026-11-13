using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ShengJi.Core;
using ShengJi.Interop;

namespace ShengJi.Audio
{
    internal enum SourceKind
    {
        /// <summary>输出设备回环（录这个设备正在播放的所有声音）</summary>
        Loopback,
        /// <summary>麦克风等输入设备</summary>
        Microphone,
        /// <summary>按应用（进程树）回环</summary>
        Process,
    }

    /// <summary>
    /// 一路采集：独立线程从 WASAPI 读取数据，转换成 48k 立体声，按时间戳写入自己的 Timeline。
    /// 出错（设备拔出等）时线程退出并标记 Failed，由 AudioEngine 的调度线程负责重建。
    /// </summary>
    internal sealed class CaptureSource
    {
        public readonly SourceKind Kind;
        public readonly string DeviceId;     // Loopback / Microphone
        public readonly int ProcessId;       // Process
        public readonly string Key;
        public readonly Timeline Timeline;
        /// <summary>电平表（随数据到达即时更新，不受混音延迟影响）。</summary>
        public readonly LoudnessMeter Meter;

        public volatile bool Failed;
        public volatile bool Started;        // 已成功开始采集
        public volatile string FailReason;
        public int FailHResult;

        /// <summary>出错退出时回调（用来立即唤醒调度线程）。在采集线程上调用。</summary>
        public Action OnFailed;

        /// <summary>
        /// 写入时间轴时整体往后挪的帧数（人声对齐：电脑声音按耳机 + 麦克风的实测延迟后移）。
        /// 可随时修改，下一批数据会重新对齐到新位置。
        /// </summary>
        public volatile int DelayFrames;
        private int _appliedDelay;

        private readonly long _createdTicks = Stopwatch.GetTimestamp();
        private long _lastDataTicks;         // 最近一次收到数据包的时间（Stopwatch ticks）
        private long _packets;

        private Thread _thread;
        private volatile bool _stop;

        // 时间轴对齐状态（仅采集线程访问）
        private readonly Resampler _resampler = new Resampler();
        private long _writePos;
        private bool _hasPos;
        private double _lastDeliver = double.NegativeInfinity;
        private double _errEma;
        private float[] _stage = new float[48000];   // 立体声 float，源采样率
        private float[] _out = new float[48000];

        private const double GapFrames = 0.25 * EngineClock.SampleRate;     // 超过 250ms 没数据视为断档
        private const double ResyncFrames = 0.12 * EngineClock.SampleRate;  // 偏差超过 120ms 直接重新对齐
        private const double QpcGapFrames = 0.015 * EngineClock.SampleRate; // 时间戳可信时，超过 15ms 的空档直接跳过去

        public CaptureSource(SourceKind kind, string deviceId, int processId, long startReadPos)
        {
            Kind = kind;
            DeviceId = deviceId;
            ProcessId = processId;
            Key = MakeKey(kind, deviceId, processId);
            Timeline = new Timeline(EngineClock.SampleRate * 4, startReadPos);
            Meter = new LoudnessMeter(mono: kind == SourceKind.Microphone);
        }

        public static string MakeKey(SourceKind kind, string deviceId, int pid) =>
            kind == SourceKind.Process ? "proc:" + pid : (kind == SourceKind.Loopback ? "loop:" : "mic:") + deviceId;

        public long Packets => Interlocked.Read(ref _packets);

        public double SecondsSinceCreated => (Stopwatch.GetTimestamp() - _createdTicks) / (double)Stopwatch.Frequency;

        /// <summary>距离最近一次收到数据的秒数；还没收到过数据时返回距离创建的秒数。</summary>
        public double SecondsSinceData
        {
            get
            {
                long t = Interlocked.Read(ref _lastDataTicks);
                if (t == 0) t = _createdTicks;
                return (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;
            }
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Capture " + Key, Priority = ThreadPriority.AboveNormal };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        /// <summary>停止并等待线程退出（最多 3 秒）。</summary>
        public void Stop()
        {
            _stop = true;
            if (_thread != null && !_thread.Join(3000))
                Log.Warn("采集线程未能及时退出，放弃等待: " + Key);
        }

        /// <summary>
        /// 放弃一个卡住的采集源：只通知线程退出，不等待（驱动卡死时线程可能永远回不来）。
        /// 调用方会立即创建新的采集源替代它。
        /// </summary>
        public void Abandon(string reason)
        {
            _stop = true;
            FailReason = reason;
            Failed = true;
            Log.Warn($"放弃采集源 {Key}: {reason}");
        }

        // ------------------------------------------------------------------

        private void Run()
        {
            IntPtr mmcss = IntPtr.Zero;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            IntPtr evt = IntPtr.Zero;
            try
            {
                int taskIndex = 0;
                try { mmcss = Native.AvSetMmThreadCharacteristicsW("Audio", ref taskIndex); } catch { }

                evt = Native.CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
                SourceFormat fmt;
                bool useEvent;
                client = OpenClient(evt, out fmt, out useEvent);
                if (_stop) return;

                var iid = CoreAudioConst.IID_IAudioCaptureClient;
                Check(client.GetService(ref iid, out object capObj), "GetService");
                capture = (IAudioCaptureClient)capObj;

                Check(client.Start(), "Start");
                Started = true;
                Log.Info($"开始采集 {Key} 格式 {fmt} 事件驱动={useEvent}");

                double ratioBase = (double)EngineClock.SampleRate / fmt.SampleRate;
                while (!_stop)
                {
                    if (useEvent) Native.WaitForSingleObject(evt, 20);
                    else Thread.Sleep(10);
                    if (_stop) break;
                    Drain(capture, fmt, ratioBase);
                }
            }
            catch (Exception ex)
            {
                FailHResult = (ex as COMException)?.ErrorCode ?? ex.HResult;
                FailReason = ex.Message;
                if (!_stop)
                {
                    Log.Warn($"采集 {Key} 结束: {ex.Message}");
                    Failed = true;
                    try { OnFailed?.Invoke(); } catch { }
                }
            }
            finally
            {
                try { client?.Stop(); } catch { }
                CoreAudioUtil.Release(capture);
                CoreAudioUtil.Release(client);
                if (evt != IntPtr.Zero) Native.CloseHandle(evt);
                if (mmcss != IntPtr.Zero) { try { Native.AvRevertMmThreadCharacteristics(mmcss); } catch { } }
            }
        }

        /// <summary>
        /// 打开并初始化 IAudioClient。优先让系统直接转换成 48k 立体声 float；
        /// 不支持时退回设备原生格式（自己转换）；事件驱动不可用时退回轮询。
        /// </summary>
        private IAudioClient OpenClient(IntPtr evt, out SourceFormat fmt, out bool useEvent)
        {
            // 进程回环只接受 LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM 这几个标志，多给会返回 AUDCLNT_E_INVALID_STREAM_FLAG
            bool isProcess = Kind == SourceKind.Process;
            uint baseFlags = isProcess ? 0 : CoreAudioConst.AUDCLNT_STREAMFLAGS_NOPERSIST;
            if (Kind != SourceKind.Microphone) baseFlags |= CoreAudioConst.AUDCLNT_STREAMFLAGS_LOOPBACK;
            uint autoFlags = isProcess
                ? CoreAudioConst.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                : CoreAudioConst.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            const long bufferHns = 1_000_000; // 100ms

            // 尝试顺序：(格式 0=float48k 1=pcm16_48k 2=设备原生, 自动转换, 事件)
            var attempts = isProcess
                ? new[] { (fmt: 0, auto: true, ev: true), (fmt: 1, auto: true, ev: true), (fmt: 1, auto: true, ev: false) }
                : new[] { (fmt: 0, auto: true, ev: true), (fmt: 2, auto: false, ev: true), (fmt: 2, auto: false, ev: false), (fmt: 0, auto: true, ev: false) };

            int lastHr = 0;
            foreach (var a in attempts)
            {
                if (_stop) break;
                IAudioClient client = null;
                IntPtr pwfx = IntPtr.Zero;
                try
                {
                    client = Activate();
                    if (a.fmt == 0) pwfx = SourceFormat.AllocFloatStereo48k();
                    else if (a.fmt == 1) pwfx = SourceFormat.AllocPcm16Stereo48k();
                    else
                    {
                        int hrMix = client.GetMixFormat(out pwfx);
                        if (hrMix < 0 || pwfx == IntPtr.Zero) { lastHr = hrMix; CoreAudioUtil.Release(client); continue; }
                    }

                    uint flags = baseFlags;
                    if (a.auto) flags |= autoFlags;
                    if (a.ev) flags |= CoreAudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;

                    int hr = client.Initialize(CoreAudioConst.AUDCLNT_SHAREMODE_SHARED, flags, bufferHns, 0, pwfx, IntPtr.Zero);
                    if (hr >= 0 && a.ev) hr = client.SetEventHandle(evt);
                    if (hr >= 0)
                    {
                        fmt = SourceFormat.FromPointer(pwfx);
                        useEvent = a.ev;
                        return client;
                    }
                    lastHr = hr;
                    Log.Info($"初始化 {Key} 失败 (0x{hr:X8})，尝试下一种方式");
                }
                catch (Exception ex)
                {
                    lastHr = (ex as COMException)?.ErrorCode ?? ex.HResult;
                    // 设备都打不开了，没必要再试其它格式
                    if (lastHr == CoreAudioConst.AUDCLNT_E_DEVICE_INVALIDATED || lastHr == CoreAudioConst.E_NOTFOUND)
                    {
                        CoreAudioUtil.Release(client);
                        throw;
                    }
                    Log.Info($"初始化 {Key} 异常: {ex.Message}");
                }
                finally
                {
                    if (pwfx != IntPtr.Zero) Marshal.FreeCoTaskMem(pwfx);
                }
                CoreAudioUtil.Release(client);
            }
            throw new COMException($"无法打开音频流 (0x{lastHr:X8})", lastHr);
        }

        private IAudioClient Activate()
        {
            if (Kind == SourceKind.Process)
                return ProcessLoopbackActivator.Activate(ProcessId);

            IMMDeviceEnumerator en = null;
            IMMDevice dev = null;
            try
            {
                en = CoreAudioUtil.CreateEnumerator();
                Check(en.GetDevice(DeviceId, out dev), "GetDevice");
                var iid = CoreAudioConst.IID_IAudioClient;
                Check(dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out object o), "Activate");
                return (IAudioClient)o;
            }
            finally
            {
                CoreAudioUtil.Release(dev);
                CoreAudioUtil.Release(en);
            }
        }

        /// <summary>读空设备缓冲区中的所有数据包，合并后一次性写入时间轴。</summary>
        private void Drain(IAudioCaptureClient cap, SourceFormat fmt, double ratioBase)
        {
            int staged = 0;
            ulong firstQpc = 0;
            bool discontinuity = false;

            while (true)
            {
                Check(cap.GetNextPacketSize(out uint next), "GetNextPacketSize");
                if (next == 0) break;

                int hr = cap.GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpc);
                if (hr == CoreAudioConst.AUDCLNT_S_BUFFER_EMPTY) break;
                Check(hr, "GetBuffer");
                try
                {
                    int n = (int)frames;
                    EnsureStage(staged + n);
                    if ((flags & CoreAudioConst.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero)
                        Array.Clear(_stage, staged * 2, n * 2);
                    else
                        SampleConverter.ToStereoFloat(data, n, fmt, _stage, staged);

                    if (staged == 0 && (flags & CoreAudioConst.AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) == 0)
                        firstQpc = qpc;
                    if ((flags & CoreAudioConst.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0)
                        discontinuity = true;
                    staged += n;
                }
                finally
                {
                    cap.ReleaseBuffer(frames);
                }
                if (staged > fmt.SampleRate) break; // 单次最多 1 秒，防御性限制
            }

            if (staged > 0)
            {
                Interlocked.Exchange(ref _lastDataTicks, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _packets);
                Deliver(staged, firstQpc, discontinuity, ratioBase);
            }
        }

        private void Deliver(int frames, ulong firstQpc, bool discontinuity, double ratioBase)
        {
            double now = EngineClock.NowFrames;
            double outLen = frames * ratioBase;

            // 估算这批数据在时间轴上的起点：优先用设备给的 QPC 时间戳，不可信时用到达时间倒推
            double est = now - outLen;
            bool fromQpc = false;
            if (firstQpc != 0)
            {
                double q = EngineClock.QpcToFrames(firstQpc);
                if (Math.Abs(q - est) < 0.3 * EngineClock.SampleRate) { est = q; fromQpc = true; }
            }

            // 人声对齐：整体后移；数值变了就当作断档，直接对齐到新位置
            int delay = DelayFrames;
            est += delay;
            bool gap = !_hasPos || (now - _lastDeliver) > GapFrames || delay != _appliedDelay;
            _appliedDelay = delay;
            double err = est - _writePos;
            if (gap || Math.Abs(err) > ResyncFrames)
            {
                // 首包 / 长时间没数据 / 大偏差：完全重新对齐
                if (_hasPos && !gap) Log.Info($"{Key} 时间偏差 {err / 48.0:F1}ms，重新对齐");
                _writePos = (long)Math.Round(est);
                _errEma = 0;
                _resampler.Reset();
                _hasPos = true;
            }
            else if (discontinuity || (fromQpc && err > QpcGapFrames))
            {
                // 中间丢了一小段（数据来晚了）：直接跳到正确位置，中间留静音；
                // 不重置重采样器，避免多余的咔嗒声。变速只用来修正很小的时钟漂移。
                if (err > 0) _writePos = (long)Math.Round(est);
                _errEma = 0;
            }
            else
            {
                _errEma += 0.05 * (err - _errEma);
            }

            // 漂移补偿：根据平滑后的偏差微调重采样速率（最多 ±0.3%，人耳无法察觉）
            double adj = 1.0 + Math.Max(-0.003, Math.Min(0.003, _errEma / (EngineClock.SampleRate * 6.0)));
            double ratio = ratioBase * adj;

            int need = (int)(frames * ratio) + 8;
            if (_out.Length < need * 2) _out = new float[need * 2];
            int outFrames = _resampler.Process(_stage, frames, ratio, _out);

            Timeline.Write(_writePos, _out, outFrames);
            Meter.Process(_out, outFrames);
            _writePos += outFrames;
            _lastDeliver = now;
        }

        private void EnsureStage(int frames)
        {
            if (_stage.Length >= frames * 2) return;
            var n = new float[Math.Max(frames * 2, _stage.Length * 2)];
            Array.Copy(_stage, n, _stage.Length);
            _stage = n;
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException($"{what} 失败 (0x{hr:X8})", hr);
        }
    }

    /// <summary>进程回环（按应用录制）的异步激活。</summary>
    internal static class ProcessLoopbackActivator
    {
        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public IntPtr Params, Variant;
            private int _state; // 0 = 等待中，1 = 已完成，2 = 调用方已超时放弃

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
            {
                // 回调参数会让 RCW 计数 +1，这里释放掉，避免激活结果（IAudioClient）一直挂到 GC
                try { if (op != null) Marshal.ReleaseComObject(op); } catch { }
                if (Interlocked.CompareExchange(ref _state, 1, 0) == 2)
                {
                    // 调用方已经超时离开：参数内存由这里释放（系统在回调前可能还会读取它们）
                    FreeParams();
                    Done.Dispose();
                    return;
                }
                Done.Set();
            }

            /// <summary>调用方超时。返回 true 表示回调尚未发生，资源交给回调释放。</summary>
            public bool MarkTimedOut() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

            public void FreeParams()
            {
                if (Variant != IntPtr.Zero) { Marshal.FreeHGlobal(Variant); Variant = IntPtr.Zero; }
                if (Params != IntPtr.Zero) { Marshal.FreeHGlobal(Params); Params = IntPtr.Zero; }
            }
        }

        public static IAudioClient Activate(int pid)
        {
            if (!Native.SupportsProcessLoopback)
                throw new NotSupportedException("当前 Windows 版本不支持按应用录制（需要 Win10 2004 或更新版本）");

            var handler = new Handler { Params = Marshal.AllocHGlobal(12), Variant = Marshal.AllocHGlobal(32) };
            IActivateAudioInterfaceAsyncOperation op = null;
            bool handedOff = false;
            try
            {
                // AUDIOCLIENT_ACTIVATION_PARAMS
                Marshal.WriteInt32(handler.Params, 0, CoreAudioConst.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK);
                Marshal.WriteInt32(handler.Params, 4, pid);
                Marshal.WriteInt32(handler.Params, 8, CoreAudioConst.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE);

                // PROPVARIANT { vt = VT_BLOB, blob = { cbSize, pBlobData } }
                for (int i = 0; i < 32; i++) Marshal.WriteByte(handler.Variant, i, 0);
                Marshal.WriteInt16(handler.Variant, 0, (short)CoreAudioConst.VT_BLOB);
                Marshal.WriteInt32(handler.Variant, 8, 12);
                Marshal.WriteIntPtr(handler.Variant, IntPtr.Size == 8 ? 16 : 12, handler.Params);

                int hr = MmDevApi.ActivateAudioInterfaceAsync(
                    CoreAudioConst.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                    CoreAudioConst.IID_IAudioClient, handler.Variant, handler, out op);
                if (hr < 0) throw new COMException($"ActivateAudioInterfaceAsync 失败 (0x{hr:X8})", hr);

                if (!handler.Done.WaitOne(5000))
                {
                    if (handler.MarkTimedOut()) handedOff = true; // 回调稍后到来时由它释放参数
                    throw new TimeoutException("激活按应用录制超时");
                }

                hr = op.GetActivateResult(out int activateHr, out object iface);
                if (hr < 0) throw new COMException($"GetActivateResult 失败 (0x{hr:X8})", hr);
                if (activateHr < 0) throw new COMException($"按应用录制激活失败 (0x{activateHr:X8})", activateHr);
                return (IAudioClient)iface;
            }
            finally
            {
                CoreAudioUtil.Release(op);
                if (!handedOff)
                {
                    handler.FreeParams();
                    handler.Done.Dispose();
                }
            }
        }
    }
}
