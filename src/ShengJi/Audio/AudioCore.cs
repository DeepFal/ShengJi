using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShengJi.Audio
{
    /// <summary>
    /// 全局时钟：所有音源都映射到同一条 48kHz 的时间轴上（以帧为单位）。
    /// 基于 QueryPerformanceCounter，和 WASAPI 给出的 QPC 时间戳同源。
    /// </summary>
    internal static class EngineClock
    {
        public const int SampleRate = 48000;
        // 时间原点往前挪 1 小时，保证任何“现在 - 延迟”的位置都是正数（环形缓冲取模要求非负）
        private static readonly long _t0 = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 3600;
        private static readonly double _freq = Stopwatch.Frequency;

        /// <summary>当前时间（帧）。</summary>
        public static double NowFrames => (Stopwatch.GetTimestamp() - _t0) / _freq * SampleRate;

        /// <summary>把 WASAPI 的 QPC 时间戳（100ns 单位）换算成时间轴帧。</summary>
        public static double QpcToFrames(ulong qpc100ns)
        {
            double seconds = qpc100ns / 1e7;
            double t0Seconds = _t0 / _freq;
            return (seconds - t0Seconds) * SampleRate;
        }
    }

    /// <summary>
    /// 一个音源在时间轴上的环形缓冲（立体声 float）。
    /// 写入方：采集线程（按估算的时间位置写入）；读取方：混音线程（顺序读取，读完清零）。
    /// 没有写入过的位置就是静音——这样没声音时不出数据的回环设备也能对齐时间轴。
    /// </summary>
    internal sealed class Timeline
    {
        private readonly float[] _buf;
        private readonly int _capFrames;
        private readonly object _lock = new object();
        private long _readPos;

        public Timeline(int capacityFrames, long startReadPos)
        {
            _capFrames = capacityFrames;
            _buf = new float[capacityFrames * 2];
            _readPos = startReadPos;
        }

        public long ReadPos { get { lock (_lock) return _readPos; } }

        /// <summary>时间轴位置 → 缓冲区下标（对负数也安全）。</summary>
        private int Slot(long p)
        {
            long m = p % _capFrames;
            if (m < 0) m += _capFrames;
            return (int)m * 2;
        }

        /// <summary>写入 frames 帧（立体声交错）到时间轴位置 pos。超出可写窗口的部分丢弃。</summary>
        public void Write(long pos, float[] data, int frames)
        {
            lock (_lock)
            {
                long start = Math.Max(pos, _readPos);
                long end = Math.Min(pos + frames, _readPos + _capFrames);
                for (long p = start; p < end; p++)
                {
                    int src = (int)(p - pos) * 2;
                    int dst = Slot(p);
                    _buf[dst] = data[src];
                    _buf[dst + 1] = data[src + 1];
                }
            }
        }

        /// <summary>
        /// 读取时间轴区间 [pos, pos+frames)，乘以 gain 累加到 dst，并清零已读区域。
        /// 混音线程按顺序调用；若 pos 超前于本缓冲的读位置（例如音源刚加入），先丢弃中间的部分。
        /// </summary>
        public void ReadAdd(long pos, float[] dst, int frames, float gain)
        {
            lock (_lock)
            {
                if (pos > _readPos)
                {
                    long skip = Math.Min(pos - _readPos, _capFrames);
                    for (long p = _readPos; p < _readPos + skip; p++)
                    {
                        int idx = Slot(p);
                        _buf[idx] = 0f;
                        _buf[idx + 1] = 0f;
                    }
                    _readPos = pos;
                }
                // pos 落后于读位置的部分（理论上不会发生）视为静音
                int startI = (int)Math.Max(0, Math.Min(frames, _readPos - pos));
                for (int i = startI; i < frames; i++)
                {
                    int idx = Slot(pos + i);
                    dst[i * 2] += _buf[idx] * gain;
                    dst[i * 2 + 1] += _buf[idx + 1] * gain;
                    _buf[idx] = 0f;
                    _buf[idx + 1] = 0f;
                }
                _readPos = Math.Max(_readPos, pos + frames);
            }
        }

        /// <summary>跳到新的读位置（例如系统休眠恢复后），清空所有缓存。</summary>
        public void SkipTo(long pos)
        {
            lock (_lock)
            {
                Array.Clear(_buf, 0, _buf.Length);
                _readPos = pos;
            }
        }
    }

    /// <summary>
    /// 可变速率重采样器（立体声，4 点 Hermite 插值）。
    /// 用于：① 设备采样率 → 48k；② 微调速率，补偿不同硬件之间的时钟漂移。
    /// </summary>
    internal sealed class Resampler
    {
        // 保留最近 4 帧历史，保证跨包插值连续
        private float _l0, _l1, _l2, _l3, _r0, _r1, _r2, _r3;
        private double _phase;      // 下一个输出点相对 x1 的位置（0..1）
        private int _primed;

        public void Reset() { _primed = 0; _phase = 0; _l0 = _l1 = _l2 = _l3 = _r0 = _r1 = _r2 = _r3 = 0; }

        /// <summary>
        /// ratio = 输出采样率 / 输入采样率。返回输出帧数。
        /// output 至少要有 (frames * ratio + 4) 帧的空间。
        /// </summary>
        public int Process(float[] input, int frames, double ratio, float[] output)
        {
            double step = 1.0 / ratio;
            int outCount = 0;
            for (int i = 0; i < frames; i++)
            {
                // 推入新的输入帧
                _l0 = _l1; _l1 = _l2; _l2 = _l3; _l3 = input[i * 2];
                _r0 = _r1; _r1 = _r2; _r2 = _r3; _r3 = input[i * 2 + 1];
                if (_primed < 3) { _primed++; if (_primed < 3) continue; }

                // 在 x1..x2 区间内产生输出点
                while (_phase < 1.0)
                {
                    float t = (float)_phase;
                    output[outCount * 2] = Hermite(_l0, _l1, _l2, _l3, t);
                    output[outCount * 2 + 1] = Hermite(_r0, _r1, _r2, _r3, t);
                    outCount++;
                    _phase += step;
                }
                _phase -= 1.0;
            }
            return outCount;
        }

        private static float Hermite(float xm1, float x0, float x1, float x2, float t)
        {
            float c = (x1 - xm1) * 0.5f;
            float v = x0 - x1;
            float w = c + v;
            float a = w + v + (x2 - x0) * 0.5f;
            float b = w + a;
            return (((a * t) - b) * t + c) * t + x0;
        }
    }

    /// <summary>
    /// 电平表用的短时响度：ITU-R BS.1770 的 K 加权（削弱人耳不敏感的低频、略提升 2kHz 以上），
    /// 再做 25ms 的能量积分。比采样峰值更接近听感：敲键盘、喷麦这类短促声音不会把表顶满，
    /// 轻声和大声能明显区分。
    /// 在采集线程上随数据到达即时计算（不经过混音的 250ms 延迟），UI 线程读取。输入必须是 48kHz 立体声。
    /// </summary>
    internal sealed class LoudnessMeter
    {
        // BS.1770 给出的 48kHz 系数。第 1 级：高频搁架（模拟头部声学效应）；第 2 级：高通（RLB 加权）
        private const double A1b0 = 1.53512485958697, A1b1 = -2.69169618940638, A1b2 = 1.19839281085285,
                             A1a1 = -1.69065929318241, A1a2 = 0.73248077421585;
        private const double A2a1 = -1.99004745483398, A2a2 = 0.99007225036621;   // b = 1, -2, 1
        private const double Tau = 0.025;
        private static readonly double Alpha = 1 - Math.Exp(-1.0 / (Tau * EngineClock.SampleRate));

        private readonly bool _mono;
        // 滤波器状态（仅采集线程访问）：[声道] 第 1 级 z1 z2，第 2 级 z1 z2
        private double _l1, _l2, _l3, _l4, _r1, _r2, _r3, _r4;
        private double _env;

        private readonly object _lock = new object();
        private double _max;        // 上次读取以来 _env 的最大值
        private double _last;       // 最近一次数据结束时的 _env
        private long _lastTicks;
        private bool _fresh;

        /// <param name="mono">麦克风：先合成单声道（混音时会放在左右两边，按两个声道计）。</param>
        public LoudnessMeter(bool mono) { _mono = mono; }

        public void Process(float[] buf, int frames)
        {
            double env = _env, max = 0, a = Alpha;
            if (_mono)
            {
                for (int i = 0; i < frames; i++)
                {
                    double y = K((buf[i * 2] + buf[i * 2 + 1]) * 0.5, ref _l1, ref _l2, ref _l3, ref _l4);
                    env += a * (2 * y * y - env);
                    if (env > max) max = env;
                }
            }
            else
            {
                for (int i = 0; i < frames; i++)
                {
                    double yl = K(buf[i * 2], ref _l1, ref _l2, ref _l3, ref _l4);
                    double yr = K(buf[i * 2 + 1], ref _r1, ref _r2, ref _r3, ref _r4);
                    env += a * (yl * yl + yr * yr - env);
                    if (env > max) max = env;
                }
            }
            if (double.IsNaN(env) || double.IsInfinity(env) || double.IsInfinity(max))
            {
                Reset();
                env = max = 0;
            }
            else
            {
                // 静音时状态会衰减进非规格化数区间（运算很慢），直接归零
                if (env < 1e-20) env = 0;
                if (Math.Abs(_l1) + Math.Abs(_l2) + Math.Abs(_l3) + Math.Abs(_l4) < 1e-20) _l1 = _l2 = _l3 = _l4 = 0;
                if (Math.Abs(_r1) + Math.Abs(_r2) + Math.Abs(_r3) + Math.Abs(_r4) < 1e-20) _r1 = _r2 = _r3 = _r4 = 0;
            }
            _env = env;

            lock (_lock)
            {
                if (max > _max) _max = max;
                _last = _env;
                _lastTicks = Stopwatch.GetTimestamp();
                _fresh = true;
            }
        }

        /// <summary>
        /// 取出上次读取以来的最大短时能量（K 加权、各声道相加的均方值）。
        /// 期间没有新数据时按积分时间常数衰减——回环设备没声音时可能完全不出数据。
        /// </summary>
        public double Read()
        {
            lock (_lock)
            {
                double v;
                if (_fresh) v = _max;
                else
                {
                    double dt = (Stopwatch.GetTimestamp() - _lastTicks) / (double)Stopwatch.Frequency;
                    v = _last * Math.Exp(-dt / Tau);
                }
                _max = 0;
                _fresh = false;
                return v;
            }
        }

        private void Reset() => _l1 = _l2 = _l3 = _l4 = _r1 = _r2 = _r3 = _r4 = 0;

        // 两级双二阶滤波（转置直接 II 型）
        private static double K(double x, ref double s1, ref double s2, ref double s3, ref double s4)
        {
            double y1 = A1b0 * x + s1;
            s1 = A1b1 * x - A1a1 * y1 + s2;
            s2 = A1b2 * x - A1a2 * y1;
            double y2 = y1 + s3;
            s3 = -2 * y1 - A2a1 * y2 + s4;
            s4 = y1 - A2a2 * y2;
            return y2;
        }
    }

    /// <summary>采集到的原始格式描述（来自 WAVEFORMATEX / WAVEFORMATEXTENSIBLE）。</summary>
    internal sealed class SourceFormat
    {
        public int SampleRate;
        public int Channels;
        public int BitsPerSample;     // 容器位数
        public int BlockAlign;
        public bool IsFloat;

        private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");

        public static SourceFormat FromPointer(IntPtr pwfx)
        {
            var f = new SourceFormat();
            int tag = (ushort)Marshal.ReadInt16(pwfx, 0);
            f.Channels = (ushort)Marshal.ReadInt16(pwfx, 2);
            f.SampleRate = Marshal.ReadInt32(pwfx, 4);
            f.BlockAlign = (ushort)Marshal.ReadInt16(pwfx, 12);
            f.BitsPerSample = (ushort)Marshal.ReadInt16(pwfx, 14);
            if (tag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE
            {
                var bytes = new byte[16];
                Marshal.Copy(pwfx + 24, bytes, 0, 16);
                var sub = new Guid(bytes);
                f.IsFloat = sub == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
                if (!f.IsFloat && sub != KSDATAFORMAT_SUBTYPE_PCM)
                    throw new NotSupportedException("不支持的音频格式 " + sub);
            }
            else if (tag == 3) f.IsFloat = true;      // WAVE_FORMAT_IEEE_FLOAT
            else if (tag != 1) throw new NotSupportedException("不支持的音频格式 tag=" + tag);
            return f;
        }

        /// <summary>分配一个 WAVEFORMATEX（float32 立体声 48k），调用方负责 FreeCoTaskMem。</summary>
        public static IntPtr AllocFloatStereo48k() => AllocFormat(3, 2, 48000, 32);

        /// <summary>分配一个 WAVEFORMATEX（16bit PCM 立体声 48k）。</summary>
        public static IntPtr AllocPcm16Stereo48k() => AllocFormat(1, 2, 48000, 16);

        private static IntPtr AllocFormat(int tag, int channels, int rate, int bits)
        {
            IntPtr p = Marshal.AllocCoTaskMem(18);
            int block = channels * bits / 8;
            Marshal.WriteInt16(p, 0, (short)tag);
            Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * block);
            Marshal.WriteInt16(p, 12, (short)block);
            Marshal.WriteInt16(p, 14, (short)bits);
            Marshal.WriteInt16(p, 16, 0);
            return p;
        }

        public override string ToString() =>
            $"{SampleRate}Hz {Channels}ch {BitsPerSample}bit {(IsFloat ? "float" : "pcm")}";
    }

    /// <summary>把任意 PCM/float 多声道数据转换为立体声 float。</summary>
    internal static class SampleConverter
    {
        /// <summary>将 frames 帧从 src 转换为立体声 float 写入 dst（从 dstOffsetFrames 开始）。</summary>
        public static unsafe void ToStereoFloat(IntPtr src, int frames, SourceFormat fmt, float[] dst, int dstOffsetFrames)
        {
            int ch = fmt.Channels;
            int bytesPerSample = fmt.BlockAlign / Math.Max(1, ch);
            byte* p = (byte*)src;
            int o = dstOffsetFrames * 2;
            for (int i = 0; i < frames; i++)
            {
                byte* frame = p + (long)i * fmt.BlockAlign;
                float l, r;
                if (ch == 1)
                {
                    l = r = Read(frame, bytesPerSample, fmt.IsFloat);
                }
                else
                {
                    l = Read(frame, bytesPerSample, fmt.IsFloat);
                    r = Read(frame + bytesPerSample, bytesPerSample, fmt.IsFloat);
                    if (ch >= 3)
                    {
                        // 环绕声简单下混：中置 + 后置/侧置按 -3dB 混入
                        const float k = 0.7071f;
                        float c = Read(frame + bytesPerSample * 2, bytesPerSample, fmt.IsFloat) * k;
                        l += c; r += c;
                        if (ch >= 6)
                        {
                            l += Read(frame + bytesPerSample * 4, bytesPerSample, fmt.IsFloat) * k;
                            r += Read(frame + bytesPerSample * 5, bytesPerSample, fmt.IsFloat) * k;
                        }
                        if (ch >= 8)
                        {
                            l += Read(frame + bytesPerSample * 6, bytesPerSample, fmt.IsFloat) * k;
                            r += Read(frame + bytesPerSample * 7, bytesPerSample, fmt.IsFloat) * k;
                        }
                    }
                }
                // 有些应用/驱动会送出 NaN 或无穷大：一律当静音，避免污染混音和限幅器
                if (!(l > -1e4f && l < 1e4f)) l = 0f;
                if (!(r > -1e4f && r < 1e4f)) r = 0f;
                dst[o++] = l;
                dst[o++] = r;
            }
        }

        private static unsafe float Read(byte* s, int bytes, bool isFloat)
        {
            if (isFloat)
            {
                if (bytes == 4) return *(float*)s;
                if (bytes == 8) return (float)*(double*)s;
                return 0f;
            }
            switch (bytes)
            {
                case 2: return *(short*)s / 32768f;
                case 3: return ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f;
                case 4: return *(int*)s / 2147483648f;
                case 1: return (s[0] - 128) / 128f;
                default: return 0f;
            }
        }
    }
}
