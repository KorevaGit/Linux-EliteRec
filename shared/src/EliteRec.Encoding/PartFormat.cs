using System.Buffers.Binary;

namespace EliteRec.Encoding;

public enum RecordingFormat { Mp3, Flac, Opus, Wav }

/// <summary>Кодер одной части: пишет в открытый файл части, <see cref="Finish"/> дописывает хвост/заголовок.</summary>
internal interface IPartEncoder : IDisposable
{
    void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right);
    void Finish();
    /// <summary>Перед сбросом на диск: выписать то, что кодер держит в памяти (Ogg-страницу).</summary>
    void Checkpoint() { }
}

/// <summary>Что о формате нужно записи и восстановлению: расширение, кодер, длина целых данных в обрезанной части.</summary>
internal static class PartFormat
{
    internal const string PartSuffix = ".part";

    internal static string Extension(RecordingFormat format) => format switch
    {
        RecordingFormat.Mp3 => ".mp3",
        RecordingFormat.Flac => ".flac",
        RecordingFormat.Opus => ".opus",
        RecordingFormat.Wav => ".wav",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    internal static RecordingFormat? FromExtension(string extension) => extension switch
    {
        ".mp3" => RecordingFormat.Mp3,
        ".flac" => RecordingFormat.Flac,
        ".opus" => RecordingFormat.Opus,
        ".wav" => RecordingFormat.Wav,
        _ => null,
    };

    internal static IPartEncoder Create(RecordingFormat format, bool stereo, FileStream file) => format switch
    {
        RecordingFormat.Mp3 => new Mp3Encoder(stereo, file),
        RecordingFormat.Flac => new FlacEncoder(stereo, file),
        RecordingFormat.Opus => new OpusEncoder(stereo, file),
        RecordingFormat.Wav => new WavEncoder(stereo, file),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Сколько байт от начала части можно оставить и какой длительности это звук.</summary>
    internal static (long Bytes, TimeSpan Duration) Inspect(RecordingFormat format, string path) => format switch
    {
        RecordingFormat.Mp3 => Mp3Frames.Scan(path),
        RecordingFormat.Flac => FlacFrames.Scan(path),
        RecordingFormat.Opus => OggPages.Scan(path),
        RecordingFormat.Wav => WavEncoder.Scan(path),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Обрезает часть до целых данных и чинит то, что пишется только при нормальном завершении.</summary>
    internal static void Repair(RecordingFormat format, FileStream file, long bytes)
    {
        file.SetLength(bytes);
        if (format == RecordingFormat.Wav) WavEncoder.WriteSizes(file);
        if (format == RecordingFormat.Opus) OggPages.MarkEnd(file);
    }

    internal static void Clean(ReadOnlySpan<float> source, Span<float> target)
    {
        for (int i = 0; i < source.Length; i++)
            target[i] = float.IsFinite(source[i]) ? Math.Clamp(source[i], -1f, 1f) : 0f;
    }

    internal static short Pcm16(float sample) => !float.IsFinite(sample) ? (short)0
        : (short)Math.Clamp((int)Math.Round(Math.Clamp(sample, -1f, 1f) * 32768.0), short.MinValue, short.MaxValue);
}

/// <summary>Кадры MPEG-1 Layer III 48 кГц от начала файла (CBR без тегов).</summary>
internal static class Mp3Frames
{
    private static readonly int[] Rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];

    internal static (long Bytes, TimeSpan Duration) Scan(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[4];
        long bytes = 0, frames = 0, length = input.Length;
        while (bytes + 4 <= length)
        {
            input.Position = bytes;
            input.ReadExactly(header);
            uint h = BinaryPrimitives.ReadUInt32BigEndian(header);
            int bitrate = Rates[(h >> 12) & 15];
            if ((h & 0xfffe0000) != 0xfffa0000 || ((h >> 10) & 3) != 1 || bitrate == 0) break;
            long size = 144000 * bitrate / 48000 + ((h >> 9) & 1);
            if (bytes + size > length) break;
            bytes += size; frames++;
        }
        return (bytes, TimeSpan.FromSeconds(frames * 1152 / 48000.0));
    }
}
