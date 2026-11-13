using System;
using System.Runtime.InteropServices;

namespace ShengJi.Interop
{
    // =====================================================================
    //  Windows Core Audio (MMDevice / WASAPI / 会话) COM 声明
    //  注意：ComImport 接口的方法顺序必须与 vtable 完全一致。
    // =====================================================================

    internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [Flags]
    internal enum DeviceState : uint
    {
        Active = 0x1, Disabled = 0x2, NotPresent = 0x4, Unplugged = 0x8, All = 0xF
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
        public PROPERTYKEY(Guid g, int p) { fmtid = g; pid = p; }
    }

    internal static class CoreAudioConst
    {
        public static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        public static readonly PROPERTYKEY PKEY_Device_FriendlyName =
            new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        public const uint CLSCTX_ALL = 0x17;

        public const int AUDCLNT_SHAREMODE_SHARED = 0;

        public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const uint AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;
        public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;

        public const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
        public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        public const uint AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;

        public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
        public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
        public const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;
        public const int E_NOTFOUND = unchecked((int)0x80070490);

        // 进程回环
        public const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
        public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
        public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;

        public const ushort VT_LPWSTR = 31;
        public const ushort VT_BLOB = 65;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out DeviceState state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, IntPtr propVariant);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, IntPtr propVariant);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceState newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PROPERTYKEY key);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
                                     IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out uint flags,
                                    out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFrames);
        [PreserveSig] int GetNextPacketSize(out uint numFrames);
    }

    // ---------------- 进程回环异步激活 ----------------

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult,
                                            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>标记接口：告诉系统回调对象可以跨线程直接调用。</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    internal static class MmDevApi
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);
    }

    // ---------------- 音频会话（用于列出“正在发声的应用”） ----------------

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr audioVolume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    internal enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out AudioSessionState state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    // ---------------- 辅助 ----------------

    internal static class Ole32
    {
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(IntPtr pvar);

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr ptr);
    }

    internal static class CoreAudioUtil
    {
        public static IMMDeviceEnumerator CreateEnumerator()
        {
            var t = Type.GetTypeFromCLSID(CoreAudioConst.CLSID_MMDeviceEnumerator, true);
            return (IMMDeviceEnumerator)Activator.CreateInstance(t);
        }

        public static string GetId(IMMDevice dev)
        {
            return dev.GetId(out string id) == 0 ? id : null;
        }

        public static string GetFriendlyName(IMMDevice dev)
        {
            IPropertyStore store = null;
            IntPtr pv = Marshal.AllocCoTaskMem(32);
            try
            {
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                if (dev.OpenPropertyStore(0 /*STGM_READ*/, out store) != 0 || store == null) return null;
                var key = CoreAudioConst.PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, pv) != 0) return null;
                ushort vt = (ushort)Marshal.ReadInt16(pv, 0);
                string name = null;
                if (vt == CoreAudioConst.VT_LPWSTR)
                    name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8));
                Ole32.PropVariantClear(pv);
                return name;
            }
            catch { return null; }
            finally
            {
                Marshal.FreeCoTaskMem(pv);
                if (store != null) Marshal.ReleaseComObject(store);
            }
        }

        public static void Release(object com)
        {
            try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
        }
    }
}
