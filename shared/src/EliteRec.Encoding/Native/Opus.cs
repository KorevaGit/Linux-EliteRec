using System.Runtime.InteropServices;

namespace EliteRec.Encoding.Native;

// Сигнатуры: xiph/opus include/opus_multistream.h, opus_defines.h.
// *_ctl — variadic; на x86-64 SysV целые и указатели передаются как обычные аргументы.
internal static unsafe partial class Opus
{
    private const string Library = "libopus.so.0";
    internal const int ApplicationVoip = 2048;
    internal const int SetBitrateRequest = 4002;
    internal const int GetLookaheadRequest = 4027;

    [LibraryImport(Library, EntryPoint = "opus_multistream_encoder_create")]
    internal static partial nint EncoderCreate(int rate, int channels, int streams, int coupled, byte* mapping, int application, int* error);
    [LibraryImport(Library, EntryPoint = "opus_multistream_encoder_ctl")]
    internal static partial int EncoderCtl(nint encoder, int request, int value);
    [LibraryImport(Library, EntryPoint = "opus_multistream_encoder_ctl")]
    internal static partial int EncoderCtl(nint encoder, int request, int* value);
    [LibraryImport(Library, EntryPoint = "opus_multistream_encode_float")]
    internal static partial int EncodeFloat(nint encoder, float* pcm, int frames, byte* data, int capacity);
    [LibraryImport(Library, EntryPoint = "opus_multistream_encoder_destroy")]
    internal static partial void EncoderDestroy(nint encoder);

    // Декодер нужен только тестам: проверить, что каналы стерео не смешиваются.
    [LibraryImport(Library, EntryPoint = "opus_multistream_decoder_create")]
    internal static partial nint DecoderCreate(int rate, int channels, int streams, int coupled, byte* mapping, int* error);
    [LibraryImport(Library, EntryPoint = "opus_multistream_decode_float")]
    internal static partial int DecodeFloat(nint decoder, byte* data, int length, float* pcm, int frames, int fec);
    [LibraryImport(Library, EntryPoint = "opus_multistream_decoder_destroy")]
    internal static partial void DecoderDestroy(nint decoder);
}
