using System;
using System.Runtime.InteropServices;
using System.Threading;
using ShengJi.Interop;

namespace ShengJi.Audio
{
    /// <summary>
    /// 往指定播放设备播放一段 48kHz 立体声 float 音频（阻塞直到播完）。用于对齐校准的提示音。
    /// 共享模式，优先让系统自动转换格式；不支持时按设备混音格式（float）播放并在这里重采样到设备采样率。
    /// </summary>
    internal static class RenderPlayer
    {
        public static void Play(string deviceId, float[] stereo48k, CancellationToken cancel)
        {
            IMMDeviceEnumerator en = null;
            IMMDevice dev = null;
            IAudioClient client = null;
            IAudioRenderClient render = null;
            IntPtr pwfx = IntPtr.Zero;
            try
            {
                en = CoreAudioUtil.CreateEnumerator();
                Check(en.GetDevice(deviceId, out dev), "GetDevice");
                var iid = CoreAudioConst.IID_IAudioClient;
                Check(dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out object o), "Activate");
                client = (IAudioClient)o;

                const long bufferHns = 2_000_000; // 200ms
                float[] data = stereo48k;
                int channels = 2;
                pwfx = SourceFormat.AllocFloatStereo48k();
                int hr = client.Initialize(CoreAudioConst.AUDCLNT_SHAREMODE_SHARED,
                    CoreAudioConst.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                    bufferHns, 0, pwfx, IntPtr.Zero);
                if (hr < 0)
                {
                    // 退回设备混音格式（共享模式下总是 float）
                    Marshal.FreeCoTaskMem(pwfx); pwfx = IntPtr.Zero;
                    CoreAudioUtil.Release(client);
                    Check(dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o), "Activate");
                    client = (IAudioClient)o;
                    Check(client.GetMixFormat(out pwfx), "GetMixFormat");
                    var fmt = SourceFormat.FromPointer(pwfx);
                    if (!fmt.IsFloat || fmt.BitsPerSample != 32) throw new NotSupportedException("设备混音格式不是 float32");
                    Check(client.Initialize(CoreAudioConst.AUDCLNT_SHAREMODE_SHARED, 0, bufferHns, 0, pwfx, IntPtr.Zero), "Initialize");
                    channels = fmt.Channels;
                    data = Convert(stereo48k, fmt.SampleRate, channels);
                }

                Check(client.GetBufferSize(out uint bufFrames), "GetBufferSize");
                var riid = CoreAudioConst.IID_IAudioRenderClient;
                Check(client.GetService(ref riid, out object ro), "GetService");
                render = (IAudioRenderClient)ro;

                int total = data.Length / channels, written = 0;
                bool started = false;
                while (!cancel.IsCancellationRequested)
                {
                    Check(client.GetCurrentPadding(out uint padding), "GetCurrentPadding");
                    if (written >= total)
                    {
                        if (padding == 0) break;
                    }
                    else
                    {
                        int n = Math.Min((int)(bufFrames - padding), total - written);
                        if (n > 0)
                        {
                            Check(render.GetBuffer((uint)n, out IntPtr p), "GetBuffer");
                            Marshal.Copy(data, written * channels, p, n * channels);
                            Check(render.ReleaseBuffer((uint)n, 0), "ReleaseBuffer");
                            written += n;
                        }
                    }
                    if (!started) { Check(client.Start(), "Start"); started = true; }
                    Thread.Sleep(10);
                }
                try { client.Stop(); } catch { }
            }
            finally
            {
                if (pwfx != IntPtr.Zero) Marshal.FreeCoTaskMem(pwfx);
                CoreAudioUtil.Release(render);
                CoreAudioUtil.Release(client);
                CoreAudioUtil.Release(dev);
                CoreAudioUtil.Release(en);
            }
        }

        /// <summary>48k 立体声 → 指定采样率 / 声道数（线性插值，提示音用足够）。</summary>
        private static float[] Convert(float[] src, int rate, int channels)
        {
            int inFrames = src.Length / 2;
            int outFrames = (int)((long)inFrames * rate / 48000);
            var dst = new float[outFrames * channels];
            for (int i = 0; i < outFrames; i++)
            {
                double x = (double)i * 48000 / rate;
                int k = (int)x; double f = x - k;
                int k1 = Math.Min(k + 1, inFrames - 1);
                float l = (float)(src[k * 2] * (1 - f) + src[k1 * 2] * f);
                float r = (float)(src[k * 2 + 1] * (1 - f) + src[k1 * 2 + 1] * f);
                for (int c = 0; c < channels; c++) dst[i * channels + c] = c == 1 ? r : c == 0 ? l : 0f;
            }
            return dst;
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException($"{what} 失败 (0x{hr:X8})", hr);
        }
    }
}
