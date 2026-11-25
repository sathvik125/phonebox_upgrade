#if WINDOWS
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// H.264 from NV12 pictures with the encoder Windows ships (Media Foundation's software encoder, in every Windows 10 and
/// 11 edition but the N ones without the Media Feature Pack), set up for live use: low latency, no B-frames, Baseline
/// profile so every phone decoder takes it, a key frame every second or when asked. Output is Annex B (start
/// codes), one access unit per picture, with the SPS and PPS in front of every key frame.
/// </summary>
internal sealed class H264Encoder : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }

    private readonly IMFTransform _mft;
    private readonly ICodecAPI? _codec;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private byte[]? _sps, _pps;
    private bool _disposed;

    public H264Encoder(int width, int height, int fps, int bitrate)
    {
        Width = width; Height = height; Fps = fps;
        Startup();
        _mft = (IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MSH264EncoderMFT, true)!)!;
        _codec = _mft as ICodecAPI;
        try
        {
            if (_mft.GetAttributes(out var attrs) >= 0 && attrs is not null) { attrs.SetUINT32(MF_LOW_LATENCY, 1); Marshal.ReleaseComObject(attrs); }
        }
        catch { /* older encoders: low latency through ICodecAPI below */ }
        if (_codec is not null)
        {
            TrySet(CODECAPI_AVLowLatencyMode, true);
            TrySet(CODECAPI_AVEncCommonRateControlMode, 0u); // CBR
            TrySet(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
            TrySet(CODECAPI_AVEncMPVGOPSize, (uint)fps); // a key frame a second
            TrySet(CODECAPI_AVEncMPVDefaultBPictureCount, 0u);
        }

        // the encoder wants its output type first, then the input type
        MFCreateMediaType(out var output);
        output.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        output.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        output.SetUINT32(MF_MT_AVG_BITRATE, (uint)bitrate);
        output.SetUINT64(MF_MT_FRAME_SIZE, Pack((uint)width, (uint)height));
        output.SetUINT64(MF_MT_FRAME_RATE, Pack((uint)fps, 1));
        output.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        output.SetUINT32(MF_MT_INTERLACE_MODE, 2); // progressive
        output.SetUINT32(MF_MT_MPEG2_PROFILE, 66); // Baseline
        Check(_mft.SetOutputType(0, output, 0), "output type");
        Marshal.ReleaseComObject(output);

        MFCreateMediaType(out var input);
        input.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        input.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
        input.SetUINT64(MF_MT_FRAME_SIZE, Pack((uint)width, (uint)height));
        input.SetUINT64(MF_MT_FRAME_RATE, Pack((uint)fps, 1));
        input.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        input.SetUINT32(MF_MT_INTERLACE_MODE, 2);
        Check(_mft.SetInputType(0, input, 0), "input type");
        Marshal.ReleaseComObject(input);

        Check(_mft.GetOutputStreamInfo(0, out var info), "output stream info");
        _providesSamples = (info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        _outputSize = (int)Math.Max(info.cbSize, (uint)(width * height * 3 / 2));

        _mft.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        _mft.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }

    /// <summary>The codec string for WebCodecs ("avc1.42C01F"), known once the first key frame is out.</summary>
    public string? Codec => _sps is { Length: >= 4 } s ? $"avc1.{s[1]:X2}{s[2]:X2}{s[3]:X2}" : null;

    /// <summary>
    /// Encodes one NV12 picture (Width x Height, Y plane then interleaved UV) taken at <paramref name="timeUs"/>. Returns
    /// the access units that came out (none while the encoder still holds the picture), each with whether it is a key frame.
    /// </summary>
    public List<(byte[] Data, bool Key, long TimeUs)> Encode(ReadOnlySpan<byte> nv12, long timeUs, bool forceKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (forceKey) TrySet(CODECAPI_AVEncVideoForceKeyFrame, 1u);
        var sample = CreateSample(nv12, timeUs);
        try
        {
            var hr = _mft.ProcessInput(0, sample, 0);
            if (hr == MF_E_NOTACCEPTING) { Drain(out var pending); _mft.ProcessInput(0, sample, 0); return pending; }
            Check(hr, "encode");
        }
        finally { Marshal.ReleaseComObject(sample); }
        Drain(out var outs);
        return outs;
    }

    private void Drain(out List<(byte[] Data, bool Key, long TimeUs)> outs)
    {
        outs = [];
        for (var guard = 0; guard < 16; guard++)
        {
            IMFSample? own = null;
            if (!_providesSamples)
            {
                MFCreateSample(out own);
                MFCreateMemoryBuffer((uint)_outputSize, out var buf);
                own.AddBuffer(buf);
                Marshal.ReleaseComObject(buf);
            }
            var data = new MFT_OUTPUT_DATA_BUFFER[1];
            data[0].pSample = own is null ? 0 : Marshal.GetIUnknownForObject(own);
            var hr = _mft.ProcessOutput(0, 1, ref data[0], out _);
            if (data[0].pEvents != 0) { Marshal.Release(data[0].pEvents); data[0].pEvents = 0; }
            try
            {
                if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    // the encoder settled on its output type: take the one it offers and go on
                    if (_mft.GetOutputAvailableType(0, 0, out var t) >= 0 && t is not null) { _mft.SetOutputType(0, t, 0); Marshal.ReleaseComObject(t); }
                    continue;
                }
                Check(hr, "read encoded picture");
                if (data[0].pSample == 0) continue;
                var produced = (IMFSample)Marshal.GetObjectForIUnknown(data[0].pSample);
                try
                {
                    produced.GetSampleTime(out var t100ns);
                    var bytes = Copy(produced);
                    if (bytes.Length > 0 && Shape(bytes) is { } unit) outs.Add((unit.Data, unit.Key, t100ns / 10));
                }
                finally { if (!ReferenceEquals(produced, own)) Marshal.ReleaseComObject(produced); }
            }
            finally
            {
                if (data[0].pSample != 0) Marshal.Release(data[0].pSample);
                if (own is not null) Marshal.ReleaseComObject(own);
            }
        }
    }

    /// <summary>
    /// Remembers the SPS and PPS, and puts them in front of a key frame that came without them. SVC extension units
    /// (prefix NAL 14, slice extension 20), which some encoders add for temporal layers, are dropped: a phone's
    /// Baseline decoder shows a green picture on them.
    /// </summary>
    private (byte[] Data, bool Key)? Shape(byte[] au)
    {
        bool key = false, hasSps = false, hasPps = false, svc = false;
        foreach (var (start, end) in Nals(au))
        {
            var type = au[start] & 0x1F;
            if (type == 5) key = true;
            else if (type == 7) { hasSps = true; _sps = au[start..end]; }
            else if (type == 8) { hasPps = true; _pps = au[start..end]; }
            else if (type is 14 or 20) svc = true;
        }
        if (svc)
        {
            using var kept = new MemoryStream(au.Length);
            foreach (var (start, end) in Nals(au))
                if ((au[start] & 0x1F) is not (14 or 20)) { kept.Write(StartCode); kept.Write(au, start, end - start); }
            au = kept.ToArray();
        }
        if (key && (!hasSps || !hasPps) && _sps is not null && _pps is not null)
        {
            using var ms = new MemoryStream(au.Length + _sps.Length + _pps.Length + 8);
            ms.Write(StartCode); ms.Write(_sps); ms.Write(StartCode); ms.Write(_pps); ms.Write(au);
            return (ms.ToArray(), true);
        }
        return (au, key);
    }

    private static readonly byte[] StartCode = [0, 0, 0, 1];

    /// <summary>The NAL units of an Annex B access unit, as [first byte after the start code, end).</summary>
    internal static IEnumerable<(int Start, int End)> Nals(byte[] au)
    {
        var starts = new List<int>();
        for (var i = 0; i + 2 < au.Length; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) { starts.Add(i + 3); i += 2; }
        }
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1] - 3 : au.Length;
            if (k + 1 < starts.Count && end > 0 && au[end - 1] == 0) end--; // a four-byte start code
            if (end > starts[k]) yield return (starts[k], end);
        }
    }

    private IMFSample CreateSample(ReadOnlySpan<byte> nv12, long timeUs)
    {
        MFCreateMemoryBuffer((uint)nv12.Length, out var buffer);
        buffer.Lock(out var ptr, out _, out _);
        unsafe { nv12.CopyTo(new Span<byte>((void*)ptr, nv12.Length)); }
        buffer.Unlock();
        buffer.SetCurrentLength((uint)nv12.Length);
        MFCreateSample(out var sample);
        sample.AddBuffer(buffer);
        Marshal.ReleaseComObject(buffer);
        sample.SetSampleTime(timeUs * 10);
        sample.SetSampleDuration(10_000_000L / Fps);
        return sample;
    }

    private static byte[] Copy(IMFSample sample)
    {
        sample.ConvertToContiguousBuffer(out var buffer);
        try
        {
            buffer.Lock(out var ptr, out _, out var length);
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(ptr, bytes, 0, (int)length);
                return bytes;
            }
            finally { buffer.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buffer); }
    }

    private void TrySet(Guid api, object value)
    {
        if (_codec is null) return;
        try { _codec.SetValue(api, ref value); } catch { /* not every encoder knows every setting */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mft.ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0); _mft.ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0); } catch { }
        try { Marshal.ReleaseComObject(_mft); } catch { }
    }

    private static int _started;
    private static void Startup()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) Check(MFStartup(0x00020070, 0), "Media Foundation");
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"H.264 encoder: {what} failed (0x{hr:X8})");
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    // ------------------------------------------------------------------ Media Foundation interop

    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    private const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100, MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;
    private const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000, MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001,
        MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002, MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    private static readonly Guid CLSID_MSH264EncoderMFT = new("6CA50344-051A-4DED-9779-A43305165E35");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid MF_MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("AD76A80B-2D5C-4E0B-B375-64E520137036");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_LOW_LATENCY = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    private static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    private static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", PreserveSig = false)] private static extern void MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", PreserveSig = false)] private static extern void MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", PreserveSig = false)] private static extern void MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_STREAM_INFO { public uint dwFlags, cbSize, cbAlignment; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_DATA_BUFFER { public uint dwStreamID; public nint pSample; public uint dwStatus; public nint pEvents; }

    // COM interfaces repeat every method of the interfaces they extend, in vtable order. Methods never called are kept
    // as placeholders (_xx) so the slots after them stay in place.

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare();
        void GetUINT32(in Guid key, out uint value); void GetUINT64(in Guid key, out ulong value); void _GetDouble(); void GetGUID(in Guid key, out Guid value);
        void _GetStringLength(); void _GetString(); void _GetAllocatedString(); void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        void SetUINT32(in Guid key, uint value); void SetUINT64(in Guid key, ulong value); void _SetDouble(); void SetGUID(in Guid key, in Guid value);
        void _SetString(); void _SetBlob(); void _SetUnknown(); void _LockStore(); void _UnlockStore(); void _GetCount(); void _GetItemByIndex(); void _CopyAllItems();
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare();
        void GetUINT32(in Guid key, out uint value); void GetUINT64(in Guid key, out ulong value); void _GetDouble(); void GetGUID(in Guid key, out Guid value);
        void _GetStringLength(); void _GetString(); void _GetAllocatedString(); void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        void SetUINT32(in Guid key, uint value); void SetUINT64(in Guid key, ulong value); void _SetDouble(); void SetGUID(in Guid key, in Guid value);
        void _SetString(); void _SetBlob(); void _SetUnknown(); void _LockStore(); void _UnlockStore(); void _GetCount(); void _GetItemByIndex(); void _CopyAllItems();
        void _GetMajorType(); void _IsCompressedFormat(); void _IsEqual(); void _GetRepresentation(); void _FreeRepresentation();
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare();
        void _GetUINT32(); void _GetUINT64(); void _GetDouble(); void _GetGUID();
        void _GetStringLength(); void _GetString(); void _GetAllocatedString(); void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        void _SetUINT32(); void _SetUINT64(); void _SetDouble(); void _SetGUID();
        void _SetString(); void _SetBlob(); void _SetUnknown(); void _LockStore(); void _UnlockStore(); void _GetCount(); void _GetItemByIndex(); void _CopyAllItems();
        void _GetSampleFlags(); void _SetSampleFlags();
        void GetSampleTime(out long time); void SetSampleTime(long time);
        void _GetSampleDuration(); void SetSampleDuration(long duration);
        void _GetBufferCount(); void _GetBufferByIndex(); void ConvertToContiguousBuffer(out IMFMediaBuffer buffer); void AddBuffer(IMFMediaBuffer buffer);
        void _RemoveBufferByIndex(); void _RemoveAllBuffers(); void _GetTotalLength(); void _CopyToBuffer();
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        void Lock(out nint buffer, out uint maxLength, out uint currentLength);
        void Unlock();
        void _GetCurrentLength();
        void SetCurrentLength(uint length);
        void _GetMaxLength();
    }

    [ComImport, Guid("BF94C121-5B05-4E6F-8000-BA598961414D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFTransform
    {
        void _GetStreamLimits(); void _GetStreamCount(); void _GetStreamIDs(); void _GetInputStreamInfo();
        [PreserveSig] int GetOutputStreamInfo(uint stream, out MFT_OUTPUT_STREAM_INFO info);
        [PreserveSig] int GetAttributes(out IMFAttributes attributes);
        void _GetInputStreamAttributes(); void _GetOutputStreamAttributes(); void _DeleteInputStream(); void _AddInputStreams();
        void _GetInputAvailableType();
        [PreserveSig] int GetOutputAvailableType(uint stream, uint index, out IMFMediaType type);
        [PreserveSig] int SetInputType(uint stream, IMFMediaType type, uint flags);
        [PreserveSig] int SetOutputType(uint stream, IMFMediaType type, uint flags);
        void _GetInputCurrentType(); void _GetOutputCurrentType(); void _GetInputStatus(); void _GetOutputStatus(); void _SetOutputBounds(); void _ProcessEvent();
        [PreserveSig] int ProcessMessage(uint message, nint param);
        [PreserveSig] int ProcessInput(uint stream, IMFSample sample, uint flags);
        // one output stream: a single struct by reference (an array here would be marshaled as a SAFEARRAY)
        [PreserveSig] int ProcessOutput(uint flags, uint count, ref MFT_OUTPUT_DATA_BUFFER buffer, out uint status);
    }

    [ComImport, Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        void _IsSupported(); void _IsModifiable(); void _GetParameterRange(); void _GetParameterValues(); void _GetDefaultValue(); void _GetValue();
        void SetValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
#endif
