using EliteRec.Core.Recording;
using EliteRec.Encoding.Native;

namespace EliteRec.Encoding;

/// <summary>Потоковый CBR MP3 48 кГц без Xing/ID3: кадры самодостаточны, обрезанный хвост восстанавливается.</summary>
internal sealed unsafe class Mp3Encoder : IPartEncoder
{
    private nint _encoder;
    private readonly int _channels;
    private readonly Stream _output;
    // lame.h: 1.25 * отсчёты + 7200 байт достаточно для любого вызова и flush.
    private readonly byte[] _buffer = new byte[SynchronizedAudioMixer.BlockFrames * 5 / 4 + 7200];
    private readonly float[] _left = new float[SynchronizedAudioMixer.BlockFrames], _right = new float[SynchronizedAudioMixer.BlockFrames];

    internal Mp3Encoder(bool stereo, Stream output)
    {
        _output = output;
        _channels = stereo ? 2 : 1;
        _encoder = Lame.Init();
        if (_encoder == 0) throw new InvalidOperationException("libmp3lame: lame_init вернул NULL.");
        try
        {
            Check(Lame.SetInputRate(_encoder, SynchronizedAudioMixer.SampleRate));
            Check(Lame.SetOutputRate(_encoder, SynchronizedAudioMixer.SampleRate));
            Check(Lame.SetChannels(_encoder, _channels));
            Check(Lame.SetMode(_encoder, stereo ? 0 : 3)); // STEREO, не JOINT_STEREO: каналы не смешиваются.
            Check(Lame.SetBitrate(_encoder, stereo ? 192 : 128));
            Check(Lame.SetVbr(_encoder, 0));
            Check(Lame.SetVbrTag(_encoder, 0));
            Check(Lame.SetQuality(_encoder, 2));
            Check(Lame.InitParams(_encoder));
        }
        catch { Dispose(); throw; }
    }

    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        ObjectDisposedException.ThrowIf(_encoder == 0, this);
        if (left.Length > _left.Length || (_channels == 2 && right.Length != left.Length)) throw new ArgumentException("Неверный MP3-блок.");
        PartFormat.Clean(left, _left);
        if (_channels == 2) PartFormat.Clean(right, _right);
        fixed (float* l = _left, r = _right)
        fixed (byte* target = _buffer)
        {
            int count = Lame.Encode(_encoder, l, _channels == 2 ? r : l, left.Length, target, _buffer.Length);
            Check(count);
            _output.Write(_buffer, 0, count);
        }
    }

    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_encoder == 0, this);
        fixed (byte* target = _buffer)
        {
            int count = Lame.Flush(_encoder, target, _buffer.Length);
            Check(count);
            _output.Write(_buffer, 0, count);
        }
    }

    private static void Check(int result)
    {
        if (result < 0) throw new IOException($"libmp3lame: ошибка {result}.");
    }

    public void Dispose()
    {
        if (_encoder == 0) return;
        _ = Lame.Close(_encoder);
        _encoder = 0;
    }
}
