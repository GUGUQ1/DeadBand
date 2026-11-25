using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeadBand
{
    // =====================================================================
    //  WASAPI 回环采集的 COM 互操作声明
    //  只用到「默认渲染设备的回环流」，够坐频谱条了。
    // =====================================================================
    internal static class AudioInterop
    {
        public const int CLSCTX_ALL = 23;
        public const int ERender = 0;
        public const int EConsole = 0;
        public const int AUDCLNT_SHAREMODE_SHARED = 0;
        public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

        public const int WAVE_FORMAT_PCM = 1;
        public const int WAVE_FORMAT_IEEE_FLOAT = 3;
        public const int WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

        public static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        public static readonly Guid SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid SUBTYPE_FLOAT = new Guid("00000003-0000-0010-8000-00AA00389B71");

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        internal class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            int OpenPropertyStore(int access, out IntPtr properties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int GetState(out int state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioClient
        {
            int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
                IntPtr format, IntPtr audioSessionGuid);
            int GetBufferSize(out uint bufferFrames);
            int GetStreamLatency(out long latency);
            int GetCurrentPadding(out uint padding);
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            int GetMixFormat(out IntPtr format);
            int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr eventHandle);
            int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr data, out uint numFrames, out uint flags,
                out long devicePosition, out long qpcPosition);
            int ReleaseBuffer(uint numFrames);
            int GetNextPacketSize(out uint numFrames);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal struct WAVEFORMATEXTENSIBLE
        {
            public WAVEFORMATEX Format;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            public Guid SubFormat;
        }
    }

    // =====================================================================
    //  系统声音回环采集：把默认播放设备正在播的东西抓成单声道浮点样本
    // =====================================================================
    internal sealed class LoopbackCapture
    {
        private const int RingSize = 16384;

        private readonly float[] ring = new float[RingSize];
        private int writePos;
        private readonly object gate = new object();

        private Thread thread;
        private volatile bool running;
        private int sampleRate;
        private string lastError = "";
        private bool failed;

        public int SampleRate { get { return sampleRate; } }
        public string LastError { get { return lastError; } }
        public bool Failed { get { return failed; } }
        public bool IsRunning { get { return running; } }
        public string DeviceId { get; private set; }

        /// <summary>
        /// 读当前「默认播放设备」的 ID。
        /// 用来发现声音换设备了（插耳机、切蓝牙、播放器改输出），此时必须重新绑定，
        /// 否则回环采集会一直停在旧端点上收不到任何数据。
        /// </summary>
        public static string GetDefaultDeviceId()
        {
            object enObj = null;
            AudioInterop.IMMDevice dev = null;
            try
            {
                var en = (AudioInterop.IMMDeviceEnumerator)
                    (new AudioInterop.MMDeviceEnumeratorComObject());
                enObj = en;
                if (en.GetDefaultAudioEndpoint(AudioInterop.ERender, AudioInterop.EConsole, out dev) != 0
                    || dev == null) return "";
                string id;
                if (dev.GetId(out id) != 0) return "";
                return id == null ? "" : id;
            }
            catch { return ""; }
            finally
            {
                if (dev != null) { try { Marshal.ReleaseComObject(dev); } catch { } }
                if (enObj != null) { try { Marshal.ReleaseComObject(enObj); } catch { } }
            }
        }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "DeadBand-Audio";
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            Thread t = thread;
            thread = null;
            if (t != null) { try { t.Join(600); } catch { } }
        }

        /// <summary>取最近 n 个单声道样本（不够就补 0）。</summary>
        public float[] Snapshot(int n)
        {
            float[] dst = new float[n];
            lock (gate)
            {
                int need = Math.Min(n, RingSize);
                int start = writePos - need;
                while (start < 0) start += RingSize;
                int pad = n - need;
                for (int i = 0; i < need; i++)
                    dst[pad + i] = ring[(start + i) % RingSize];
            }
            return dst;
        }

        private void Push(float[] src, int count)
        {
            lock (gate)
            {
                for (int i = 0; i < count; i++)
                {
                    ring[writePos] = src[i];
                    writePos++;
                    if (writePos >= RingSize) writePos = 0;
                }
            }
        }

        private void Note(string msg)
        {
            lastError = msg;
            failed = !msg.StartsWith("ok");
            try
            {
                File.WriteAllText(Path.Combine(ProfileStore.DataDir, "audio.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + msg + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private void Loop()
        {
            object enumeratorObj = null;
            AudioInterop.IMMDevice device = null;
            AudioInterop.IAudioClient client = null;
            AudioInterop.IAudioCaptureClient capture = null;
            IntPtr mixPtr = IntPtr.Zero;
            try
            {
                var enumerator = (AudioInterop.IMMDeviceEnumerator)
                    (new AudioInterop.MMDeviceEnumeratorComObject());
                enumeratorObj = enumerator;

                int hr = enumerator.GetDefaultAudioEndpoint(
                    AudioInterop.ERender, AudioInterop.EConsole, out device);
                if (hr != 0 || device == null) { Note("找不到默认播放设备 hr=0x" + hr.ToString("X8")); return; }
                try
                {
                    string did;
                    if (device.GetId(out did) == 0) DeviceId = did;
                }
                catch { }

                Guid iid = AudioInterop.IID_IAudioClient;
                object o;
                hr = device.Activate(ref iid, AudioInterop.CLSCTX_ALL, IntPtr.Zero, out o);
                if (hr != 0 || o == null) { Note("Activate IAudioClient 失败 hr=0x" + hr.ToString("X8")); return; }
                client = (AudioInterop.IAudioClient)o;

                hr = client.GetMixFormat(out mixPtr);
                if (hr != 0 || mixPtr == IntPtr.Zero) { Note("GetMixFormat 失败 hr=0x" + hr.ToString("X8")); return; }

                var fmt = (AudioInterop.WAVEFORMATEX)Marshal.PtrToStructure(
                    mixPtr, typeof(AudioInterop.WAVEFORMATEX));
                int channels = fmt.nChannels;
                sampleRate = (int)fmt.nSamplesPerSec;
                int bits = fmt.wBitsPerSample;
                int tag = fmt.wFormatTag;
                bool isFloat = (tag == AudioInterop.WAVE_FORMAT_IEEE_FLOAT);

                if (tag == AudioInterop.WAVE_FORMAT_EXTENSIBLE)
                {
                    var ext = (AudioInterop.WAVEFORMATEXTENSIBLE)Marshal.PtrToStructure(
                        mixPtr, typeof(AudioInterop.WAVEFORMATEXTENSIBLE));
                    isFloat = (ext.SubFormat == AudioInterop.SUBTYPE_FLOAT);
                }

                // 回环：100ms 缓冲
                hr = client.Initialize(AudioInterop.AUDCLNT_SHAREMODE_SHARED,
                    AudioInterop.AUDCLNT_STREAMFLAGS_LOOPBACK, 1000000, 0, mixPtr, IntPtr.Zero);
                if (hr != 0)
                {
                    Note("Initialize 失败 hr=0x" + hr.ToString("X8") +
                         "  fmt=" + tag + "/" + channels + "ch/" + bits + "bit/" + sampleRate + "Hz");
                    return;
                }

                Guid capIid = AudioInterop.IID_IAudioCaptureClient;
                object capObj;
                hr = client.GetService(ref capIid, out capObj);
                if (hr != 0 || capObj == null) { Note("GetService 失败 hr=0x" + hr.ToString("X8")); return; }
                capture = (AudioInterop.IAudioCaptureClient)capObj;

                hr = client.Start();
                if (hr != 0) { Note("Start 失败 hr=0x" + hr.ToString("X8")); return; }

                Note("ok  " + sampleRate + "Hz " + channels + "ch " +
                     (isFloat ? "float" : bits + "bit") + " loopback");

                var mono = new float[65536];
                long statTicks = Environment.TickCount;
                long packets = 0, framesTotal = 0;
                float peak = 0f;
                int hrErr = 0;

                while (running)
                {
                    uint packet;
                    int hrNext = capture.GetNextPacketSize(out packet);
                    if (hrNext != 0) { hrErr = hrNext; Thread.Sleep(10); continue; }

                    if (packet == 0)
                    {
                        Thread.Sleep(10);
                        // 没有新数据时也要写统计，否则"没声音"和"没采到"分不清
                        if (Environment.TickCount - statTicks > 5000)
                        {
                            statTicks = Environment.TickCount;
                            WriteStats(packets, framesTotal, peak, hrErr);
                            packets = 0; framesTotal = 0; peak = 0f;
                        }
                        continue;
                    }

                    while (packet > 0 && running)
                    {
                        IntPtr data;
                        uint frames;
                        uint flags;
                        long devPos, qpc;
                        if (capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc) != 0)
                            break;

                        if (frames > 0)
                        {
                            int n = (int)frames;
                            packets++;
                            framesTotal += n;
                            if (mono.Length < n) mono = new float[n];
                            if ((flags & AudioInterop.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                            {
                                for (int i = 0; i < n; i++) mono[i] = 0f;
                            }
                            else
                            {
                                ReadSamples(data, frames, channels, isFloat, bits, mono);
                            }
                            Push(mono, n);
                            for (int q = 0; q < n; q++)
                            {
                                float av = mono[q] < 0 ? -mono[q] : mono[q];
                                if (av > peak) peak = av;
                            }

                        }
                        capture.ReleaseBuffer(frames);

                        if (Environment.TickCount - statTicks > 5000)
                        {
                            statTicks = Environment.TickCount;
                            WriteStats(packets, framesTotal, peak, hrErr);
                            packets = 0; framesTotal = 0; peak = 0f;
                        }
                        if (capture.GetNextPacketSize(out packet) != 0) break;
                    }
                }

                try { client.Stop(); } catch { }
            }
            catch (Exception ex)
            {
                Note("异常：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                if (capture != null) { try { Marshal.ReleaseComObject(capture); } catch { } }
                if (client != null) { try { Marshal.ReleaseComObject(client); } catch { } }
                if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
                if (enumeratorObj != null) { try { Marshal.ReleaseComObject(enumeratorObj); } catch { } }
                if (mixPtr != IntPtr.Zero) { try { Marshal.FreeCoTaskMem(mixPtr); } catch { } }
                running = false;
            }
        }

        private void WriteStats(long packets, long framesTotal, float peak, int hrErr)
        {
            try
            {
                File.WriteAllText(Path.Combine(ProfileStore.DataDir, "audio-stats.txt"),
                    DateTime.Now.ToString("HH:mm:ss") +
                    "\r\npackets=" + packets + " frames=" + framesTotal +
                    " peak=" + peak.ToString("F4") + " hrNext=0x" + hrErr.ToString("X8") +
                    " writingPos=" + writePos + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static void ReadSamples(IntPtr data, uint frames, int channels,
            bool isFloat, int bits, float[] dst)
        {
            int n = (int)frames;
            if (channels < 1) channels = 1;

            int total = n * channels;
            if (isFloat || bits == 32)
            {
                var tmp = new float[total];
                Marshal.Copy(data, tmp, 0, total);
                for (int f = 0; f < n; f++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++) sum += tmp[f * channels + c];
                    dst[f] = sum / channels;
                }
            }
            else
            {
                var tmp = new short[total];
                Marshal.Copy(data, tmp, 0, total);
                for (int f = 0; f < n; f++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++) sum += tmp[f * channels + c];
                    dst[f] = sum / (channels * 32768f);
                }
            }
        }
    }

    // =====================================================================
    //  简易 radix-2 FFT
    // =====================================================================
    internal static class FftCore
    {
        public static void Magnitude(float[] re, float[] im, int n, float[] mag)
        {
            int j = 0;
            for (int i = 0; i < n - 1; i++)
            {
                if (i < j)
                {
                    float tr = re[i]; re[i] = re[j]; re[j] = tr;
                    float ti = im[i]; im[i] = im[j]; im[j] = ti;
                }
                int k = n >> 1;
                while (k <= j) { j -= k; k >>= 1; }
                j += k;
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                float wr = (float)Math.Cos(ang);
                float wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cwr = 1f, cwi = 0f;
                    int half = len >> 1;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k;
                        int b = i + k + half;
                        float tr = cwr * re[b] - cwi * im[b];
                        float ti = cwr * im[b] + cwi * re[b];
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                        float nwr = cwr * wr - cwi * wi;
                        cwi = cwr * wi + cwi * wr;
                        cwr = nwr;
                    }
                }
            }

            for (int i = 0; i < n / 2 && i < mag.Length; i++)
                mag[i] = (float)Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
        }
    }
}
