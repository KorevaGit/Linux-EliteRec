// Адаптация Linux EliteSIP, каталог и наблюдение устройств для EliteRec R1.
using System.Runtime.InteropServices;

namespace EliteRec.Audio.Pulse.Native;

/// <summary>
/// Объявления libpulse — ровно те, что нужны тракту.
/// </summary>
///
/// <remarks>
/// <para>
/// Сверены 19 октября 2026 с заголовками <c>libpulse-dev</c> 16.1 (Ubuntu
/// noble, его ставит Mint 22): структуры, числа перечислений и флаги — оттуда,
/// а не по памяти. Ошибка в раскладке структуры здесь не падает, а тихо даёт
/// неверный буфер или неверную задержку, и искать её потом по звуку.
/// </para>
/// <para>
/// Готовой поддерживаемой обёртки под .NET нет, поэтому свой P/Invoke — та же
/// роль, что у NAudio на Windows. Разделяемая библиотека — <c>libpulse.so.0</c>:
/// она есть и на PulseAudio-системах, и на PipeWire (пакет <c>libpulse0</c>
/// стоит рядом с <c>pipewire-pulse</c>).
/// </para>
/// </remarks>
internal static unsafe partial class LibPulse
{
    private const string Library = "libpulse.so.0";

    // pa_sample_format_t (sample.h): U8, ALAW, ULAW, S16LE, S16BE, FLOAT32LE...
    internal const int SampleS16Le = 3;
    internal const int SampleFloat32Le = 5;

    // pa_context_state_t (def.h).
    internal const int ContextReady = 4;
    internal const int ContextFailed = 5;
    internal const int ContextTerminated = 6;

    // pa_stream_state_t (def.h).
    internal const int StreamReady = 2;
    internal const int StreamFailed = 3;
    internal const int StreamTerminated = 4;

    // pa_context_flags_t.
    internal const int ContextNoAutospawn = 0x1;

    // pa_stream_flags_t.
    internal const int StreamStartCorked = 0x1;
    internal const int StreamInterpolateTiming = 0x2;
    internal const int StreamAutoTimingUpdate = 0x8;
    internal const int StreamAdjustLatency = 0x2000;
    internal const int StreamDontMove = 0x200;

    internal const int SeekRelative = 0;

    // pa_timing_info на 64-битном Linux; timeval содержит два native long.
    [StructLayout(LayoutKind.Sequential)]
    internal struct TimingInfo
    {
        public long Seconds, Microseconds;
        public int SynchronizedClocks;
        public ulong SinkUsec, SourceUsec, TransportUsec;
        public int Playing, WriteIndexCorrupt;
        public long WriteIndex;
        public int ReadIndexCorrupt;
        public long ReadIndex;
        public ulong ConfiguredSinkUsec, ConfiguredSourceUsec;
        public long SinceUnderrun;
    }

    [LibraryImport(Library, EntryPoint = "pa_stream_new_with_proplist", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint StreamNewWithProplist(nint context, string name, SampleSpec* spec, nint map, nint properties);
    [LibraryImport(Library, EntryPoint = "pa_stream_set_read_callback")]
    internal static partial void StreamSetReadCallback(nint stream, delegate* unmanaged[Cdecl]<nint, nuint, nint, void> callback, nint user);
    [LibraryImport(Library, EntryPoint = "pa_stream_set_overflow_callback")]
    internal static partial void StreamSetOverflowCallback(nint stream, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint user);
    [LibraryImport(Library, EntryPoint = "pa_stream_connect_record", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int StreamConnectRecord(nint stream, string device, BufferAttr* attributes, int flags);
    [LibraryImport(Library, EntryPoint = "pa_stream_get_state")]
    internal static partial int StreamGetState(nint stream);
    [LibraryImport(Library, EntryPoint = "pa_stream_peek")]
    internal static partial int StreamPeek(nint stream, void** data, nuint* bytes);
    [LibraryImport(Library, EntryPoint = "pa_stream_drop")]
    internal static partial int StreamDrop(nint stream);
    [LibraryImport(Library, EntryPoint = "pa_stream_get_timing_info")]
    internal static partial TimingInfo* StreamGetTimingInfo(nint stream);
    [LibraryImport(Library, EntryPoint = "pa_stream_update_timing_info")]
    internal static partial nint StreamUpdateTiming(nint stream, nint callback, nint user);
    [LibraryImport(Library, EntryPoint = "pa_stream_disconnect")]
    internal static partial int StreamDisconnect(nint stream);
    [LibraryImport(Library, EntryPoint = "pa_stream_unref")]
    internal static partial void StreamUnref(nint stream);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    /// <summary>
    /// <c>pa_buffer_attr</c>. <see cref="uint.MaxValue"/> в поле — «на усмотрение
    /// сервера» (<c>(uint32_t) -1</c> в заголовке).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    // --- threaded mainloop -------------------------------------------------

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_new")]
    internal static partial nint ThreadedMainloopNew();

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_free")]
    internal static partial void ThreadedMainloopFree(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_start")]
    internal static partial int ThreadedMainloopStart(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_stop")]
    internal static partial void ThreadedMainloopStop(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_lock")]
    internal static partial void ThreadedMainloopLock(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_unlock")]
    internal static partial void ThreadedMainloopUnlock(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_wait")]
    internal static partial void ThreadedMainloopWait(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_signal")]
    internal static partial void ThreadedMainloopSignal(nint loop, int waitForAccept);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_get_api")]
    internal static partial nint ThreadedMainloopGetApi(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_set_name", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void ThreadedMainloopSetName(nint loop, string name);

    // --- proplist -----------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "pa_proplist_new")]
    internal static partial nint ProplistNew();

    [LibraryImport(Library, EntryPoint = "pa_proplist_free")]
    internal static partial void ProplistFree(nint list);

    [LibraryImport(Library, EntryPoint = "pa_proplist_sets", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ProplistSets(nint list, string key, string value);

    // --- context ------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "pa_context_new_with_proplist", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ContextNewWithProplist(nint api, string name, nint proplist);

    [LibraryImport(Library, EntryPoint = "pa_context_set_state_callback")]
    internal static partial void ContextSetStateCallback(nint context, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_context_connect")]
    internal static partial int ContextConnect(nint context, nint server, int flags, nint spawnApi);

    [LibraryImport(Library, EntryPoint = "pa_context_get_state")]
    internal static partial int ContextGetState(nint context);

    [LibraryImport(Library, EntryPoint = "pa_context_errno")]
    internal static partial int ContextErrno(nint context);

    [LibraryImport(Library, EntryPoint = "pa_context_disconnect")]
    internal static partial void ContextDisconnect(nint context);

    [LibraryImport(Library, EntryPoint = "pa_context_unref")]
    internal static partial void ContextUnref(nint context);

    [LibraryImport(Library, EntryPoint = "pa_strerror")]
    internal static partial nint StrErrorRaw(int error);

    internal static string StrError(int error) => Marshal.PtrToStringUTF8(StrErrorRaw(error)) ?? $"ошибка {error}";

    // --- раскладка каналов ----------------------------------------------------

    /// <summary>Вместимость <c>pa_channel_map</c> и <c>pa_cvolume</c> (<c>PA_CHANNELS_MAX</c>).</summary>
    internal const int ChannelsMax = 32;

    // pa_channel_position_t (channelmap.h).
    internal const int PositionMono = 0;
    internal const int PositionFrontLeft = 1;
    internal const int PositionFrontRight = 2;

    /// <summary><c>pa_channel_map</c>: 132 байта, сверено <c>sizeof</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ChannelMap
    {
        public byte Channels;
        public fixed int Map[ChannelsMax];
    }

    /// <summary><c>pa_cvolume</c>: 132 байта, сверено <c>sizeof</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CVolume
    {
        public byte Channels;
        public fixed uint Values[ChannelsMax];
    }

    /// <summary>Громкость 100 % без усиления (<c>PA_VOLUME_NORM</c>).</summary>
    internal const uint VolumeNorm = 0x10000;

    // --- опрос сервера --------------------------------------------------------

    /// <summary>
    /// Общее начало <c>pa_source_info</c> и <c>pa_sink_info</c>.
    /// </summary>
    ///
    /// <remarks>
    /// Смещения сняты <c>offsetof</c> с заголовков 16.1 на x86_64, а не
    /// посчитаны руками: у двух структур они совпадают до <c>active_port</c>
    /// включительно, поэтому одна раскладка служит обеим. Поле 308 у входа —
    /// <c>monitor_of_sink</c>, у выхода — <c>monitor_source</c>; читается только
    /// у входа. Остальное в структуре (форматы, число портов) тракту не нужно,
    /// и объявлять его незачем: раскладку с явными смещениями хвост не трогает.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    internal struct DeviceInfo
    {
        [FieldOffset(0)] public nint Name;
        [FieldOffset(8)] public uint Index;
        [FieldOffset(16)] public nint Description;
        [FieldOffset(24)] public SampleSpec SampleSpec;
        [FieldOffset(36)] public ChannelMap ChannelMap;
        [FieldOffset(172)] public CVolume Volume;
        [FieldOffset(304)] public int Mute;
        [FieldOffset(308)] public uint MonitorOfSink;
        [FieldOffset(312)] public nint MonitorName;
        [FieldOffset(336)] public int Flags;
        [FieldOffset(344)] public nint Proplist;
        [FieldOffset(360)] public uint BaseVolume;
        [FieldOffset(364)] public int State;
        [FieldOffset(392)] public PortInfo* ActivePort;
    }

    /// <summary>Начало <c>pa_source_port_info</c> / <c>pa_sink_port_info</c>.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct PortInfo
    {
        [FieldOffset(0)] public nint Name;
        [FieldOffset(8)] public nint Description;
        [FieldOffset(16)] public uint Priority;
        [FieldOffset(20)] public int Available;
    }

    // pa_port_available_t.
    internal const int PortAvailableNo = 1;

    /// <summary><c>PA_INVALID_INDEX</c>: у входа — «не монитор».</summary>
    internal const uint InvalidIndex = uint.MaxValue;

    /// <summary>Начало <c>pa_server_info</c>: нужны только имена устройств по умолчанию.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct ServerInfo
    {
        [FieldOffset(48)] public nint DefaultSinkName;
        [FieldOffset(56)] public nint DefaultSourceName;
    }

    // pa_operation_state_t.
    internal const int OperationRunning = 0;

    [LibraryImport(Library, EntryPoint = "pa_operation_get_state")]
    internal static partial int OperationGetState(nint operation);

    [LibraryImport(Library, EntryPoint = "pa_operation_cancel")]
    internal static partial void OperationCancel(nint operation);

    [LibraryImport(Library, EntryPoint = "pa_operation_unref")]
    internal static partial void OperationUnref(nint operation);

    [LibraryImport(Library, EntryPoint = "pa_context_get_server_info")]
    internal static partial nint ContextGetServerInfo(nint context, delegate* unmanaged[Cdecl]<nint, ServerInfo*, nint, void> callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_context_get_source_info_list")]
    internal static partial nint ContextGetSourceInfoList(nint context, delegate* unmanaged[Cdecl]<nint, DeviceInfo*, int, nint, void> callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_context_get_sink_info_list")]
    internal static partial nint ContextGetSinkInfoList(nint context, delegate* unmanaged[Cdecl]<nint, DeviceInfo*, int, nint, void> callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_proplist_gets", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ProplistGetsRaw(nint list, string key);

    [LibraryImport(Library, EntryPoint = "pa_proplist_iterate")]
    internal static partial nint ProplistIterate(nint list, nint* state);

    // --- подписка -------------------------------------------------------------

    // pa_subscription_mask_t.
    internal const int SubscriptionMaskSink = 0x1;
    internal const int SubscriptionMaskSource = 0x2;
    internal const int SubscriptionMaskServer = 0x80;
    internal const int SubscriptionMaskCard = 0x200;

    // pa_subscription_event_type_t.
    internal const int EventFacilityMask = 0x0F;
    internal const int EventSink = 0x0;
    internal const int EventSource = 0x1;
    internal const int EventServer = 0x7;
    internal const int EventCard = 0x9;
    internal const int EventTypeMask = 0x30;
    internal const int EventNew = 0x00;
    internal const int EventChange = 0x10;
    internal const int EventRemove = 0x20;

    [LibraryImport(Library, EntryPoint = "pa_context_subscribe")]
    internal static partial nint ContextSubscribe(nint context, int mask, nint callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_context_set_subscribe_callback")]
    internal static partial void ContextSetSubscribeCallback(nint context, delegate* unmanaged[Cdecl]<nint, int, uint, nint, void> callback, nint userdata);

    internal static string? Utf8(nint text) => text == 0 ? null : Marshal.PtrToStringUTF8(text);
}
