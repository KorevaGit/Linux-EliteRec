using System.Buffers.Binary;

namespace EliteRec.Probe;

internal sealed record Mp3Inspection(int Frames, int SampleRate, int BitrateKbps, string Mode, long Bytes)
{
    // Стенд создаёт MPEG-1 Layer III CBR 48k без ID3/Xing: проверяем КАЖДЫЙ кадр,
    // чтобы joint stereo не появился в середине файла. Это не проверка декодированного звука.
    internal static Mp3Inspection Read(Stream input, bool stereo)
    {
        int expectedMode = stereo ? 0 : 3, expectedBitrate = stereo ? 192 : 128;
        int[] rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        Span<byte> header = stackalloc byte[4];
        Span<byte> body = stackalloc byte[2048];
        long bytes = 0; int frames = 0;
        while (true)
        {
            int first = input.ReadByte();
            if (first < 0) break;
            header[0] = (byte)first;
            input.ReadExactly(header[1..]);
            uint h = BinaryPrimitives.ReadUInt32BigEndian(header);
            if ((h & 0xfffe0000) != 0xfffa0000) throw new InvalidDataException("Не MPEG-1 Layer III.");
            int bitrate = rates[(h >> 12) & 15], rateIndex = (int)((h >> 10) & 3), mode = (int)((h >> 6) & 3);
            if (rateIndex != 1 || bitrate != expectedBitrate || mode != expectedMode)
                throw new InvalidDataException($"Неожиданный MP3: rate index {rateIndex}, {bitrate} кбит/с, mode {mode}.");
            int size = 144000 * bitrate / 48000 + (int)((h >> 9) & 1);
            input.ReadExactly(body[..(size - 4)]);
            bytes += size; frames++;
        }
        if (frames == 0) throw new InvalidDataException("Пустой MP3.");
        return new(frames, 48000, expectedBitrate, stereo ? "STEREO" : "MONO", bytes);
    }
}
