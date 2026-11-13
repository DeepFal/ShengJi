using System;
using System.IO;
using System.Runtime.InteropServices;
using ShengJi.Core;
using ShengJi.Interop;

namespace ShengJi.Recording
{
    /// <summary>16bit 立体声 48k PCM → 文件。所有方法都在录音写入线程上调用。</summary>
    internal interface IAudioFileWriter : IDisposable
    {
        string FilePath { get; }
        void Write(short[] pcm, int frames);
        /// <summary>toDisk=true 时强制落盘（断电保护）。</summary>
        void Flush(bool toDisk);
        /// <summary>正常收尾。</summary>
        void Close();
    }

    // ======================================================================
    //  MP3（系统自带 Media Foundation MP3 编码器，Windows 8+）
    // ======================================================================

    internal sealed class Mp3FileWriter : IAudioFileWriter
    {
        private const int Rate = 48000;
        private const int Channels = 2;

        private static readonly object StartupLock = new object();
        private static bool _mfStarted;

        private IMFTransform _mft;
        private FileStream _fs;
        private IMFSample _outSample;
        private IMFMediaBuffer _outBuffer;
        private bool _mftProvidesSamples;
        private byte[] _copy = new byte[64 * 1024];
        private long _time; // 100ns
        private bool _closed;

        public string FilePath { get; }

        public Mp3FileWriter(string path, int bitrateKbps)
        {
            FilePath = path;
            try
            {
                EnsureStartup();
                var t = Type.GetTypeFromCLSID(MF.CLSID_MP3EncoderMFT, true);
                _mft = (IMFTransform)Activator.CreateInstance(t);

                SetOutputType(bitrateKbps);
                SetInputType();

                MF.Check(_mft.GetOutputStreamInfo(0, out var info), "GetOutputStreamInfo");
                _mftProvidesSamples = (info.dwFlags & (MF.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) != 0;
                if (!_mftProvidesSamples)
                {
                    int size = Math.Max(info.cbSize, 256 * 1024);
                    MF.Check(MF.MFCreateSample(out _outSample), "MFCreateSample");
                    MF.Check(MF.MFCreateMemoryBuffer(size, out _outBuffer), "MFCreateMemoryBuffer");
                    MF.Check(_outSample.AddBuffer(_outBuffer), "AddBuffer");
                }

                MF.Check(_mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero), "BEGIN_STREAMING");
                MF.Check(_mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero), "START_OF_STREAM");

                // path == null 时只做“编码器是否可用”的探测，不创建文件
                if (path != null)
                    _fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
            }
            catch
            {
                ReleaseCom();
                throw;
            }
        }

        private static void EnsureStartup()
        {
            lock (StartupLock)
            {
                if (_mfStarted) return;
                MF.Check(MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_LITE), "MFStartup");
                _mfStarted = true;
            }
        }

        /// <summary>优先从编码器支持的输出类型里挑采样率/声道匹配、码率最接近的；失败则手动构造。</summary>
        private void SetOutputType(int kbps)
        {
            int wantBytes = kbps * 1000 / 8;
            IMFMediaType best = null;
            int bestDiff = int.MaxValue;
            for (int i = 0; i < 200; i++)
            {
                int hr = _mft.GetOutputAvailableType(0, i, out IMFMediaType t);
                if (hr < 0 || t == null) break;
                var kRate = MF.MF_MT_AUDIO_SAMPLES_PER_SECOND;
                var kCh = MF.MF_MT_AUDIO_NUM_CHANNELS;
                var kBytes = MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND;
                t.GetUINT32(ref kRate, out int rate);
                t.GetUINT32(ref kCh, out int ch);
                t.GetUINT32(ref kBytes, out int bytes);
                int diff = Math.Abs(bytes - wantBytes);
                if (rate == Rate && ch == Channels && diff < bestDiff)
                {
                    if (best != null) Marshal.ReleaseComObject(best);
                    best = t; bestDiff = diff;
                }
                else Marshal.ReleaseComObject(t);
            }

            if (best != null)
            {
                try
                {
                    if (_mft.SetOutputType(0, best, 0) >= 0) return;
                }
                finally { Marshal.ReleaseComObject(best); }
            }

            // 手动构造
            MF.Check(MF.MFCreateMediaType(out IMFMediaType mt), "MFCreateMediaType");
            try
            {
                SetGuid(mt, MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
                SetGuid(mt, MF.MF_MT_SUBTYPE, MF.MFAudioFormat_MP3);
                SetU32(mt, MF.MF_MT_AUDIO_NUM_CHANNELS, Channels);
                SetU32(mt, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, Rate);
                SetU32(mt, MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, wantBytes);
                MF.Check(_mft.SetOutputType(0, mt, 0), "SetOutputType(MP3)");
            }
            finally { Marshal.ReleaseComObject(mt); }
        }

        private void SetInputType()
        {
            MF.Check(MF.MFCreateMediaType(out IMFMediaType mt), "MFCreateMediaType");
            try
            {
                SetGuid(mt, MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
                SetGuid(mt, MF.MF_MT_SUBTYPE, MF.MFAudioFormat_PCM);
                SetU32(mt, MF.MF_MT_AUDIO_NUM_CHANNELS, Channels);
                SetU32(mt, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, Rate);
                SetU32(mt, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                SetU32(mt, MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, Channels * 2);
                SetU32(mt, MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, Rate * Channels * 2);
                SetU32(mt, MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                MF.Check(_mft.SetInputType(0, mt, 0), "SetInputType(PCM)");
            }
            finally { Marshal.ReleaseComObject(mt); }
        }

        private static void SetGuid(IMFMediaType mt, Guid key, Guid value) => MF.Check(mt.SetGUID(ref key, ref value), "SetGUID");
        private static void SetU32(IMFMediaType mt, Guid key, int value) => MF.Check(mt.SetUINT32(ref key, value), "SetUINT32");

        public void Write(short[] pcm, int frames)
        {
            if (frames <= 0) return;
            int bytes = frames * Channels * 2;
            IMFMediaBuffer buf = null;
            IMFSample sample = null;
            try
            {
                MF.Check(MF.MFCreateMemoryBuffer(bytes, out buf), "MFCreateMemoryBuffer");
                MF.Check(buf.Lock(out IntPtr p, out _, out _), "Lock");
                Marshal.Copy(pcm, 0, p, frames * Channels);
                buf.Unlock();
                MF.Check(buf.SetCurrentLength(bytes), "SetCurrentLength");

                MF.Check(MF.MFCreateSample(out sample), "MFCreateSample");
                MF.Check(sample.AddBuffer(buf), "AddBuffer");
                long dur = frames * 10_000_000L / Rate;
                sample.SetSampleTime(_time);
                sample.SetSampleDuration(dur);
                _time += dur;

                int hr = _mft.ProcessInput(0, sample, 0);
                if (hr == MF.MF_E_NOTACCEPTING)
                {
                    DrainOutput();
                    hr = _mft.ProcessInput(0, sample, 0);
                }
                MF.Check(hr, "ProcessInput");
            }
            finally
            {
                if (sample != null) Marshal.ReleaseComObject(sample);
                if (buf != null) Marshal.ReleaseComObject(buf);
            }
            DrainOutput();
        }

        private void DrainOutput()
        {
            for (int guard = 0; guard < 1000; guard++)
            {
                var db = new MFT_OUTPUT_DATA_BUFFER();
                IntPtr ours = IntPtr.Zero;
                if (!_mftProvidesSamples)
                {
                    _outBuffer.SetCurrentLength(0);
                    ours = Marshal.GetComInterfaceForObject(_outSample, typeof(IMFSample));
                    db.pSample = ours;
                }
                int hr = _mft.ProcessOutput(0, 1, ref db, out _);
                try
                {
                    if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                    MF.Check(hr, "ProcessOutput");

                    IMFSample s = _mftProvidesSamples
                        ? (IMFSample)Marshal.GetObjectForIUnknown(db.pSample)
                        : _outSample;
                    IMFMediaBuffer b = null;
                    try
                    {
                        MF.Check(s.ConvertToContiguousBuffer(out b), "ConvertToContiguousBuffer");
                        MF.Check(b.Lock(out IntPtr p, out _, out int cur), "Lock");
                        try
                        {
                            if (cur > 0)
                            {
                                if (_copy.Length < cur) _copy = new byte[cur];
                                Marshal.Copy(p, _copy, 0, cur);
                                _fs?.Write(_copy, 0, cur);
                            }
                        }
                        finally { b.Unlock(); }
                    }
                    finally
                    {
                        // 每次跨入托管的 COM 指针都会让 RCW 计数 +1（即使是同一个对象），所以总是释放一次
                        if (b != null) Marshal.ReleaseComObject(b);
                        if (_mftProvidesSamples) Marshal.ReleaseComObject(s);
                    }
                }
                finally
                {
                    if (db.pEvents != IntPtr.Zero) Marshal.Release(db.pEvents);
                    if (_mftProvidesSamples && db.pSample != IntPtr.Zero) Marshal.Release(db.pSample);
                    if (ours != IntPtr.Zero) Marshal.Release(ours);
                }
            }
        }

        public void Flush(bool toDisk)
        {
            if (_fs == null) return;
            _fs.Flush(toDisk);
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try
            {
                _mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
                _mft.ProcessMessage(MF.MFT_MESSAGE_COMMAND_DRAIN, IntPtr.Zero);
                DrainOutput();
                _mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
            }
            catch (Exception ex) { Log.Warn("MP3 收尾时出错: " + ex.Message); }
            try { _fs?.Flush(true); } catch { }
            try { _fs?.Dispose(); }
            catch (Exception ex) { Log.Warn("关闭 MP3 文件出错: " + ex.Message); }
            finally
            {
                _fs = null;
                ReleaseCom();
            }
        }

        private void ReleaseCom()
        {
            if (_outBuffer != null) { Marshal.ReleaseComObject(_outBuffer); _outBuffer = null; }
            if (_outSample != null) { Marshal.ReleaseComObject(_outSample); _outSample = null; }
            if (_mft != null) { Marshal.ReleaseComObject(_mft); _mft = null; }
        }

        public void Dispose()
        {
            if (!_closed) Close();
        }

        private static volatile bool _available;

        /// <summary>检查系统是否有可用的 MP3 编码器（N 版 Windows / Win7 可能没有）。只缓存“可用”的结果，失败下次还会再试。</summary>
        public static bool IsAvailable()
        {
            if (_available) return true;
            try
            {
                using (var w = new Mp3FileWriter(null, 128)) { w.Close(); }
                _available = true;
            }
            catch (Exception ex)
            {
                Log.Warn("MP3 编码器不可用: " + ex.Message);
            }
            return _available;
        }
    }

    // ======================================================================
    //  WAV（16bit PCM），每隔几秒回写文件头，异常退出也能播放
    // ======================================================================

    internal sealed class WavFileWriter : IAudioFileWriter
    {
        private const int Rate = 48000;
        private const int Channels = 2;
        private FileStream _fs;
        private long _dataBytes;
        private byte[] _bytes = new byte[0];
        private bool _closed;

        public string FilePath { get; }
        public long DataBytes => _dataBytes;

        public WavFileWriter(string path)
        {
            FilePath = path;
            _fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
            WriteHeader();
        }

        private void WriteHeader()
        {
            long pos = _fs.Position;
            _fs.Position = 0;
            var w = new BinaryWriter(_fs, System.Text.Encoding.ASCII, true);
            uint data = (uint)Math.Min(_dataBytes, uint.MaxValue - 36);
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + data);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)Channels);
            w.Write(Rate);
            w.Write(Rate * Channels * 2);
            w.Write((short)(Channels * 2));
            w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(data);
            w.Flush();
            _fs.Position = Math.Max(pos, 44);
        }

        public void Write(short[] pcm, int frames)
        {
            int bytes = frames * Channels * 2;
            if (_bytes.Length < bytes) _bytes = new byte[bytes];
            Buffer.BlockCopy(pcm, 0, _bytes, 0, bytes);
            _fs.Write(_bytes, 0, bytes);
            _dataBytes += bytes;
        }

        public void Flush(bool toDisk)
        {
            WriteHeader();
            _fs.Flush(toDisk);
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try { WriteHeader(); _fs.Flush(true); } catch { }
            _fs.Dispose();
        }

        public void Dispose() { if (!_closed) Close(); }
    }
}
