using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ShengJi.Audio;
using ShengJi.Core;
using ShengJi.Interop;
using ShengJi.Recording;

namespace ShengJi
{
    /// <summary>
    /// 自检：枚举设备 → 测试 MP3 编码器 → 实际采集电脑声音/麦克风 → 录一小段（混音 + 分轨）→ 校验文件。
    /// 加 --tone 时会用一个子进程播放 2.5 秒的小音量提示音，用来验证“按设备”和“按应用”两种电脑声音采集都能收到信号。
    /// 结果写入 &lt;dir&gt;\selftest.txt，返回值 0 = 全部通过。
    /// </summary>
    internal static class SelfTest
    {
        private static StringBuilder _log;
        private static int _failures;

        public static int Run(string dir, bool tone)
        {
            Directory.CreateDirectory(dir);
            _log = new StringBuilder();
            _failures = 0;
            try { Body(dir, tone); }
            catch (Exception ex) { Fail("自检异常: " + ex); }
            Line(_failures == 0 ? "== 全部通过 ==" : $"== {_failures} 项失败 ==");
            File.WriteAllText(Path.Combine(dir, "selftest.txt"), _log.ToString(), new UTF8Encoding(true));
            Log.Flush();
            return _failures == 0 ? 0 : 1;
        }

        private static void Body(string dir, bool tone)
        {
            Line($"系统: {Native.OSVersion}  64位进程: {Environment.Is64BitProcess}  按应用录制: {Native.SupportsProcessLoopback}");

            // 1. 设备与应用
            var cat = Task.Run(() => DeviceCatalog.Build()).Result;
            Line($"播放设备 {cat.Outputs.Count} 个:");
            foreach (var d in cat.Outputs) Line($"   {(d.IsDefault ? "*" : " ")} {d.Name}");
            Line($"录音设备 {cat.Inputs.Count} 个:");
            foreach (var d in cat.Inputs) Line($"   {(d.IsDefault ? "*" : " ")} {d.Name}");
            Line($"有音频会话的应用 {cat.Apps.Count} 个: {string.Join(", ", cat.Apps.Select(a => a.DisplayName + "(" + a.ExeName + (a.IsPlaying ? ",播放中" : "") + ")"))}");
            Check(cat.Outputs.Count > 0, "至少有一个播放设备");

            // 2. MP3 编码器
            bool mp3 = Task.Run(() => Mp3FileWriter.IsAvailable()).Result;
            Check(mp3, "系统 MP3 编码器可用");
            if (mp3) TestMp3Encode(dir);

            // 2b. 电平表（响度计）与人声对齐校准算法：纯计算，不需要声卡
            TestLoudnessMeter();
            TestCalibrationAnalysis();

            // 3. 引擎：默认设备
            var engine = new AudioEngine();
            engine.Notice += (m, k) => Line($"   [提示/{k}] {m}");
            engine.SetSources(new SourceSpec { Mode = SourceMode.Default }, new SourceSpec { Mode = SourceMode.Default });
            engine.Start();
            Thread.Sleep(1500);
            Line($"电脑声音状态: {engine.SysStatus.State} {engine.SysStatus.Detail} (采集源 {engine.SysStatus.ActiveSources})");
            Line($"麦克风状态:   {engine.MicStatus.State} {engine.MicStatus.Detail} (采集源 {engine.MicStatus.ActiveSources})");
            Check(engine.SysStatus.ActiveSources > 0, "电脑声音（默认设备回环）已开始采集");
            if (cat.Inputs.Count > 0) Check(engine.MicStatus.ActiveSources > 0, "麦克风已开始采集");

            Process player = null;
            if (tone) player = PlayTone(dir);

            // 4. 录 3 秒（WAV + 分轨，便于分析）
            var opt = new RecordingOptions
            {
                Format = "wav", SplitTracks = true, OutputDir = Path.Combine(dir, "rec_wav"),
                Template = "{日期}_{时间}_{备注}", Note = "自检", StartTime = DateTime.Now,
            };
            float maxSys = 0, maxMic = 0;
            var rec = Recorder.Start(engine, opt, w => Line("   [警告] " + w));
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                Thread.Sleep(100);
                engine.ReadLevels(out float s, out float m);
                maxSys = Math.Max(maxSys, s); maxMic = Math.Max(maxMic, m);
            }
            rec.Stop();
            Check(rec.WaitCompleted(5000), "WAV 录音正常结束");
            Line($"录音时长 {rec.Duration.TotalSeconds:F2}s，最大响度 电脑 {Db(maxSys)}  麦克风 {Db(maxMic)}");
            Check(Math.Abs(rec.Duration.TotalSeconds - 3.0) < 0.3, "录音时长与实际时间一致（±0.3s）");
            foreach (var f in Directory.GetFiles(opt.OutputDir))
            {
                var info = AnalyzeWav(f);
                Line($"   {Path.GetFileName(f)}  {new FileInfo(f).Length / 1024}KB  {info}");
            }
            Check(Directory.GetFiles(opt.OutputDir).Length == 3, "生成 3 个文件（混音 + 电脑声音 + 麦克风）");
            if (tone) Check(maxSys > 0.005f, "播放提示音时，电脑声音（设备回环）收到了信号");

            // 5. 按应用录制
            if (Native.SupportsProcessLoopback && tone)
            {
                if (player == null || player.HasExited) player = PlayTone(dir);
                string exe = Path.GetFileName(player.MainModule.FileName).ToLowerInvariant();
                engine.SetSources(new SourceSpec { Mode = SourceMode.App, Id = exe, Label = "提示音进程" }, new SourceSpec { Mode = SourceMode.Default });
                Thread.Sleep(1200);
                engine.ReadLevels(out _, out _);
                float appMax = 0;
                for (int i = 0; i < 10; i++) { Thread.Sleep(100); engine.ReadLevels(out float s, out _); appMax = Math.Max(appMax, s); }
                Line($"按应用录制({exe}): 状态 {engine.SysStatus.State} 采集源 {engine.SysStatus.ActiveSources} 响度 {Db(appMax)}");
                Check(engine.SysStatus.ActiveSources > 0, "按应用录制已开始采集");
                Check(appMax > 0.005f, "按应用录制收到了该应用的声音");
            }
            try { player?.Kill(); } catch { }

            // 6. MP3 录音 2 秒
            if (mp3)
            {
                engine.SetSources(new SourceSpec { Mode = SourceMode.Default }, new SourceSpec { Mode = SourceMode.Default });
                Thread.Sleep(800);
                var opt2 = new RecordingOptions
                {
                    Format = "mp3", Bitrate = 128, OutputDir = Path.Combine(dir, "rec_mp3"),
                    Template = "{日期}_{时间}", StartTime = DateTime.Now,
                };
                var rec2 = Recorder.Start(engine, opt2, w => Line("   [警告] " + w));
                Thread.Sleep(1000);
                rec2.Pause();
                Thread.Sleep(700);
                rec2.Resume();
                Thread.Sleep(1000);
                rec2.Stop();
                Check(rec2.WaitCompleted(5000), "MP3 录音正常结束");
                var f = Directory.GetFiles(opt2.OutputDir).FirstOrDefault();
                Check(f != null, "生成 MP3 文件");
                if (f != null)
                {
                    var (frames, secs) = Mp3Info(f);
                    Line($"   {Path.GetFileName(f)}  {new FileInfo(f).Length / 1024}KB  MP3帧 {frames}  时长约 {secs:F2}s（含 0.7s 暂停，应约 2.0s）");
                    Check(Math.Abs(secs - 2.0) < 0.35, "MP3 时长正确（暂停期间不录）");
                }
            }

            // 7. 指定设备不存在 → 自动改录默认设备
            engine.SetSources(new SourceSpec { Mode = SourceMode.Device, Id = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}", Label = "不存在的耳机" },
                              new SourceSpec { Mode = SourceMode.Device, Id = "{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}", Label = "不存在的麦克风" });
            Thread.Sleep(1500);
            Line($"指定设备不存在: 电脑 {engine.SysStatus.State}/{engine.SysStatus.Detail} 源{engine.SysStatus.ActiveSources}；麦 {engine.MicStatus.State}/{engine.MicStatus.Detail} 源{engine.MicStatus.ActiveSources}");
            Check(engine.SysStatus.State == GroupState.Fallback && engine.SysStatus.ActiveSources == 1, "电脑声音：设备不在时退回默认设备");
            if (cat.Inputs.Count > 0)
                Check(engine.MicStatus.State == GroupState.Fallback && engine.MicStatus.ActiveSources == 1, "麦克风：设备不在时退回默认麦克风");

            // 8. 所有播放设备
            engine.SetSources(new SourceSpec { Mode = SourceMode.AllOutputs }, new SourceSpec { Mode = SourceMode.Default });
            Thread.Sleep(2000);
            Line($"所有播放设备: {engine.SysStatus.Detail}，采集源 {engine.SysStatus.ActiveSources}");
            Check(engine.SysStatus.ActiveSources == cat.Outputs.Count, "所有播放设备都在采集");

            // 9. 按应用：未运行时等待 → 启动后自动接上 → 退出后回到等待
            if (Native.SupportsProcessLoopback && tone)
            {
                string toneExe = Path.Combine(dir, "sjtone.exe");
                File.Copy(Process.GetCurrentProcess().MainModule.FileName, toneExe, true);
                engine.SetSources(new SourceSpec { Mode = SourceMode.App, Id = "sjtone.exe", Label = "测试播放器" }, new SourceSpec { Mode = SourceMode.Default });
                Thread.Sleep(1500);
                Check(engine.SysStatus.State == GroupState.Waiting, "应用未运行时显示“等待启动”");

                var p = Process.Start(new ProcessStartInfo(toneExe, $"--play-tone \"{Path.Combine(dir, "tone.wav")}\"") { UseShellExecute = false, CreateNoWindow = true });
                float got = 0;
                var sw2 = Stopwatch.StartNew();
                while (sw2.ElapsedMilliseconds < 2400)
                {
                    Thread.Sleep(100);
                    engine.ReadLevels(out float sv, out _);
                    if (sw2.ElapsedMilliseconds > 1200) got = Math.Max(got, sv);
                }
                Line($"应用启动后: 状态 {engine.SysStatus.State} 采集源 {engine.SysStatus.ActiveSources} 响度 {Db(got)}");
                Check(engine.SysStatus.State == GroupState.Ok && got > 0.005f, "应用启动后自动接上并录到声音");
                p.WaitForExit(5000);
                Thread.Sleep(1800);
                Check(engine.SysStatus.State == GroupState.Waiting, "应用退出后回到“等待启动”");
            }

            // 9b. 人声对齐确实把电脑声音后移了设定值；电平表没有混音延迟
            if (tone) TestAlignmentAndMeterLatency(engine, cat);

            // 10. 自动分段（测试用 1 秒一段）
            engine.SetSources(new SourceSpec { Mode = SourceMode.Default }, new SourceSpec { Mode = SourceMode.Default });
            Thread.Sleep(800);
            var opt3 = new RecordingOptions
            {
                Format = "wav", OutputDir = Path.Combine(dir, "rec_seg"), Template = "{日期}_{时间}_{备注}",
                Note = "分段", StartTime = DateTime.Now, TestSegmentFrames = 48000,
            };
            var rec3 = Recorder.Start(engine, opt3, null);
            Thread.Sleep(2500);
            rec3.Stop();
            Check(rec3.WaitCompleted(5000), "分段录音正常结束");
            var segFiles = Directory.GetFiles(opt3.OutputDir).OrderBy(f => f).ToArray();
            foreach (var f in segFiles) Line($"   {Path.GetFileName(f)}  {AnalyzeWav(f)}");
            Check(segFiles.Length == 3 && segFiles[0].EndsWith("_01.wav") && segFiles[2].EndsWith("_03.wav"), "按时长切成 3 个文件（_01/_02/_03）");

            // 11. 停止后立刻开始下一段：上一段不应被截断
            var optA = new RecordingOptions { Format = "wav", OutputDir = Path.Combine(dir, "rec_ab"), Template = "{日期}_{时间}_{备注}", Note = "A", StartTime = DateTime.Now };
            var recA = Recorder.Start(engine, optA, null);
            Thread.Sleep(2000);
            recA.Stop();
            var optB = new RecordingOptions { Format = "wav", OutputDir = Path.Combine(dir, "rec_ab"), Template = "{日期}_{时间}_{备注}", Note = "B", StartTime = DateTime.Now };
            var recB = Recorder.Start(engine, optB, null);
            Thread.Sleep(1500);
            recB.Stop();
            Check(recA.WaitCompleted(5000) && recB.WaitCompleted(5000), "连续两段录音都正常结束");
            Line($"连续两段: A={recA.Duration.TotalSeconds:F2}s B={recB.Duration.TotalSeconds:F2}s");
            Check(Math.Abs(recA.Duration.TotalSeconds - 2.0) < 0.15 && Math.Abs(recB.Duration.TotalSeconds - 1.5) < 0.15,
                  "停止后马上开始，上一段没有被截断（A≈2.0s B≈1.5s）");

            // 12. 实验：应用不出声时，按应用录制是否仍持续给数据（决定能否对它做“没数据=卡死”检测）
            if (Native.SupportsProcessLoopback)
            {
                var ping = Process.Start(new ProcessStartInfo("ping.exe", "-n 8 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true });
                engine.SetSources(new SourceSpec { Mode = SourceMode.App, Id = "ping.exe", Label = "ping" }, new SourceSpec { Mode = SourceMode.Default });
                Thread.Sleep(1500);
                var before = engine.GetSourceStats();
                Thread.Sleep(3000);
                var after = engine.GetSourceStats();
                foreach (var a in after)
                {
                    var b = before.FirstOrDefault(x => x.Key == a.Key);
                    long delta = a.Packets - (b?.Packets ?? 0);
                    Line($"   诊断 {a.Kind,-10} 开始={a.Started} 3秒内数据包 {delta,4}  距上次数据 {a.SecondsSinceData:F1}s  {a.Key}");
                }
                try { ping.Kill(); } catch { }
            }

            engine.Stop();
        }

        // ------------------------------------------------------------------

        /// <summary>响度计：K 加权读数与 BS.1770 一致（-20dBFS 正弦，双声道），IEC 刻度换算正确。</summary>
        private static void TestLoudnessMeter()
        {
            double Measure(double freq, bool mono)
            {
                var m = new LoudnessMeter(mono);
                var buf = new float[4800 * 2];
                double peakRead = 0;
                for (int blk = 0; blk < 20; blk++)   // 2 秒
                {
                    for (int i = 0; i < 4800; i++)
                    {
                        float v = (float)(0.1 * Math.Sin(2 * Math.PI * freq * (blk * 4800 + i) / 48000.0));
                        buf[i * 2] = v; buf[i * 2 + 1] = v;
                    }
                    m.Process(buf, 4800);
                    double r = m.Read();
                    if (blk >= 10) peakRead = Math.Max(peakRead, r);   // 稳定后
                }
                return 10 * Math.Log10(peakRead * 0.85290578);
            }
            double k1k = Measure(1000, false), k4k = Measure(4000, false), k100 = Measure(100, false), mono1k = Measure(1000, true);
            Line($"响度计(-20dBFS 正弦): 1kHz {k1k:F2}  4kHz {k4k:F2}  100Hz {k100:F2}  单声道1kHz {mono1k:F2} LUFS（理论 -19.99 / -16.72 / -21.82 / -19.99）");
            Check(Math.Abs(k1k + 19.99) < 0.15 && Math.Abs(k4k + 16.72) < 0.15 && Math.Abs(k100 + 21.82) < 0.25 && Math.Abs(mono1k + 19.99) < 0.15,
                  "响度计读数符合 BS.1770（误差 < 0.25dB，低频含 25ms 积分的纹波）");

            var silent = new LoudnessMeter(false);
            silent.Process(new float[9600], 4800);
            Check(silent.Read() == 0, "静音时读数为 0");

            double p20 = MeterState.Position((float)Math.Pow(10, -20 / 20.0)), p40 = MeterState.Position((float)Math.Pow(10, -40 / 20.0));
            Check(Math.Abs(p20 - 0.5) < 1e-6 && Math.Abs(p40 - 0.15) < 1e-6 && MeterState.Position(1f) == 1 && MeterState.Position(0f) == 0,
                  "电平表刻度符合 IEC 60268-18（-20dB 在 50%，-40dB 在 15%）");
        }

        /// <summary>校准算法：合成信号里已知延迟能被准确测出；没有提示音时不会误报。</summary>
        private static void TestCalibrationAnalysis()
        {
            bool allOk = true;
            var parts = new System.Collections.Generic.List<string>();
            foreach (double d in new[] { 0.0, 37.4, 118.5, 260.0, 845.2 })
            {
                var (loop, mic) = AlignmentCalibrator.Synthesize(d, (int)d + 1);
                var r = AlignmentCalibrator.Analyze(loop, mic);
                parts.Add(r.Ok ? $"{d}→{r.Ms}" : $"{d}→失败({r.Error} {r.Detail})");
                if (!r.Ok || Math.Abs(r.Ms - d) > 1.0) allOk = false;
            }
            Line("校准算法(设定→测得 ms): " + string.Join("  ", parts));
            Check(allOk, "校准算法测得的延迟与设定值一致（±1ms）");

            var (l2, m2) = AlignmentCalibrator.Synthesize(100, 7);
            var noMic = AlignmentCalibrator.Analyze(l2, new float[m2.Length]);
            var noLoop = AlignmentCalibrator.Analyze(new float[l2.Length], m2);
            Line($"   麦克风没收到: {noMic.Error}；没播放出来: {noLoop.Error}");
            Check(!noMic.Ok && !noLoop.Ok, "收不到提示音时校准报失败，不会给出错误数值");
        }

        /// <summary>
        /// 实际播放提示音：① 引擎录到的电脑声音比同一时刻的原始回环正好晚设定的对齐值；
        /// ② 电平表在声音进入时间轴后很快就有反应（不再有 250ms 混音延迟）。
        /// </summary>
        private static void TestAlignmentAndMeterLatency(AudioEngine engine, SourceCatalog cat)
        {
            string renderId = cat.Outputs.FirstOrDefault(o => o.IsDefault)?.Id;
            if (renderId == null) { Line("   没有默认播放设备，跳过人声对齐测试"); return; }
            engine.SetSources(new SourceSpec { Mode = SourceMode.Default }, new SourceSpec { Mode = SourceMode.Default });
            engine.SetAlignment(new System.Collections.Generic.Dictionary<string, int>(), 200);
            Thread.Sleep(1200);
            int expectMs = engine.AlignMicId != null ? 200 : 0;   // 没有麦克风时不做对齐

            // 引擎录到的电脑声音（经过对齐）
            var probe = new ProbeSink();
            // 同一设备、不加对齐的独立回环，作为参照
            long start = (long)EngineClock.NowFrames;
            var refSrc = new CaptureSource(SourceKind.Loopback, renderId, 0, start);
            refSrc.Start();
            var wait = Stopwatch.StartNew();
            while (!refSrc.Started && wait.ElapsedMilliseconds < 4000) Thread.Sleep(20);
            long s0 = (long)EngineClock.NowFrames;
            refSrc.Timeline.SkipTo(s0);
            probe.Begin(s0, 48000 * 8);
            engine.AttachSink(probe);
            engine.ReadLevels(out _, out _);

            var seq = AlignmentCalibrator.MakeSequence(0.05f);   // 约 -26dBFS
            var player = new Thread(() => { try { RenderPlayer.Play(renderId, seq, CancellationToken.None); } catch (Exception ex) { Line("   播放出错: " + ex.Message); } })
                { IsBackground = true };
            player.SetApartmentState(ApartmentState.MTA);
            player.Start();

            var refBuf = new float[48000 * 8];
            var tmp = new float[4800 * 2];
            long pos = s0;
            double meterHit = -1;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 6500)
            {
                Thread.Sleep(2);
                engine.ReadLevels(out float lv, out _);
                if (meterHit < 0 && lv > 0.003f) meterHit = EngineClock.NowFrames;
                long target = (long)EngineClock.NowFrames - AudioEngine.LatencyFrames;
                while (pos < target && pos - s0 + 4800 <= refBuf.Length)
                {
                    int n = (int)Math.Min(target - pos, 4800);
                    Array.Clear(tmp, 0, n * 2);
                    refSrc.Timeline.ReadAdd(pos, tmp, n, 1f);
                    for (int i = 0; i < n; i++) refBuf[pos - s0 + i] = (tmp[i * 2] + tmp[i * 2 + 1]) * 0.5f;
                    pos += n;
                }
            }
            player.Join(2000);
            engine.DetachSink(probe);
            refSrc.Stop();
            engine.SetAlignment(new System.Collections.Generic.Dictionary<string, int>(), 0);

            var r = AlignmentCalibrator.Analyze(refBuf, probe.Data);
            Line($"人声对齐: 设定 {expectMs}ms，录到的电脑声音相对原始回环 {(r.Ok ? r.Ms + "ms" : "未测出 " + r.Error)}  {r.Detail}");
            Check(r.Ok && Math.Abs(r.Ms - expectMs) <= 2, "电脑声音按设定值后移（±2ms）");

            // 第一声在原始回环时间轴上的位置 → 电平表第一次超过阈值的时刻
            int onset = -1;
            for (int i = 0; i < refBuf.Length; i++) if (Math.Abs(refBuf[i]) > 0.003f) { onset = i; break; }
            if (onset >= 0 && meterHit > 0)
            {
                double lat = (meterHit - (s0 + onset)) / 48.0;
                Line($"电平表延迟: {lat:F0}ms（相对声音在时间轴上的位置；负数表示系统提前交出了将要播放的数据）");
                Check(lat < 60, "电平表没有混音延迟（< 60ms）");
            }
            else Fail("电平表延迟：没有检测到提示音");
        }

        /// <summary>收集引擎输出的电脑声音分轨（单声道），按时间轴位置对齐到 Begin 指定的起点。</summary>
        private sealed class ProbeSink : IAudioSink
        {
            public float[] Data;
            private long _start;
            public void Begin(long start, int frames) { _start = start; Data = new float[frames]; }
            public void Write(long pos, float[] mix, float[] sys, float[] mic, int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    long k = pos + i - _start;
                    if (k >= 0 && k < Data.Length) Data[k] = (sys[i * 2] + sys[i * 2 + 1]) * 0.5f;
                }
            }
            public bool IsFinished => false;
            public void EngineStopped() { }
        }

        private static void TestMp3Encode(string dir)
        {
            string path = Path.Combine(dir, "sine_440.mp3");
            if (File.Exists(path)) File.Delete(path);
            var t = Task.Run(() =>
            {
                using (var w = new Mp3FileWriter(path, 128))
                {
                    var buf = new short[4800 * 2];
                    int n = 0;
                    for (int block = 0; block < 30; block++) // 3 秒
                    {
                        for (int i = 0; i < 4800; i++, n++)
                        {
                            short v = (short)(Math.Sin(2 * Math.PI * 440 * n / 48000.0) * 8000);
                            buf[i * 2] = v; buf[i * 2 + 1] = v;
                        }
                        w.Write(buf, 4800);
                    }
                    w.Close();
                }
            });
            t.Wait();
            var (frames, secs) = Mp3Info(path);
            Line($"MP3 编码测试: {new FileInfo(path).Length / 1024}KB 帧数 {frames} 时长约 {secs:F2}s");
            Check(Math.Abs(secs - 3.0) < 0.15, "MP3 编码时长正确");
        }

        /// <summary>按 MPEG 帧头遍历 MP3 文件，返回帧数和时长。</summary>
        public static (int frames, double seconds) Mp3Info(string path)
        {
            var data = File.ReadAllBytes(path);
            int[] br1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
            int[] sr1 = { 44100, 48000, 32000, 0 };
            int pos = 0, frames = 0;
            double secs = 0;
            while (pos + 4 <= data.Length)
            {
                if (data[pos] == 0xFF && (data[pos + 1] & 0xFE) == 0xFA) // MPEG-1 Layer III
                {
                    int bi = data[pos + 2] >> 4, si = (data[pos + 2] >> 2) & 3, pad = (data[pos + 2] >> 1) & 1;
                    int br = br1[bi], sr = sr1[si];
                    if (br > 0 && sr > 0)
                    {
                        int len = 144 * br * 1000 / sr + pad;
                        frames++; secs += 1152.0 / sr; pos += len;
                        continue;
                    }
                }
                pos++;
            }
            return (frames, secs);
        }

        private static string AnalyzeWav(string path)
        {
            try
            {
                var b = File.ReadAllBytes(path);
                int dataLen = BitConverter.ToInt32(b, 40);
                int n = Math.Min(dataLen, b.Length - 44) / 2;
                double sum = 0; int peak = 0;
                for (int i = 0; i < n; i++)
                {
                    short v = BitConverter.ToInt16(b, 44 + i * 2);
                    sum += (double)v * v; peak = Math.Max(peak, Math.Abs((int)v));
                }
                double rms = Math.Sqrt(sum / Math.Max(1, n)) / 32768.0;
                return $"时长 {dataLen / 192000.0:F2}s  峰值 {Db(peak / 32768f)}  RMS {Db((float)rms)}";
            }
            catch (Exception ex) { return "分析失败 " + ex.Message; }
        }

        /// <summary>用独立进程播放一段小音量的 440Hz 提示音（约 -26dBFS，2.5 秒）。</summary>
        private static Process PlayTone(string dir)
        {
            string wav = Path.Combine(dir, "tone.wav");
            if (!File.Exists(wav))
            {
                using (var w = new BinaryWriter(File.Create(wav)))
                {
                    int frames = 48000 * 5 / 2;
                    w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + frames * 4);
                    w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)2);
                    w.Write(48000); w.Write(48000 * 4); w.Write((short)4); w.Write((short)16);
                    w.Write(Encoding.ASCII.GetBytes("data")); w.Write(frames * 4);
                    for (int i = 0; i < frames; i++)
                    {
                        double env = Math.Min(1, Math.Min(i, frames - i) / 2400.0);
                        short v = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000.0) * 1650 * env);
                        w.Write(v); w.Write(v);
                    }
                }
            }
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"(New-Object Media.SoundPlayer '{wav}').PlaySync()\"")
            { UseShellExecute = false, CreateNoWindow = true };
            var p = Process.Start(psi);
            Thread.Sleep(700);
            return p;
        }

        private static string Db(float lin) => lin <= 0.00001f ? "-∞dB" : $"{20 * Math.Log10(lin):F1}dB";
        private static void Line(string s) { _log.AppendLine(s); Log.Info("[自检] " + s); }
        private static void Check(bool ok, string what) { Line((ok ? "  [通过] " : "  [失败] ") + what); if (!ok) _failures++; }
        private static void Fail(string s) { Line("  [失败] " + s); _failures++; }
    }
}
