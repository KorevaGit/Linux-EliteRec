using System.Buffers.Binary;
using EliteRec.Core.Recording;

namespace EliteRec.Encoding;

/// <summary>Пара PCM16 WAV: mix mono, L=микрофон / R=система. R3 добавит части и восстановление.</summary>
public sealed class WavRecordingSink : IRecordingSink
{
    private readonly WavFile _mix, _stereo;
    public RecordingFiles Files { get; }
    public long Frames => _mix.Frames;
    private bool _disposed;

    public WavRecordingSink(string basePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(basePath))!);
        Files = new(basePath + ".wav", basePath + "_stereo.wav");
        _mix = new WavFile(Files.MixPath, 1);
        try { _stereo = new WavFile(Files.StereoPath, 2); }
        catch { _mix.Dispose(); File.Delete(Files.MixPath); throw; }
    }

    public void Write(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system, ReadOnlySpan<float> mix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (microphone.Length != system.Length || microphone.Length != mix.Length) throw new ArgumentException("Размеры каналов различаются.");
        _mix.Write(mix, []);
        _stereo.Write(microphone, system);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mix.Dispose(); }
        finally { _stereo.Dispose(); }
    }

    private sealed class WavFile : IDisposable
    {
        private readonly FileStream _file;
        private readonly int _channels;
        private readonly byte[] _buffer = new byte[SynchronizedAudioMixer.BlockFrames * 4];
        internal long Frames { get; private set; }

        internal WavFile(string path, int channels)
        {
            _channels = channels;
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            try { Header(); }
            catch { _file.Dispose(); throw; }
        }

        internal void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            if (left.Length > SynchronizedAudioMixer.BlockFrames) throw new ArgumentException("Слишком большой WAV-блок.");
            if ((Frames + left.Length) * _channels * 2 > uint.MaxValue - 36) throw new IOException("Достигнут предел RIFF WAV (4 ГБ). Завершите запись.");
            int pos = 0;
            for (int i = 0; i < left.Length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(pos), Pcm(left[i])); pos += 2;
                if (_channels == 2) { BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(pos), Pcm(right[i])); pos += 2; }
            }
            _file.Write(_buffer, 0, pos);
            Frames += left.Length;
        }

        private static short Pcm(float sample) => !float.IsFinite(sample) ? (short)0
            : (short)Math.Clamp((int)Math.Round(Math.Clamp(sample, -1f, 1f) * 32768.0), short.MinValue, short.MaxValue);

        private void Header()
        {
            _file.Position = 0;
            using var writer = new BinaryWriter(_file, System.Text.Encoding.ASCII, leaveOpen: true);
            uint bytes = checked((uint)(Frames * _channels * 2));
            writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)_channels);
            writer.Write(48000); writer.Write(48000 * _channels * 2);
            writer.Write((short)(_channels * 2)); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(bytes);
        }

        public void Dispose()
        {
            try { Header(); _file.Flush(flushToDisk: true); }
            finally { _file.Dispose(); }
        }
    }
}
