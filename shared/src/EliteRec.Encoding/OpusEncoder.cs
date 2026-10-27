using System.Buffers.Binary;
using EliteRec.Encoding.Native;

namespace EliteRec.Encoding;

/// <summary>
/// Ogg Opus 48 кГц, кадр 20 мс. Микс — один моно-поток 32 кбит/с; стерео — два НЕЗАВИСИМЫХ
/// моно-потока по 32 кбит/с (семейство раскладки 1, 2 потока, 0 связанных), чтобы кодер не
/// смешивал менеджера и клиента, как joint stereo в MP3. Ogg-страницы пишутся сами (RFC 3533, RFC 7845).
/// </summary>
internal sealed unsafe class OpusEncoder : IPartEncoder
{
    internal const int FrameSamples = 960;
    private const int PacketsPerPage = 50; // ~1 с на страницу
    private nint _encoder;
    private readonly OggWriter _ogg;
    private readonly int _channels;
    private readonly int _preSkip;
    private readonly float[] _pcm;
    private readonly float[] _left = new float[FrameSamples], _right = new float[FrameSamples];
    private readonly byte[] _packet = new byte[4000];
    private int _filled;
    private long _samples;

    internal OpusEncoder(bool stereo, Stream output)
    {
        _channels = stereo ? 2 : 1;
        _pcm = new float[FrameSamples * _channels];
        int error;
        byte* mapping = stackalloc byte[] { 0, 1 };
        _encoder = Opus.EncoderCreate(48000, _channels, _channels, 0, mapping, Opus.ApplicationVoip, &error);
        if (_encoder == 0) throw new IOException($"libopus: encoder_create {error}.");
        try
        {
            Check(Opus.EncoderCtl(_encoder, Opus.SetBitrateRequest, 32000 * _channels), "bitrate");
            int lookahead;
            Check(Opus.EncoderCtl(_encoder, Opus.GetLookaheadRequest, &lookahead), "lookahead");
            _preSkip = lookahead;
            _ogg = new OggWriter(output);
            _ogg.WriteHeaders(Head(_channels, _preSkip), Tags());
        }
        catch { Dispose(); throw; }
    }

    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        ObjectDisposedException.ThrowIf(_encoder == 0, this);
        if (_channels == 2 && right.Length != left.Length) throw new ArgumentException("Неверный Opus-блок.");
        while (!left.IsEmpty)
        {
            int take = Math.Min(left.Length, FrameSamples - _filled);
            PartFormat.Clean(left[..take], _left.AsSpan(_filled, take));
            if (_channels == 2) PartFormat.Clean(right[..take], _right.AsSpan(_filled, take));
            _filled += take; _samples += take;
            left = left[take..];
            if (_channels == 2) right = right[take..];
            if (_filled == FrameSamples) Encode();
        }
    }

    public void Checkpoint() => _ogg.FlushPage(endOfStream: false);

    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_encoder == 0, this);
        // Добиваем неполный кадр и прогоняем ещё один тихий, чтобы выдать задержку кодера.
        long total = _samples;
        do
        {
            Array.Clear(_left, _filled, FrameSamples - _filled);
            Array.Clear(_right, _filled, FrameSamples - _filled);
            _filled = FrameSamples;
            Encode();
        } while (_ogg.Granule < _preSkip + total);
        _ogg.FlushPage(endOfStream: true, finalGranule: _preSkip + total);
    }

    private void Encode()
    {
        for (int i = 0; i < FrameSamples; i++)
        {
            _pcm[i * _channels] = _left[i];
            if (_channels == 2) _pcm[i * 2 + 1] = _right[i];
        }
        int bytes;
        fixed (float* pcm = _pcm)
        fixed (byte* data = _packet)
            bytes = Opus.EncodeFloat(_encoder, pcm, FrameSamples, data, _packet.Length);
        Check(bytes, "encode");
        _filled = 0;
        _ogg.AddPacket(_packet.AsSpan(0, bytes), FrameSamples);
        if (_ogg.PendingPackets >= PacketsPerPage) _ogg.FlushPage(endOfStream: false);
    }

    private static byte[] Head(int channels, int preSkip)
    {
        // RFC 7845 §5.1. Стерео: семейство 1, 2 потока, 0 связанных, раскладка L=0, R=1.
        byte[] head = new byte[channels == 2 ? 23 : 19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1; head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48000);
        if (channels == 2) { head[18] = 1; head[19] = 2; head[20] = 0; head[21] = 0; head[22] = 1; }
        return head;
    }

    private static byte[] Tags()
    {
        byte[] vendor = "EliteRec"u8.ToArray();
        byte[] tags = new byte[8 + 4 + vendor.Length + 4];
        "OpusTags"u8.CopyTo(tags);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(8), (uint)vendor.Length);
        vendor.CopyTo(tags, 12);
        return tags;
    }

    private static void Check(int result, string step)
    {
        if (result < 0) throw new IOException($"libopus: ошибка {result} ({step}).");
    }

    public void Dispose()
    {
        if (_encoder == 0) return;
        Opus.EncoderDestroy(_encoder);
        _encoder = 0;
    }
}

/// <summary>Минимальная запись страниц Ogg одного логического потока.</summary>
internal sealed class OggWriter(Stream output)
{
    private readonly uint _serial = (uint)Random.Shared.Next();
    private readonly List<byte[]> _packets = [];
    private uint _sequence;
    internal long Granule { get; private set; }
    internal int PendingPackets => _packets.Count;

    internal void WriteHeaders(byte[] head, byte[] tags)
    {
        Page([head], 0, 0x02);
        Page([tags], 0, 0);
    }

    internal void AddPacket(ReadOnlySpan<byte> packet, int samples)
    {
        _packets.Add(packet.ToArray());
        Granule += samples;
    }

    internal void FlushPage(bool endOfStream, long? finalGranule = null)
    {
        if (_packets.Count == 0 && !endOfStream) return;
        Page(_packets, finalGranule ?? Granule, endOfStream ? (byte)0x04 : (byte)0);
        _packets.Clear();
    }

    private void Page(List<byte[]> packets, long granule, byte flags)
    {
        var lacing = new List<byte>();
        foreach (byte[] packet in packets)
        {
            for (int left = packet.Length; ; left -= 255)
            {
                lacing.Add((byte)Math.Min(left, 255));
                if (left < 255) break;
            }
        }
        if (lacing.Count > 255) throw new InvalidOperationException("Слишком много сегментов на странице Ogg.");
        int body = packets.Sum(p => p.Length);
        byte[] page = new byte[27 + lacing.Count + body];
        "OggS"u8.CopyTo(page);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), _serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), _sequence++);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page, 27);
        int pos = 27 + lacing.Count;
        foreach (byte[] packet in packets) { packet.CopyTo(page, pos); pos += packet.Length; }
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), OggPages.Crc(page));
        output.Write(page);
    }
}

/// <summary>Чтение страниц Ogg: проверка CRC, поиск последней целой страницы после сбоя.</summary>
internal static class OggPages
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint r = (uint)i << 24;
        for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? r << 1 ^ 0x04c11db7 : r << 1;
        return r;
    }).ToArray();

    internal static uint Crc(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        for (int i = 0; i < page.Length; i++)
            crc = crc << 8 ^ Table[(crc >> 24) ^ (i is >= 22 and < 26 ? 0 : page[i])];
        return crc;
    }

    internal readonly record struct Page(long Start, int Length, long Granule, byte Flags);

    /// <summary>Целые страницы от начала файла; останавливается на первой битой или неполной.</summary>
    internal static List<Page> Read(byte[] data)
    {
        var pages = new List<Page>();
        for (long pos = 0; pos + 27 <= data.Length;)
        {
            var span = data.AsSpan((int)pos);
            if (!span[..4].SequenceEqual("OggS"u8) || span[4] != 0) break;
            int segments = span[26];
            if (27 + segments > span.Length) break;
            int length = 27 + segments;
            for (int i = 0; i < segments; i++) length += span[27 + i];
            if (length > span.Length || Crc(span[..length]) != BinaryPrimitives.ReadUInt32LittleEndian(span[22..])) break;
            pages.Add(new(pos, length, BinaryPrimitives.ReadInt64LittleEndian(span[6..]), span[5]));
            pos += length;
        }
        return pages;
    }

    internal static (long Bytes, TimeSpan Duration) Scan(string path)
    {
        byte[] data;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            data = new byte[input.Length];
            input.ReadExactly(data);
        }
        var pages = Read(data);
        if (pages.Count < 3) return (0, TimeSpan.Zero); // OpusHead, OpusTags и хотя бы одна страница звука
        int preSkip = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(27 + 1 + 10)); // OpusHead после 1 сегмента
        var last = pages[^1];
        return (last.Start + last.Length, TimeSpan.FromSeconds(Math.Max(0, last.Granule - preSkip) / 48000.0));
    }

    /// <summary>После обрезки помечает последнюю страницу концом потока (EOS) и пересчитывает её CRC.</summary>
    internal static void MarkEnd(FileStream file)
    {
        byte[] data = new byte[file.Length];
        file.Position = 0;
        file.ReadExactly(data);
        var last = Read(data)[^1];
        byte[] page = data.AsSpan((int)last.Start, last.Length).ToArray();
        page[5] |= 0x04;
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page));
        file.Position = last.Start;
        file.Write(page);
        file.Position = file.Length;
    }
}
