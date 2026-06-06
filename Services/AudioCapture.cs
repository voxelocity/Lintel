using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;

namespace Lintel.Services;

/// <summary>
/// Captures the system audio output (WASAPI loopback), runs an FFT, and exposes log-spaced
/// magnitude bands for the visualizer. Best-effort: if capture can't start, the visualizer
/// falls back to its ambient animation.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private const int FftLen = 1024;
    private const int M = 10; // 2^10 = 1024

    private WasapiLoopbackCapture? _cap;
    private readonly object _lock = new();
    private readonly Complex[] _fft = new Complex[FftLen];
    private readonly float[] _window = new float[FftLen];
    private readonly float[] _mono = new float[FftLen];
    private readonly float[] _spectrum = new float[FftLen / 2];
    private int _filled;
    private bool _running;

    public AudioCapture()
    {
        for (int i = 0; i < FftLen; i++) _window[i] = (float)FastFourierTransform.HannWindow(i, FftLen);
    }

    public void Start()
    {
        if (_running) return;
        try
        {
            _cap = new WasapiLoopbackCapture();
            _cap.DataAvailable += OnData;
            _cap.StartRecording();
            _running = true;
            Controls.Visualizer.Provider = Sample;
        }
        catch { _cap = null; }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try
        {
            Controls.Visualizer.Provider = null;
            if (_cap != null) { _cap.DataAvailable -= OnData; _cap.StopRecording(); _cap.Dispose(); }
        }
        catch { }
        _cap = null;
        lock (_lock) Array.Clear(_spectrum);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_cap == null) return;
        int ch = Math.Max(1, _cap.WaveFormat.Channels);
        var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
        for (int i = 0; i + ch <= samples.Length; i += ch)
        {
            float m = 0;
            for (int c = 0; c < ch; c++) m += samples[i + c];
            _mono[_filled++] = m / ch;
            if (_filled >= FftLen)
            {
                DoFft();
                Array.Copy(_mono, FftLen / 2, _mono, 0, FftLen / 2); // 50% overlap
                _filled = FftLen / 2;
            }
        }
    }

    private void DoFft()
    {
        for (int i = 0; i < FftLen; i++) { _fft[i].X = _mono[i] * _window[i]; _fft[i].Y = 0; }
        FastFourierTransform.FFT(true, M, _fft);
        lock (_lock)
        {
            for (int i = 0; i < FftLen / 2; i++)
            {
                float mag = (float)Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
                _spectrum[i] = Math.Max(mag, _spectrum[i] * 0.55f); // fast attack, decay
            }
        }
    }

    private float _runningMax = 1e-4f;

    /// <summary>Resample the spectrum into <paramref name="bars"/> log-spaced bands (0..1),
    /// auto-normalised to the current loudness so it stays sensitive at any volume.</summary>
    public float[]? Sample(int bars)
    {
        if (!_running) return null;
        var outp = new float[bars];
        lock (_lock)
        {
            int bins = FftLen / 2;
            int maxBin = (int)(bins * 0.55);
            float frameMax = 1e-4f;
            var raw = new float[bars];
            for (int b = 0; b < bars; b++)
            {
                double f0 = Math.Pow(maxBin, b / (double)bars);
                double f1 = Math.Pow(maxBin, (b + 1) / (double)bars);
                int i0 = Math.Clamp((int)f0, 1, maxBin - 1);
                int i1 = Math.Clamp((int)Math.Ceiling(f1), i0 + 1, maxBin);
                float sum = 0;
                for (int i = i0; i < i1; i++) sum += _spectrum[i];
                // weight higher bands up (they're quieter) so the whole bar moves
                float avg = sum / (i1 - i0) * (1f + b / (float)bars * 1.8f);
                raw[b] = avg;
                if (avg > frameMax) frameMax = avg;
            }

            // adaptive ceiling: rise instantly to peaks, decay slowly
            _runningMax = Math.Max(frameMax, _runningMax * 0.994f);
            float norm = Math.Max(_runningMax, 1e-4f);
            for (int b = 0; b < bars; b++)
                outp[b] = (float)Math.Clamp(Math.Pow(raw[b] / norm, 0.55), 0, 1); // gamma lifts low values
        }
        return outp;
    }

    public void Dispose() => Stop();
}
