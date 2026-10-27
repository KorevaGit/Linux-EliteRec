using System.Runtime.InteropServices;

namespace EliteRec.Encoding.Native;

// Сигнатуры: xiph/flac include/FLAC/stream_encoder.h.
internal static unsafe partial class Flac
{
    private const string Library = "libFLAC";
    // Soname различается: Debian 13 — libFLAC.so.14, Ubuntu 24.04 — libFLAC.so.12.
    private static readonly string[] Candidates = ["libFLAC.so.14", "libFLAC.so.12", "libFLAC.so.8", "libFLAC.so"];

    // Явный статический конструктор срабатывает до первого вызова libFLAC.
    static Flac() =>
        NativeLibrary.SetDllImportResolver(typeof(Flac).Assembly, (name, assembly, path) =>
        {
            if (name != Library) return 0;
            foreach (string candidate in Candidates)
                if (NativeLibrary.TryLoad(candidate, assembly, path, out nint handle)) return handle;
            return 0;
        });

    [LibraryImport(Library, EntryPoint = "FLAC__stream_encoder_new")]
    internal static partial nint New();
    [LibraryImport(Library, EntryPoint = "FLAC__stream_encoder_delete")]
    internal static partial void Delete(nint encoder);
    [LibraryImport(Library, EntryPoint = "FLAC__stream_encoder_set_channels")]
    internal static partial int SetChannels(nint encoder, uint value);
    [LibraryImport(Library, EntryPoint = "FLAC__stream_encoder_set_bits_per_sample")]
    internal static partial int SetBitsPerSample(nint encoder, uint value);
    [LibraryImport(Library, EntryPoint = "FLAC__stream_encoder_set_sample_rate")]
    internal static partial int SetSampleRate(nint encoder, uint value);
    [LibraryImport(Library, EntryPoint = "FLAC__stream_encoder_set_compression_level")]
    internal static partial int SetCompressionLevel(nint encoder, uint value);
    [LibraryImport(Library, EntryPoint = "FLAC__stream_encoder_init_stream")]
    internal static partial int InitStream(nint encoder,
        delegate* unmanaged[Cdecl]<nint, byte*, nuint, uint, uint, nint, int> write,
        delegate* unmanaged[Cdecl]<nint, ulong, nint, int> seek,
        delegate* unmanaged[Cdecl]<nint, ulong*, nint, int> tell,
        nint metadata, nint clientData);
    [LibraryImport(Library, EntryPoint = "FLAC__stream_encoder_process_interleaved")]
    internal static partial int ProcessInterleaved(nint encoder, int* buffer, uint samples);
    [LibraryImport(Library, EntryPoint = "FLAC__stream_encoder_finish")]
    internal static partial int Finish(nint encoder);
}
