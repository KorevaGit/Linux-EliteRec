using System.Buffers.Binary;
using EliteRec.Core.Recording;

namespace EliteRec.Encoding;

/// <summary>PCM16 WAV 48 кГц. Размеры в заголовке пишутся при завершении; после сбоя их восстанавливает <see cref="WriteSizes"/>.</summary>
internal sealed class WavEncoder : IPartEncoder
{
    private const int HeaderBytes = 44;
    private readonly FileStream _file;
    private readonly int _channels;
    private readonly byte[] _buffer = new byte[SynchronizedAudioMixer.BlockFrames * 4];
    private long _frames;

    internal WavEncoder(bool stereo, FileStream file)
    {
        _file = file; _channels = stereo ? 2 : 1;
        Header(_file, _channels, 0);
    }

    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length > SynchronizedAudioMixer.BlockFrames) throw new ArgumentException("Слишком большой WAV-блок.");
        if ((_frames + left.Length) * _channels * 2 > uint.MaxValue - 36) throw new IOException("Достигнут предел RIFF WAV (4 ГБ). Завершите запись.");
        int pos = 0;
        for (int i = 0; i < left.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(pos), PartFormat.Pcm16(left[i])); pos += 2;
            if (_channels == 2) { BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(pos), PartFormat.Pcm16(right[i])); pos += 2; }
        }
        _file.Write(_buffer, 0, pos);
        _frames += left.Length;
    }

    public void Finish() => WriteSizes(_file);

    public void Dispose() { }

    /// <summary>Записывает размеры RIFF/data по текущей длине файла и возвращает позицию в конец.</summary>
    internal static void WriteSizes(FileStream file)
    {
        long end = file.Length;
        file.Position = 22;
        Span<byte> channels = stackalloc byte[2];
        file.ReadExactly(channels);
        Header(file, BinaryPrimitives.ReadInt16LittleEndian(channels), checked((uint)(end - HeaderBytes)));
        file.Position = end;
    }

    internal static (long Bytes, TimeSpan Duration) Scan(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (input.Length < HeaderBytes) return (0, TimeSpan.Zero);
        Span<byte> header = stackalloc byte[HeaderBytes];
        input.ReadExactly(header);
        int align = BinaryPrimitives.ReadInt16LittleEndian(header[32..]);
        if (!header[..4].SequenceEqual("RIFF"u8) || align is not (2 or 4)) return (0, TimeSpan.Zero);
        long frames = (input.Length - HeaderBytes) / align;
        return (HeaderBytes + frames * align, TimeSpan.FromSeconds(frames / 48000.0));
    }

    private static void Header(Stream file, int channels, uint dataBytes)
    {
        file.Position = 0;
        using var writer = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + dataBytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
        writer.Write(48000); writer.Write(48000 * channels * 2);
        writer.Write((short)(channels * 2)); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(dataBytes);
    }
}
