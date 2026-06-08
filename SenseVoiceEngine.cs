using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;

namespace CantoneseDictation;

public class SenseVoiceResult
{
    public string Text { get; set; } = "";
    public double TimeSeconds { get; set; }
}

public class SenseVoiceEngine : IDisposable
{
    private readonly string _baseDir;
    private InferenceSession? _session;
    private List<string>? _tokens;

    // FFT constants
    private const int SampleRate = 16000;
    private const int FftSize = 512;
    private const int HopLength = 160;
    private const int NumMels = 80;
    private const int FrameStack = 7;
    private const int FeatDim = NumMels * FrameStack; // 560

    // Pre-computed mel filterbank
    private float[,]? _melBasis;
    private float[]? _hanningWindow;

    public SenseVoiceEngine(string baseDir)
    {
        _baseDir = baseDir;
    }

    public void Load()
    {
        var modelPath = Path.Combine(_baseDir, "model_quant.onnx");
        var tokensPath = Path.Combine(_baseDir, "tokens.txt");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model not found: {modelPath}");

        // Load tokens
        _tokens = File.ReadAllLines(tokensPath).Select(l => l.Trim()).ToList();
        while (_tokens.Count < 25055) _tokens.Add("<unk>");

        // Load ONNX Runtime
        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL;
        _session = new InferenceSession(modelPath, opts);

        // Pre-compute mel filterbank
        _melBasis = CreateMelFilterbank();
        _hanningWindow = CreateHanningWindow();
    }

    public SenseVoiceResult Transcribe(string audioPath, string language = "auto")
    {
        if (_session == null) throw new InvalidOperationException("Model not loaded");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        AppLogger.Info($"Transcribe start: {audioPath}, lang={language}");

        // 1. Load & resample audio to 16kHz mono
        var samples = LoadAudioMono16k(audioPath);
        AppLogger.Info($"Audio loaded: {samples.Length} samples ({samples.Length / 16000.0:F2}s)");

        if (samples.Length < 1600) // less than 100ms
        {
            AppLogger.Warn($"Audio too short: {samples.Length} samples (<100ms)");
            sw.Stop();
            return new SenseVoiceResult { Text = "", TimeSeconds = sw.Elapsed.TotalSeconds };
        }

        // 2. Compute log Mel spectrogram
        var mels = ComputeLogMelSpectrogram(samples);
        AppLogger.Info($"Mel spectrogram: {mels.Count} frames x {mels[0]?.Length ?? 0} dims");

        // 3. Stack frames
        var features = StackFrames(mels);

        // 4. Run ONNX inference
        int numFrames = features.Count;
        int langId = language switch { "zh" => 0, "en" => 1, "yue" => 2, "ja" => 3, "ko" => 4, _ => 2 };
        AppLogger.Info($"ONNX input: 1 x {numFrames} x {FeatDim} (speech_lengths={numFrames}, language={language}({langId}))");

        var speech = new DenseTensor<float>(new[] { 1, numFrames, FeatDim });
        for (int t = 0; t < numFrames; t++)
            for (int f = 0; f < FeatDim; f++)
                speech[0, t, f] = features[t][f];

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("speech", speech),
            NamedOnnxValue.CreateFromTensor("speech_lengths", new DenseTensor<int>(new[] { numFrames })),
            NamedOnnxValue.CreateFromTensor("language", new DenseTensor<int>(new[] { langId })),
            NamedOnnxValue.CreateFromTensor("textnorm", new DenseTensor<int>(new[] { 1 })),
        };

        using var results = _session.Run(inputs);
        var logits = results[0].AsTensor<float>();
        AppLogger.Info($"ONNX output shape: [{string.Join(", ", Enumerable.Range(0, logits.Dimensions.Length).Select(i => logits.Dimensions[i]))}]");

        // Log top-3 tokens at 5 probe points
        int maxT = logits.Dimensions[1];
        int vocab = logits.Dimensions[2];
        int step = Math.Max(1, maxT / 5);
        for (int probe = 0; probe < maxT; probe += step)
        {
            var top3 = Enumerable.Range(0, vocab)
                .Select(v => (v, logits[0, probe, v]))
                .OrderByDescending(x => x.Item2)
                .Take(3)
                .Select(x => $"[{x.v}]{(x.v < _tokens?.Count ? _tokens[x.v] : "?")}={x.Item2:F2}")
                .ToList();
            AppLogger.Info($"  Logits[t={probe}]: {string.Join(", ", top3)}");
        }

        // 5. CTC decode
        var text = GreedyCtcDecode(logits);
        AppLogger.Info($"Decoded text ({text.Length} chars): \"{text}\"");

        sw.Stop();
        AppLogger.Info($"Transcribe done: {sw.Elapsed.TotalSeconds:F3}s");
        return new SenseVoiceResult { Text = text, TimeSeconds = sw.Elapsed.TotalSeconds };
    }

    private float[] LoadAudioMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        var buffer = new List<float>();

        // If not 16kHz, we need to resample
        if (reader.WaveFormat.SampleRate != SampleRate || reader.WaveFormat.Channels != 1)
        {
            // Read all, resample manually
            var allSamples = new List<float>();
            var readBuf = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
            int read;
            while ((read = reader.Read(readBuf, 0, readBuf.Length)) > 0)
                for (int i = 0; i < read; i += reader.WaveFormat.Channels)
                    allSamples.Add(readBuf[i]);

            // Simple downsampling (keep every Nth sample)
            if (reader.WaveFormat.SampleRate > SampleRate)
            {
                int ratio = reader.WaveFormat.SampleRate / SampleRate;
                for (int i = 0; i < allSamples.Count; i += ratio)
                    buffer.Add(allSamples[i]);
            }
            else
            {
                buffer = allSamples;
            }
        }
        else
        {
            var readBuf = new float[4096];
            int read;
            while ((read = reader.Read(readBuf, 0, readBuf.Length)) > 0)
                for (int i = 0; i < read; i++)
                    buffer.Add(readBuf[i]);
        }

        return buffer.ToArray();
    }

    private float[,] CreateMelFilterbank()
    {
        var melBasis = new float[NumMels, FftSize / 2 + 1];

        double fMin = 20, fMax = 8000;
        double melMin = 2595 * Math.Log10(1 + fMin / 700);
        double melMax = 2595 * Math.Log10(1 + fMax / 700);

        var melPoints = new double[NumMels + 2];
        for (int i = 0; i < NumMels + 2; i++)
            melPoints[i] = fMin + (fMax - fMin) * i / (NumMels + 1);

        var fftFreqs = new double[FftSize / 2 + 1];
        for (int i = 0; i <= FftSize / 2; i++)
            fftFreqs[i] = (double)i * SampleRate / FftSize;

        for (int m = 0; m < NumMels; m++)
        {
            double fLeft = melPoints[m];
            double fCenter = melPoints[m + 1];
            double fRight = melPoints[m + 2];

            for (int k = 0; k <= FftSize / 2; k++)
            {
                double f = fftFreqs[k];
                if (f >= fLeft && f <= fCenter)
                    melBasis[m, k] = (float)((f - fLeft) / (fCenter - fLeft));
                else if (f >= fCenter && f <= fRight)
                    melBasis[m, k] = (float)((fRight - f) / (fRight - fCenter));
                else
                    melBasis[m, k] = 0;
            }
        }

        return melBasis;
    }

    private float[] CreateHanningWindow()
    {
        var win = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
            win[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))));
        return win;
    }

    private List<float[]> ComputeLogMelSpectrogram(float[] samples)
    {
        if (_melBasis == null || _hanningWindow == null)
            throw new InvalidOperationException("Filterbank not initialized");

        int numFrames = Math.Max(0, (samples.Length - FftSize) / HopLength) + 1;
        var mels = new List<float[]>();

        for (int t = 0; t < numFrames; t++)
        {
            int start = t * HopLength;

            // Apply window and compute FFT
            var spectrum = ComputeMagnitudeSpectrum(samples, start, _hanningWindow);

            // Apply mel filterbank (80 dims)
            var mel = new float[NumMels];
            for (int m = 0; m < NumMels; m++)
            {
                double sum = 0;
                for (int k = 0; k <= FftSize / 2; k++)
                    sum += spectrum[k] * _melBasis[m, k];
                mel[m] = (float)Math.Log(Math.Max(sum, 1e-10));
            }

            // Simple mean-variance normalization
            float mean = mel.Average();
            float var = (float)Math.Sqrt(mel.Select(x => (x - mean) * (x - mean)).Average() + 1e-10);
            for (int m = 0; m < NumMels; m++)
                mel[m] = (mel[m] - mean) / var;

            mels.Add(mel);
        }

        return mels;
    }

    private float[] ComputeMagnitudeSpectrum(float[] samples, int start, float[] window)
    {
        // Real FFT using Goertzel-like approach (simple DFT for real inputs)
        int n = FftSize;
        int halfN = n / 2 + 1;
        var spectrum = new float[halfN];

        // Apply window
        var frame = new float[n];
        for (int i = 0; i < n; i++)
        {
            int idx = start + i;
            frame[i] = (idx < samples.Length ? samples[idx] : 0) * window[i];
        }

        // Simple DFT
        for (int k = 0; k < halfN; k++)
        {
            double real = 0, imag = 0;
            for (int i = 0; i < n; i++)
            {
                double angle = 2 * Math.PI * k * i / n;
                real += frame[i] * Math.Cos(angle);
                imag -= frame[i] * Math.Sin(angle);
            }
            spectrum[k] = (float)((real * real + imag * imag) / n);
        }

        return spectrum;
    }

    private List<float[]> StackFrames(List<float[]> feats)
    {
        int n = feats.Count;
        var result = new List<float[]>();

        for (int t = 0; t < n; t++)
        {
            var stacked = new float[FeatDim];
            int idx = 0;
            for (int offset = -3; offset <= 3; offset++)
            {
                int srcIdx = Math.Clamp(t + offset, 0, n - 1);
                Array.Copy(feats[srcIdx], 0, stacked, idx, NumMels);
                idx += NumMels;
            }
            result.Add(stacked);
        }

        return result;
    }

    private string GreedyCtcDecode(Tensor<float> logits)
    {
        if (_tokens == null) return "";

        int maxTime = logits.Dimensions[1];
        int vocabSize = logits.Dimensions[2];

        var decoded = new List<int>();
        int prev = -1;

        for (int t = 0; t < maxTime; t++)
        {
            int best = 0;
            float bestScore = float.MinValue;
            for (int v = 0; v < vocabSize; v++)
            {
                if (logits[0, t, v] > bestScore)
                {
                    bestScore = logits[0, t, v];
                    best = v;
                }
            }

            if (best > 0 && best != prev)
                decoded.Add(best);
            prev = best;
        }

        var text = string.Concat(decoded.Select(t =>
        {
            if (t >= 0 && t < _tokens.Count)
            {
                var tok = _tokens[t];
                if (tok.StartsWith("<") && tok.EndsWith(">")) return "";
                return tok.Replace("▁", " ");
            }
            return "";
        }));

        text = string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Trim();
    }

    public void Dispose() => _session?.Dispose();
}
