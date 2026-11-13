using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ShengJi.Core;

namespace ShengJi.Audio
{
    internal sealed class CalibrationResult
    {
        public bool Ok;
        public int Ms;          // 人声对齐补偿值
        public string Error;    // 失败原因（给用户看的短句）
        public string Detail;   // 日志用
    }

    /// <summary>
    /// 人声对齐校准：往收听设备播放一串不等间隔的短扫频音，同时采集该设备的回环和麦克风（耳罩贴近麦克风），
    /// 用匹配滤波找出每一声在两路里的位置，差值就是「播放 → 耳朵 → 麦克风 → 时间戳」里 Windows 没报告的那段延迟。
    /// 实测（2.4G 无线耳麦 + 厂商降噪）各声之间的差异 &lt; 0.2ms。
    /// </summary>
    internal static class AlignmentCalibrator
    {
        public const int MaxMs = 1000;

        // 提示音时刻（秒），间隔不规则，避免把相邻两声认错
        private static readonly double[] Beeps = { 0.30, 1.01, 1.84, 2.46, 3.43, 4.09 };
        private const int Sr = EngineClock.SampleRate;
        private const int BeepLen = Sr / 20;            // 50ms
        private static readonly float[] Template = MakeChirp(1f);

        /// <summary>阻塞执行（约 7 秒），请在后台 MTA 线程调用。</summary>
        public static CalibrationResult Run(string renderId, string micId, CancellationToken cancel)
        {
            if (renderId == null || micId == null)
                return new CalibrationResult { Error = "没有可用的耳机或麦克风" };

            long start = (long)EngineClock.NowFrames;
            var loop = new CaptureSource(SourceKind.Loopback, renderId, 0, start);
            var mic = new CaptureSource(SourceKind.Microphone, micId, 0, start);
            Thread player = null;
            var playCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            string playError = null;
            try
            {
                loop.Start();
                mic.Start();
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!(loop.Started && mic.Started))
                {
                    if (loop.Failed || mic.Failed || wait.ElapsedMilliseconds > 5000 || cancel.IsCancellationRequested)
                        return new CalibrationResult { Error = "无法打开耳机或麦克风", Detail = loop.FailReason ?? mic.FailReason };
                    Thread.Sleep(20);
                }

                // 从现在开始收集两路时间轴（时间轴本身比实时晚 250ms 才完整）
                long pos = (long)EngineClock.NowFrames;
                loop.Timeline.SkipTo(pos);
                mic.Timeline.SkipTo(pos);
                var a = new List<float>(Sr * 8);
                var b = new List<float>(Sr * 8);
                var tmp = new float[4800 * 2];
                float[] signal = MakeSequence();
                player = new Thread(() =>
                {
                    try { RenderPlayer.Play(renderId, signal, playCancel.Token); }
                    catch (Exception ex) { playError = ex.Message; }
                }) { IsBackground = true, Name = "CalibrationPlayer" };
                player.SetApartmentState(ApartmentState.MTA);
                player.Start();

                double total = Beeps[Beeps.Length - 1] + 0.1 + MaxMs / 1000.0 + 0.6;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < total + 0.3)
                {
                    if (cancel.IsCancellationRequested) return new CalibrationResult { Error = "已取消" };
                    if (playError != null) return new CalibrationResult { Error = "无法播放提示音", Detail = playError };
                    Thread.Sleep(20);
                    long target = (long)EngineClock.NowFrames - AudioEngine.LatencyFrames;
                    while (pos < target)
                    {
                        int n = (int)Math.Min(target - pos, 4800);
                        Drain(loop.Timeline, pos, n, tmp, a);
                        Drain(mic.Timeline, pos, n, tmp, b);
                        pos += n;
                    }
                }
                return Analyze(a.ToArray(), b.ToArray());
            }
            catch (Exception ex)
            {
                return new CalibrationResult { Error = "校准出错", Detail = ex.Message };
            }
            finally
            {
                playCancel.Cancel();
                player?.Join(2000);
                loop.Stop();
                mic.Stop();
            }
        }

        private static void Drain(Timeline t, long pos, int n, float[] tmp, List<float> dst)
        {
            Array.Clear(tmp, 0, n * 2);
            t.ReadAdd(pos, tmp, n, 1f);
            for (int i = 0; i < n; i++) dst.Add((tmp[i * 2] + tmp[i * 2 + 1]) * 0.5f);
        }

        /// <summary>从两路信号（同一时间轴，48kHz 单声道）算出麦克风相对回环的延迟。也供自检用合成信号验证。</summary>
        public static CalibrationResult Analyze(float[] loop, float[] mic)
        {
            var cl = Correlate(loop, Template);
            var cm = Correlate(mic, Template);
            int tol = Sr / 100;   // 10ms

            // 1. 在回环里按「间隔模式」找出这串提示音（不怕别的系统声音干扰）
            double max = cl.Max();
            if (max <= 0) return Fail("没有播放出提示音", "回环无信号");
            var peaks = LocalPeaks(cl, max * 0.25, Sr * 3 / 10);
            long bestBase = 0; int bestScore = 0;
            foreach (int p in peaks)
                for (int j = 0; j < Beeps.Length; j++)
                {
                    long bs = p - (long)(Beeps[j] * Sr);
                    int score = Beeps.Count(t => peaks.Any(q => Math.Abs(q - (bs + (long)(t * Sr))) <= tol));
                    if (score > bestScore) { bestScore = score; bestBase = bs; }
                }
            if (bestScore < 4) return Fail("没有播放出提示音", $"回环只匹配到 {bestScore} 声");

            // 2. 回环里每一声的精确位置
            var lps = new List<int>();
            foreach (double t in Beeps)
            {
                long expect = bestBase + (long)(t * Sr);
                int lp = ArgMax(cl, (int)(expect - tol), (int)(expect + tol));
                if (lp >= 0 && cl[lp] >= max * 0.25) lps.Add(lp);
            }

            // 3. 整体匹配：找一个延迟 L，使各声在麦克风里 L 处的相关值之和最大。
            //    间隔互不相同，只有真实延迟能让所有声同时对上（单独找每一声会被相邻的下一声干扰）
            int minL = -Sr / 50, maxL = MaxMs * Sr / 1000;
            int bestL = 0; double bestSum = -1;
            for (int L = minL; L <= maxL; L++)
            {
                double sum = 0;
                foreach (int lp in lps) { int k = lp + L; if (k >= 0 && k < cm.Length) sum += cm[k]; }
                if (sum > bestSum) { bestSum = sum; bestL = L; }
            }

            // 4. 每一声在 bestL 附近 ±2ms 内精修，并检查信噪比
            var lags = new List<double>();
            var info = new List<string>();
            int fine = Sr / 500;
            foreach (int lp in lps)
            {
                int mp = ArgMax(cm, lp + bestL - fine, lp + bestL + fine + 1);
                if (mp < 0) continue;
                double snr = cm[mp] / (Median(cm, lp + minL, lp + maxL) + 1e-12);
                info.Add($"{(mp - lp) * 1000.0 / Sr:F1}ms/{snr:F0}");
                if (snr >= 30) lags.Add((mp - lp) * 1000.0 / Sr);
            }
            string detail = "各声(延迟/信噪比): " + string.Join(" ", info);
            if (lags.Count < 3) return Fail("没听到提示音，把耳罩贴近麦克风再试", detail);

            // 5. 取中位数，要求多数结果一致（±2ms）
            lags.Sort();
            double med = lags[lags.Count / 2];
            var inl = lags.Where(x => Math.Abs(x - med) <= 2).ToList();
            if (inl.Count < Math.Max(3, (lags.Count + 1) / 2)) return Fail("结果不稳定，请保持安静再试", detail);
            int ms = (int)Math.Round(Math.Max(0, Math.Min(MaxMs, inl.Average())));
            return new CalibrationResult { Ok = true, Ms = ms, Detail = detail };
        }

        private static CalibrationResult Fail(string error, string detail) => new CalibrationResult { Error = error, Detail = detail };

        // ------------------------------------------------------------------

        /// <summary>一声提示音：50ms 线性扫频 700→3500Hz，首尾 3ms 渐变。</summary>
        private static float[] MakeChirp(float amp)
        {
            var x = new float[BeepLen];
            int fade = Sr * 3 / 1000;
            double T = (double)BeepLen / Sr;
            for (int i = 0; i < BeepLen; i++)
            {
                double t = (double)i / Sr;
                double phase = 2 * Math.PI * (700 * t + (3500 - 700) * t * t / (2 * T));
                double env = Math.Min(1, Math.Min(i, BeepLen - 1 - i) / (double)fade);
                env = 0.5 - 0.5 * Math.Cos(Math.PI * env);
                x[i] = (float)(Math.Sin(phase) * amp * env);
            }
            return x;
        }

        /// <summary>整串提示音（48k 立体声，默认 -10dBFS）。</summary>
        public static float[] MakeSequence(float amp = 0.316f)
        {
            int frames = (int)((Beeps[Beeps.Length - 1] + 0.3) * Sr);
            var s = new float[frames * 2];
            var chirp = MakeChirp(amp);
            foreach (double t in Beeps)
            {
                int s0 = (int)(t * Sr);
                for (int i = 0; i < BeepLen; i++) s[(s0 + i) * 2] = s[(s0 + i) * 2 + 1] = chirp[i];
            }
            return s;
        }

        /// <summary>自检用：生成「回环 + 延迟 delayMs 后的麦克风」合成信号（含噪声和干扰声）。</summary>
        public static (float[] loop, float[] mic) Synthesize(double delayMs, int seed)
        {
            var rnd = new Random(seed);
            int n = Sr * 7;
            var loop = new float[n];
            var mic = new float[n];
            var seq = MakeSequence();
            int off = Sr / 2;
            int d = (int)Math.Round(delayMs * Sr / 1000);
            for (int i = 0; i < seq.Length / 2; i++)
            {
                if (off + i < n) loop[off + i] += seq[i * 2];
                if (off + d + i < n) mic[off + d + i] += seq[i * 2] * 0.05f;           // 麦克风收到的小得多
            }
            for (int i = 0; i < n; i++)
            {
                loop[i] += (float)(Math.Sin(2 * Math.PI * 220 * i / Sr) * 0.05);         // 同时在播的别的声音
                mic[i] += (float)((rnd.NextDouble() - 0.5) * 0.004 + Math.Sin(2 * Math.PI * 150 * i / Sr) * 0.01); // 底噪 + 说话般的低频
            }
            return (loop, mic);
        }

        // ------------------------------------------------------------------

        private static List<int> LocalPeaks(double[] c, double thr, int minDist)
        {
            var idx = new List<int>();
            for (int i = 1; i < c.Length - 1; i++)
                if (c[i] >= thr && c[i] >= c[i - 1] && c[i] >= c[i + 1]) idx.Add(i);
            idx.Sort((x, y) => c[y].CompareTo(c[x]));
            var picked = new List<int>();
            foreach (int i in idx)
                if (picked.All(p => Math.Abs(p - i) >= minDist)) picked.Add(i);
            return picked;
        }

        private static int ArgMax(double[] c, int lo, int hi)
        {
            lo = Math.Max(0, lo); hi = Math.Min(c.Length, hi);
            int best = -1; double v = double.MinValue;
            for (int i = lo; i < hi; i++) if (c[i] > v) { v = c[i]; best = i; }
            return best;
        }

        private static double Median(double[] c, int lo, int hi)
        {
            lo = Math.Max(0, lo); hi = Math.Min(c.Length, hi);
            if (hi <= lo) return 0;
            var a = new double[hi - lo];
            Array.Copy(c, lo, a, 0, a.Length);
            Array.Sort(a);
            return a[a.Length / 2];
        }

        /// <summary>|x ⋆ tpl|：结果第 i 个值对应模板从 x[i] 开始对齐时的相关幅度。用 FFT 计算。</summary>
        private static double[] Correlate(float[] x, float[] tpl)
        {
            int n = 1;
            while (n < x.Length + tpl.Length) n <<= 1;
            var xr = new double[n]; var xi = new double[n];
            var tr = new double[n]; var ti = new double[n];
            for (int i = 0; i < x.Length; i++) xr[i] = x[i];
            for (int i = 0; i < tpl.Length; i++) tr[i] = tpl[i];
            Fft(xr, xi, false);
            Fft(tr, ti, false);
            for (int i = 0; i < n; i++)
            {
                // X · conj(T)
                double r = xr[i] * tr[i] + xi[i] * ti[i];
                double im = xi[i] * tr[i] - xr[i] * ti[i];
                xr[i] = r; xi[i] = im;
            }
            Fft(xr, xi, true);
            var c = new double[x.Length];
            for (int i = 0; i < x.Length; i++) c[i] = Math.Abs(xr[i]) / n;
            return c;
        }

        /// <summary>原地基 2 FFT。</summary>
        private static void Fft(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double br = re[b] * cr - im[b] * ci;
                        double bi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - br; im[b] = im[a] - bi;
                        re[a] += br; im[a] += bi;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
