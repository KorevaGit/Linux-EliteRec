using System.Runtime.InteropServices;

namespace EliteRec.Probe.Native;

// Сигнатуры: lameproject/lame include/lame.h (ссылки в docs/R0-RISKS.md).
internal static unsafe partial class Lame
{
    private const string Library = "libmp3lame.so.0";
    [LibraryImport(Library, EntryPoint = "lame_init")]
    internal static partial nint Init();
    [LibraryImport(Library, EntryPoint = "lame_close")]
    internal static partial int Close(nint encoder);
    [LibraryImport(Library, EntryPoint = "lame_set_in_samplerate")]
    internal static partial int SetInputRate(nint encoder, int value);
    [LibraryImport(Library, EntryPoint = "lame_set_out_samplerate")]
    internal static partial int SetOutputRate(nint encoder, int value);
    [LibraryImport(Library, EntryPoint = "lame_set_num_channels")]
    internal static partial int SetChannels(nint encoder, int value);
    [LibraryImport(Library, EntryPoint = "lame_set_mode")]
    internal static partial int SetMode(nint encoder, int value);
    [LibraryImport(Library, EntryPoint = "lame_set_brate")]
    internal static partial int SetBitrate(nint encoder, int value);
    [LibraryImport(Library, EntryPoint = "lame_set_quality")]
    internal static partial int SetQuality(nint encoder, int value);
    [LibraryImport(Library, EntryPoint = "lame_set_VBR")]
    internal static partial int SetVbr(nint encoder, int value);
    [LibraryImport(Library, EntryPoint = "lame_set_bWriteVbrTag")]
    internal static partial int SetVbrTag(nint encoder, int value);
    [LibraryImport(Library, EntryPoint = "lame_init_params")]
    internal static partial int InitParams(nint encoder);
    [LibraryImport(Library, EntryPoint = "lame_encode_buffer_ieee_float")]
    internal static partial int Encode(nint encoder, float* left, float* right, int frames, byte* output, int capacity);
    [LibraryImport(Library, EntryPoint = "lame_encode_flush")]
    internal static partial int Flush(nint encoder, byte* output, int capacity);
}
