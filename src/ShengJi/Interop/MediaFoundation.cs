using System;
using System.Runtime.InteropServices;

namespace ShengJi.Interop
{
    // =====================================================================
    //  Media Foundation：直接驱动系统自带的 MP3 编码器 MFT（Windows 8+）。
    //  我们自己把编码出来的 MP3 帧写进文件，这样程序意外退出时，
    //  已写入的部分也是一个可以正常播放的 MP3。
    // =====================================================================

    internal static class MF
    {
        public const int MF_VERSION = 0x00020070;
        public const int MFSTARTUP_LITE = 0x1;

        public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
        public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
        public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

        public const int MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
        public const int MFT_MESSAGE_COMMAND_DRAIN = 0x00000001;
        public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
        public const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
        public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
        public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

        public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
        public const int MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;

        public static readonly Guid CLSID_MP3EncoderMFT = new Guid("11103421-354C-4CCA-A7A3-1AFF9A5B6701");

        public static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new Guid("c9173739-5e56-461c-b713-46fb995cb95f");

        public static readonly Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormat_MP3 = new Guid("00000055-0000-0010-8000-00AA00389B71");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType mediaType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample sample);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " 失败 (0x" + hr.ToString("X8") + ")", hr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_INPUT_STREAM_INFO
    {
        public long hnsMaxLatency;
        public int dwFlags;
        public int cbSize;
        public int cbMaxLookahead;
        public int cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_STREAM_INFO
    {
        public int dwFlags;
        public int cbSize;
        public int cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_DATA_BUFFER
    {
        public int dwStreamID;
        public IntPtr pSample;   // IMFSample*（我们自己管理引用）
        public int dwStatus;
        public IntPtr pEvents;   // IMFCollection*（若非空需要 Release）
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr buffer, int size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr str, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, IntPtr blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr dest);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType
    {
        // IMFAttributes
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr buffer, int size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr str, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, IntPtr blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr dest);
        // IMFMediaType
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out bool compressed);
        [PreserveSig] int IsEqual(IntPtr other, out int flags);
        [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr representation);
        [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr representation);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int currentLength);
        [PreserveSig] int SetCurrentLength(int currentLength);
        [PreserveSig] int GetMaxLength(out int maxLength);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        // IMFAttributes
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr buffer, int size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr str, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, IntPtr blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr dest);
        // IMFSample
        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int totalLength);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out int inputMin, out int inputMax, out int outputMin, out int outputMax);
        [PreserveSig] int GetStreamCount(out int inputStreams, out int outputStreams);
        [PreserveSig] int GetStreamIDs(int inputIdArraySize, IntPtr inputIds, int outputIdArraySize, IntPtr outputIds);
        [PreserveSig] int GetInputStreamInfo(int inputStreamId, out MFT_INPUT_STREAM_INFO streamInfo);
        [PreserveSig] int GetOutputStreamInfo(int outputStreamId, out MFT_OUTPUT_STREAM_INFO streamInfo);
        [PreserveSig] int GetAttributes(out IntPtr attributes);
        [PreserveSig] int GetInputStreamAttributes(int inputStreamId, out IntPtr attributes);
        [PreserveSig] int GetOutputStreamAttributes(int outputStreamId, out IntPtr attributes);
        [PreserveSig] int DeleteInputStream(int streamId);
        [PreserveSig] int AddInputStreams(int streams, IntPtr streamIds);
        [PreserveSig] int GetInputAvailableType(int inputStreamId, int typeIndex, out IMFMediaType type);
        [PreserveSig] int GetOutputAvailableType(int outputStreamId, int typeIndex, out IMFMediaType type);
        [PreserveSig] int SetInputType(int inputStreamId, IMFMediaType type, int flags);
        [PreserveSig] int SetOutputType(int outputStreamId, IMFMediaType type, int flags);
        [PreserveSig] int GetInputCurrentType(int inputStreamId, out IMFMediaType type);
        [PreserveSig] int GetOutputCurrentType(int outputStreamId, out IMFMediaType type);
        [PreserveSig] int GetInputStatus(int inputStreamId, out int flags);
        [PreserveSig] int GetOutputStatus(out int flags);
        [PreserveSig] int SetOutputBounds(long lowerBound, long upperBound);
        [PreserveSig] int ProcessEvent(int inputStreamId, IntPtr mediaEvent);
        [PreserveSig] int ProcessMessage(int message, IntPtr param);
        [PreserveSig] int ProcessInput(int inputStreamId, IMFSample sample, int flags);
        // 只传 1 个输出缓冲：按引用传结构体即等价于 C 里的数组指针（数组参数在 COM 接口上默认会被当成 SAFEARRAY）
        [PreserveSig] int ProcessOutput(int flags, int outputBufferCount,
                                        ref MFT_OUTPUT_DATA_BUFFER outputSample, out int status);
    }
}
