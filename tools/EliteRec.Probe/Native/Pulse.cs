using System.Runtime.InteropServices;

namespace EliteRec.Probe.Native;

// ABI и схема threaded mainloop — адаптация EliteSIP.Audio.Pulse/Native/LibPulse.cs.
// Только функции стенда R0. Числа sample/stream flags совпадают с заголовками libpulse.
internal static unsafe partial class Pulse
{
    private const string Library = "libpulse.so.0";
    internal const int Ready = 2, Failed = 3, Terminated = 4;
    internal const int AutoTiming = 8, Interpolate = 2, AdjustLatency = 0x2000, DontMove = 0x200;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleSpec { public int Format; public uint Rate; public byte Channels; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferAttr
    {
        public uint MaxLength, TLength, PreBuf, MinReq, FragSize;
    }
    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_new")]
    internal static partial nint LoopNew();
    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_free")]
    internal static partial void LoopFree(nint loop);
    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_start")]
    internal static partial int LoopStart(nint loop);
    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_stop")]
    internal static partial void LoopStop(nint loop);
    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_lock")]
    internal static partial void LoopLock(nint loop);
    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_unlock")]
    internal static partial void LoopUnlock(nint loop);
    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_get_api")]
    internal static partial nint LoopApi(nint loop);
    [LibraryImport(Library, EntryPoint = "pa_context_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ContextNew(nint api, string name);
    [LibraryImport(Library, EntryPoint = "pa_context_connect")]
    internal static partial int ContextConnect(nint context, nint server, int flags, nint spawn);
    [LibraryImport(Library, EntryPoint = "pa_context_get_state")]
    internal static partial int ContextState(nint context);
    [LibraryImport(Library, EntryPoint = "pa_context_errno")]
    internal static partial int ContextError(nint context);
    [LibraryImport(Library, EntryPoint = "pa_context_disconnect")]
    internal static partial void ContextDisconnect(nint context);
    [LibraryImport(Library, EntryPoint = "pa_context_unref")]
    internal static partial void ContextUnref(nint context);
    [LibraryImport(Library, EntryPoint = "pa_strerror")]
    internal static partial nint StrError(int error);
    internal static string Error(int error) => Marshal.PtrToStringUTF8(StrError(error)) ?? $"Pulse error {error}";
    [LibraryImport(Library, EntryPoint = "pa_stream_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint StreamNew(nint context, string name, SampleSpec* spec, nint map);
    [LibraryImport(Library, EntryPoint = "pa_stream_set_read_callback")]
    internal static partial void SetRead(nint stream, delegate* unmanaged[Cdecl]<nint, nuint, nint, void> callback, nint user);
    [LibraryImport(Library, EntryPoint = "pa_stream_set_overflow_callback")]
    internal static partial void SetOverflow(nint stream, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint user);
    [LibraryImport(Library, EntryPoint = "pa_stream_connect_record", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ConnectRecord(nint stream, string device, BufferAttr* attr, int flags);
    [LibraryImport(Library, EntryPoint = "pa_stream_get_state")]
    internal static partial int StreamState(nint stream);
    [LibraryImport(Library, EntryPoint = "pa_stream_peek")]
    internal static partial int Peek(nint stream, void** data, nuint* bytes);
    [LibraryImport(Library, EntryPoint = "pa_stream_drop")]
    internal static partial int Drop(nint stream);
    [LibraryImport(Library, EntryPoint = "pa_stream_get_time")]
    internal static partial int GetTime(nint stream, ulong* microseconds);
    [LibraryImport(Library, EntryPoint = "pa_stream_get_latency")]
    internal static partial int GetLatency(nint stream, ulong* microseconds, int* negative);
    [LibraryImport(Library, EntryPoint = "pa_stream_get_device_name")]
    internal static partial nint DeviceName(nint stream);
    [LibraryImport(Library, EntryPoint = "pa_stream_disconnect")]
    internal static partial int Disconnect(nint stream);
    [LibraryImport(Library, EntryPoint = "pa_stream_unref")]
    internal static partial void StreamUnref(nint stream);
}
