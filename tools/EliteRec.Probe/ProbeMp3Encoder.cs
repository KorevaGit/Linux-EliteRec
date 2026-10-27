using EliteRec.Probe.Native;

namespace EliteRec.Probe;

// Диагностическая обёртка R0, не готовый потоковый кодер R3.
internal sealed unsafe class ProbeMp3Encoder : IDisposable
{
    private nint _encoder;
    private readonly byte[] _buffer = new byte[12000]; // 1.25 * 1024 + 7200, с запасом для flush.
    private bool _finished;

    internal ProbeMp3Encoder(bool stereo)
    {
        _encoder = Lame.Init();
        if (_encoder == 0) throw new InvalidOperationException("lame_init: NULL");
        try
        {
            Check(Lame.SetInputRate(_encoder, 48000));
            Check(Lame.SetOutputRate(_encoder, 48000));
            Check(Lame.SetChannels(_encoder, stereo ? 2 : 1));
            Check(Lame.SetMode(_encoder, stereo ? 0 : 3)); // STEREO, не JOINT_STEREO; MONO.
            Check(Lame.SetBitrate(_encoder, stereo ? 192 : 128));
            Check(Lame.SetVbr(_encoder, 0));
            Check(Lame.SetVbrTag(_encoder, 0)); // Поток не требует seek для Xing-тега.
            Check(Lame.SetQuality(_encoder, 2));
            Check(Lame.InitParams(_encoder));
        }
        catch { Dispose(); throw; }
    }

    internal void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Stream output)
    {
        ObjectDisposedException.ThrowIf(_encoder == 0, this);
        if (_finished) throw new InvalidOperationException("Кодер уже завершён.");
        if (left.Length != right.Length || left.Length > 1024) throw new ArgumentException("Ожидаются равные блоки до 1024 отсчётов.");
        fixed (float* l = left, r = right)
        fixed (byte* target = _buffer)
        {
            int count = Lame.Encode(_encoder, l, r, left.Length, target, _buffer.Length);
            Check(count);
            output.Write(_buffer.AsSpan(0, count));
        }
    }

    internal void Finish(Stream output)
    {
        ObjectDisposedException.ThrowIf(_encoder == 0, this);
        if (_finished) throw new InvalidOperationException("Кодер уже завершён.");
        fixed (byte* target = _buffer)
        {
            int count = Lame.Flush(_encoder, target, _buffer.Length);
            Check(count);
            output.Write(_buffer.AsSpan(0, count));
        }
        _finished = true;
    }

    private static void Check(int result)
    {
        if (result < 0) throw new InvalidOperationException($"libmp3lame: {result}");
    }

    public void Dispose()
    {
        if (_encoder == 0) return;
        _ = Lame.Close(_encoder);
        _encoder = 0;
    }
}
