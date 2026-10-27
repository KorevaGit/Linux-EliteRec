using System.Runtime.InteropServices;
using EliteRec.Core.Recording;
using EliteRec.Encoding.Native;

namespace EliteRec.Encoding;

/// <summary>
/// FLAC 16 бит 48 кГц через libFLAC с записью в наш файл (колбэки). STREAMINFO (длина, MD5)
/// дописывается при завершении; после сбоя он остаётся «неизвестно» — это допустимо по спецификации.
/// </summary>
internal sealed unsafe class FlacEncoder : IPartEncoder
{
    private nint _encoder;
    private GCHandle _self;
    private readonly FileStream _file;
    private readonly int _channels;
    private readonly int[] _buffer = new int[SynchronizedAudioMixer.BlockFrames * 2];
    private Exception? _ioError;

    internal FlacEncoder(bool stereo, FileStream file)
    {
        _file = file; _channels = stereo ? 2 : 1;
        _encoder = Flac.New();
        if (_encoder == 0) throw new InvalidOperationException("libFLAC: не удалось создать кодер.");
        _self = GCHandle.Alloc(this);
        try
        {
            Check(Flac.SetChannels(_encoder, (uint)_channels), "channels");
            Check(Flac.SetBitsPerSample(_encoder, 16), "bits");
            Check(Flac.SetSampleRate(_encoder, SynchronizedAudioMixer.SampleRate), "rate");
            Check(Flac.SetCompressionLevel(_encoder, 5), "level");
            int status = Flac.InitStream(_encoder, &OnWrite, &OnSeek, &OnTell, 0, GCHandle.ToIntPtr(_self));
            if (status != 0) throw new IOException($"libFLAC: init_stream {status}.");
        }
        catch { Dispose(); throw; }
    }

    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        ObjectDisposedException.ThrowIf(_encoder == 0, this);
        if (left.Length > SynchronizedAudioMixer.BlockFrames || (_channels == 2 && right.Length != left.Length)) throw new ArgumentException("Неверный FLAC-блок.");
        for (int i = 0, pos = 0; i < left.Length; i++)
        {
            _buffer[pos++] = PartFormat.Pcm16(left[i]);
            if (_channels == 2) _buffer[pos++] = PartFormat.Pcm16(right[i]);
        }
        fixed (int* samples = _buffer) Check(Flac.ProcessInterleaved(_encoder, samples, (uint)left.Length), "process");
    }

    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_encoder == 0, this);
        Check(Flac.Finish(_encoder), "finish");
        _file.Seek(0, SeekOrigin.End);
    }

    private void Check(int ok, string step)
    {
        if (_ioError is { } error) { _ioError = null; throw new IOException("Запись FLAC: " + error.Message, error); }
        if (ok == 0) throw new IOException($"libFLAC: ошибка ({step}).");
    }

    public void Dispose()
    {
        if (_encoder != 0) { Flac.Delete(_encoder); _encoder = 0; }
        if (_self.IsAllocated) _self.Free();
    }

    private static FlacEncoder From(nint client) => (FlacEncoder)GCHandle.FromIntPtr(client).Target!;

    // Колбэки вызываются синхронно из process/finish в том же потоке; исключение не пересекает native-границу.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnWrite(nint encoder, byte* buffer, nuint bytes, uint samples, uint frame, nint client)
    {
        var self = From(client);
        try { self._file.Write(new ReadOnlySpan<byte>(buffer, checked((int)bytes))); return 0; }
        catch (Exception ex) { self._ioError = ex; return 1; } // FATAL_ERROR
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnSeek(nint encoder, ulong offset, nint client)
    {
        var self = From(client);
        try { self._file.Position = checked((long)offset); return 0; }
        catch (Exception ex) { self._ioError = ex; return 1; } // ERROR
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnTell(nint encoder, ulong* offset, nint client)
    {
        var self = From(client);
        try { *offset = (ulong)self._file.Position; return 0; }
        catch (Exception ex) { self._ioError = ex; return 1; }
    }
}

/// <summary>Поиск последнего целого кадра FLAC в части, оборванной сбоем.</summary>
internal static class FlacFrames
{
    internal static (long Bytes, TimeSpan Duration) Scan(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> start4 = stackalloc byte[10];
        if (input.Length < 10) return (0, TimeSpan.Zero);
        input.ReadExactly(start4);
        if (!start4[..4].SequenceEqual("fLaC"u8)) return (0, TimeSpan.Zero);
        // Кодер пишет кадры постоянного размера (STREAMINFO: минимальный блок); короче может быть только последний.
        int nominal = start4[8] << 8 | start4[9];
        // Кадр 4096 отсчётов 16 бит стерео < 17 КБ: хвоста 256 КБ хватает с запасом.
        long offset = Math.Max(0, input.Length - 256 * 1024);
        byte[] data = new byte[input.Length - offset];
        input.Position = offset;
        input.ReadExactly(data);
        // Ищем с конца последний настоящий заголовок кадра; целый ли сам кадр — по CRC-16 в его конце.
        for (int start = data.Length - 2; start + offset >= 4 && start >= 0; start--)
        {
            if (data[start] != 0xff || data[start + 1] != 0xf8) continue;
            if (!Header(data, start, out long number, out int block)) continue;
            bool complete = Crc16(data.AsSpan(start, data.Length - start - 2))
                == (data[^2] << 8 | data[^1]);
            long samples = number * nominal + (complete ? block : 0);
            return (offset + (complete ? data.Length : start), TimeSpan.FromSeconds(samples / 48000.0));
        }
        return (0, TimeSpan.Zero);
    }

    // Заголовок кадра: https://www.rfc-editor.org/rfc/rfc9639#name-frame-header
    private static bool Header(byte[] data, int start, out long number, out int block)
    {
        number = 0; block = 0;
        int p = start + 2;
        if (p + 2 > data.Length) return false;
        int blockCode = data[p] >> 4, rateCode = data[p] & 15, channels = data[p + 1] >> 4, size = (data[p + 1] >> 1) & 7;
        if (blockCode == 0 || rateCode is not (0 or 10) || channels > 10 || size is not (0 or 4) || (data[p + 1] & 1) != 0) return false;
        p += 2;
        if (p >= data.Length) return false;
        int extra = data[p] < 0x80 ? 0 : data[p] >= 0xfc ? 5 : data[p] >= 0xf8 ? 4 : data[p] >= 0xf0 ? 3 : data[p] >= 0xe0 ? 2 : data[p] >= 0xc0 ? 1 : -1;
        if (extra < 0 || p + extra >= data.Length) return false;
        number = extra == 0 ? data[p] : data[p] & (0x3f >> extra);
        for (int i = 1; i <= extra; i++)
        {
            if ((data[p + i] & 0xc0) != 0x80) return false;
            number = number << 6 | (uint)(data[p + i] & 0x3f);
        }
        p += extra + 1;
        if (blockCode == 6) { if (p >= data.Length) return false; block = data[p] + 1; p += 1; }
        else if (blockCode == 7) { if (p + 1 >= data.Length) return false; block = (data[p] << 8 | data[p + 1]) + 1; p += 2; }
        else block = blockCode == 1 ? 192 : blockCode <= 5 ? 576 << (blockCode - 2) : 256 << (blockCode - 8);
        if (p >= data.Length) return false;
        return Crc8(data.AsSpan(start, p - start)) == data[p];
    }

    private static byte Crc8(ReadOnlySpan<byte> bytes)
    {
        int crc = 0;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? (crc << 1 ^ 0x07) & 0xff : crc << 1 & 0xff;
        }
        return (byte)crc;
    }

    private static int Crc16(ReadOnlySpan<byte> bytes)
    {
        int crc = 0;
        foreach (byte b in bytes)
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (crc << 1 ^ 0x8005) & 0xffff : crc << 1 & 0xffff;
        }
        return crc;
    }
}
