using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ShengJi.Audio;
using ShengJi.Core;
using ShengJi.Interop;

namespace ShengJi.Recording
{
    internal sealed class RecordingOptions
    {
        public string Format = "mp3";
        public int Bitrate = 128;
        public bool SplitTracks;
        public int SegmentMinutes;
        public string OutputDir;
        public string Template;
        public string Note;
        public DateTime StartTime = DateTime.Now;
        /// <summary>仅测试用：按帧数分段（&gt;0 时覆盖 SegmentMinutes）。</summary>
        public long TestSegmentFrames;
    }

    internal sealed class RecordingResult
    {
        public List<string> Files = new List<string>();
        public TimeSpan Duration;
        public Exception Error;
    }

    /// <summary>
    /// 一次录音。挂到 AudioEngine 上接收混音数据；独立的写入线程负责编码和写文件。
    /// 开始/暂停/停止都按时间轴位置精确生效（混音有 250ms 延迟，停止时会把最后这段也录完）。
    /// </summary>
    internal sealed class Recorder : IAudioSink
    {
        private const int Rate = EngineClock.SampleRate;

        private sealed class Block
        {
            public short[] Mix, Sys, Mic;
            public int Frames;
        }

        private readonly RecordingOptions _opt;
        private readonly long _startPos;
        private long _stopPos = long.MaxValue;

        private readonly object _gateLock = new object();
        private readonly List<(long pos, bool paused)> _gateEvents = new List<(long, bool)>();
        private bool _gateOpen = true;

        private volatile bool _finished;          // 混音侧：不再接收数据
        private volatile bool _finishRequested;   // 写入侧：写完队列后收尾
        private long _writtenFrames;

        private readonly ConcurrentQueue<Block> _queue = new ConcurrentQueue<Block>();
        private readonly ConcurrentBag<short[]> _pool = new ConcurrentBag<short[]>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private readonly ManualResetEvent _done = new ManualResetEvent(false);
        private Thread _writer;
        private Exception _openError;

        // 写入线程状态
        private string _format;
        private readonly List<IAudioFileWriter> _writers = new List<IAudioFileWriter>();
        private readonly RecordingResult _result = new RecordingResult();
        private int _segIndex;
        private long _segFrames;
        private long _segLimit;
        private bool _diskWarned;
        private volatile bool _discardFiles;      // 开始阶段就失败/超时：收尾时删掉空文件
        private long _estimatedBytes;

        public bool IsPaused { get; private set; }
        /// <summary>停止时的最终备注（可能和开始时不同，用于保存后重命名）。</summary>
        public string FinalNote { get; set; }
        public string ActualFormat => _format;
        public TimeSpan Duration => TimeSpan.FromSeconds(Interlocked.Read(ref _writtenFrames) / (double)Rate);
        public long EstimatedBytes => Interlocked.Read(ref _estimatedBytes);
        public RecordingOptions Options => _opt;

        /// <summary>录音结束（文件全部关闭）后在写入线程上触发。</summary>
        public event Action<Recorder, RecordingResult> Completed;
        /// <summary>需要提醒用户的情况（磁盘快满、改用 WAV 等），在写入线程上触发。</summary>
        public event Action<string> Warning;

        private Recorder(RecordingOptions opt, long startPos)
        {
            _opt = opt;
            _startPos = startPos;
            _format = opt.Format == "wav" ? "wav" : "mp3";
        }

        /// <summary>
        /// 开始录音。先挂到引擎上（数据先进入队列，开头不会因为创建文件慢而丢失），
        /// 再等写入线程把文件建好；文件无法创建时撤下并抛出异常。
        /// 回调在启动写入线程之前绑定，保证不会错过。
        /// </summary>
        public static Recorder Start(AudioEngine engine, RecordingOptions opt,
                                     Action<string> onWarning, Action<Recorder, RecordingResult> onCompleted = null)
        {
            var r = new Recorder(opt, AudioEngine.NowPosition);
            if (onWarning != null) r.Warning += onWarning;
            if (onCompleted != null) r.Completed += onCompleted;
            engine.AttachSink(r);
            r._writer = new Thread(r.WriterLoop) { IsBackground = true, Name = "RecorderWriter" };
            r._writer.SetApartmentState(ApartmentState.MTA);
            r._writer.Start();

            bool ready = r._ready.WaitOne(15000);
            if (!ready || r._openError != null)
            {
                engine.DetachSink(r);
                r._discardFiles = true;
                r.Finish();
                if (!ready) throw new TimeoutException("保存位置无响应");
                throw r._openError;
            }
            Log.Info($"开始录音: {string.Join(", ", r._result.Files)}");
            return r;
        }

        public void Pause()
        {
            lock (_gateLock) { if (IsPaused) return; IsPaused = true; _gateEvents.Add((AudioEngine.NowPosition, true)); }
        }

        public void Resume()
        {
            lock (_gateLock) { if (!IsPaused) return; IsPaused = false; _gateEvents.Add((AudioEngine.NowPosition, false)); }
        }

        /// <summary>请求停止：录到“现在”为止，随后自动收尾。</summary>
        public void Stop()
        {
            Interlocked.Exchange(ref _stopPos, AudioEngine.NowPosition);
        }

        public bool WaitCompleted(int ms) => _done.WaitOne(ms);

        // ==================================================================
        //  IAudioSink（混音线程）
        // ==================================================================

        public bool IsFinished => _finished;

        public void Write(long pos, float[] mix, float[] sys, float[] mic, int frames)
        {
            if (_finished) return;
            long stop = Interlocked.Read(ref _stopPos);
            int i = 0;
            while (i < frames)
            {
                long p = pos + i;
                if (p >= stop) break;

                long nextEvent;
                bool open;
                lock (_gateLock)
                {
                    while (_gateEvents.Count > 0 && _gateEvents[0].pos <= p)
                    {
                        _gateOpen = !_gateEvents[0].paused;
                        _gateEvents.RemoveAt(0);
                    }
                    nextEvent = _gateEvents.Count > 0 ? _gateEvents[0].pos : long.MaxValue;
                    open = _gateOpen;
                }

                long end = Math.Min(Math.Min(pos + frames, nextEvent), stop);
                if (p < _startPos) { end = Math.Min(end, _startPos); open = false; }
                int len = (int)(end - p);
                if (len <= 0) len = 1; // 防御：保证前进
                if (open) Enqueue(mix, sys, mic, i, len);
                i += len;
            }

            if (pos + frames >= stop) Finish();
        }

        public void EngineStopped() => Finish();

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            _finishRequested = true;
            _signal.Set();
        }

        private void Enqueue(float[] mix, float[] sys, float[] mic, int offset, int frames)
        {
            var b = new Block { Frames = frames, Mix = ToPcm(mix, offset, frames) };
            if (_opt.SplitTracks)
            {
                b.Sys = ToPcm(sys, offset, frames);
                b.Mic = ToPcm(mic, offset, frames);
            }
            _queue.Enqueue(b);
            Interlocked.Add(ref _writtenFrames, frames);
            _signal.Set();
        }

        private short[] ToPcm(float[] src, int offsetFrames, int frames)
        {
            int n = frames * 2;
            if (!_pool.TryTake(out var dst) || dst.Length < n) dst = new short[Math.Max(n, 4800 * 2)];
            int o = offsetFrames * 2;
            for (int i = 0; i < n; i++)
            {
                float x = src[o + i];
                int v = (int)(x * 32767f + (x >= 0 ? 0.5f : -0.5f));
                dst[i] = (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v);
            }
            return dst;
        }

        // ==================================================================
        //  写入线程
        // ==================================================================

        private void WriterLoop()
        {
            try
            {
                Directory.CreateDirectory(_opt.OutputDir);
                _segLimit = _opt.SegmentMinutes > 0 ? (long)_opt.SegmentMinutes * 60 * Rate : long.MaxValue;
                if (_opt.TestSegmentFrames > 0) _segLimit = _opt.TestSegmentFrames;
                if (_format == "mp3" && !Mp3FileWriter.IsAvailable())
                {
                    // 系统没有 MP3 编码器（如 N 版 Windows、Win7）：本次录音改用 WAV
                    _format = "wav";
                    Warning?.Invoke("MP3 不可用，已改用 WAV");
                }
                OpenSegment();
            }
            catch (Exception ex)
            {
                _openError = ex;
                CloseWriters();
                DeleteFiles();
                _ready.Set();
                _done.Set();
                return;
            }
            _ready.Set();

            var flushTimer = Stopwatch.StartNew();
            var diskTimer = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    _signal.WaitOne(250);
                    while (_queue.TryDequeue(out var b)) WriteBlock(b);

                    if (flushTimer.ElapsedMilliseconds >= 1000)
                    {
                        // 每秒把数据交给系统，程序崩溃也不丢；每 10 秒强制写入磁盘，防断电
                        bool toDisk = diskTimer.ElapsedMilliseconds >= 10000;
                        foreach (var w in _writers) w.Flush(toDisk);
                        flushTimer.Restart();
                        if (toDisk) { diskTimer.Restart(); CheckDiskSpace(); }
                    }

                    if (_finishRequested && _queue.IsEmpty) break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("录音写入失败", ex);
                _result.Error = ex;
                _finished = true;
            }
            finally
            {
                CloseWriters();
                if (_discardFiles) DeleteFiles();
                _result.Duration = Duration;
                Log.Info($"录音结束，时长 {_result.Duration}，文件: {string.Join(", ", _result.Files)}");
                _done.Set();
                if (!_discardFiles)
                {
                    try { Completed?.Invoke(this, _result); } catch (Exception ex) { Log.Error("Completed 回调出错", ex); }
                }
            }
        }

        private void DeleteFiles()
        {
            foreach (var f in _result.Files)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
            _result.Files.Clear();
        }

        private void WriteBlock(Block b)
        {
            int off = 0;
            while (off < b.Frames)
            {
                long room = _segLimit - _segFrames;
                if (room <= 0) { RotateSegment(); room = _segLimit - _segFrames; }
                int n = (int)Math.Min(room, b.Frames - off);
                if (off == 0 && n == b.Frames)
                {
                    WriteAll(b.Mix, b.Sys, b.Mic, n);
                }
                else
                {
                    WriteAll(Slice(b.Mix, off, n), Slice(b.Sys, off, n), Slice(b.Mic, off, n), n);
                }
                _segFrames += n;
                off += n;
            }
            Recycle(b.Mix); Recycle(b.Sys); Recycle(b.Mic);
        }

        private void WriteAll(short[] mix, short[] sys, short[] mic, int frames)
        {
            _writers[0].Write(mix, frames);
            if (_writers.Count >= 3)
            {
                _writers[1].Write(sys, frames);
                _writers[2].Write(mic, frames);
            }
            long bytes = _format == "wav" ? frames * 4L * _writers.Count : frames * (long)_opt.Bitrate * 1000 / 8 / Rate * _writers.Count;
            Interlocked.Add(ref _estimatedBytes, bytes);
        }

        private static short[] Slice(short[] src, int offFrames, int frames)
        {
            if (src == null) return null;
            var r = new short[frames * 2];
            Array.Copy(src, offFrames * 2, r, 0, frames * 2);
            return r;
        }

        private void Recycle(short[] a) { if (a != null) _pool.Add(a); }

        private void RotateSegment()
        {
            CloseWriters();
            OpenSegment();
        }

        private void OpenSegment()
        {
            _segIndex++;
            _segFrames = 0;

            // WAV 单文件不能超过 4GB：约 5 小时自动换一个文件
            if (_format == "wav")
            {
                long wavLimit = 5L * 3600 * Rate;
                if (_segLimit > wavLimit) _segLimit = wavLimit;
            }

            string stem = FileNaming.Build(_opt.Template, _opt.StartTime, _opt.Note);
            bool numbered = _opt.SegmentMinutes > 0 || _opt.TestSegmentFrames > 0 || _segIndex > 1;
            if (numbered) stem += "_" + _segIndex.ToString("00");

            // 主文件和分轨文件作为一组判断是否重名，保证三者名字一致
            string ext = "." + _format;
            string[] suffixes = _opt.SplitTracks ? new[] { "", "_电脑声音", "_麦克风" } : new[] { "" };
            stem = FileNaming.UniqueGroup(_opt.OutputDir, stem, suffixes, ext);

            foreach (var suffix in suffixes)
            {
                string path = Path.Combine(_opt.OutputDir, stem + suffix + ext);
                var w = CreateWriter(path);
                _writers.Add(w);
                _result.Files.Add(path);
            }
        }

        private IAudioFileWriter CreateWriter(string path)
        {
            return _format == "mp3" ? (IAudioFileWriter)new Mp3FileWriter(path, _opt.Bitrate) : new WavFileWriter(path);
        }

        private void CloseWriters()
        {
            foreach (var w in _writers)
            {
                try { w.Close(); } catch (Exception ex) { Log.Warn("关闭文件出错: " + ex.Message); }
            }
            _writers.Clear();
        }

        private void CheckDiskSpace()
        {
            long? free = Native.GetFreeSpace(_opt.OutputDir);
            if (free.HasValue && free.Value < 300L * 1024 * 1024 && !_diskWarned)
            {
                _diskWarned = true;
                Warning?.Invoke($"磁盘空间不足（剩余 {free.Value / 1024 / 1024} MB）");
            }
        }
    }
}
