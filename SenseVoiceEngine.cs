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

        // Fallback: check AppData location
        if (!File.Exists(modelPath))
            modelPath = AppPaths.ModelDir;
        if (!File.Exists(tokensPath))
            tokensPath = Path.Combine(AppPaths.AppDir, "tokens.txt");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model not found in {_baseDir} or {AppPaths.AppDir}");

        // Load tokens
        _tokens = File.ReadAllLines(tokensPath).Select(l => l.Trim()).ToList();
        while (_tokens.Count < 25055) _tokens.Add("<unk>");

        // Load ONNX Runtime
        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, opts);

        // Pre-compute mel filterbank
        _melBasis = CreateMelFilterbank();
        _hanningWindow = CreateHanningWindow();
    }

    public SenseVoiceResult Transcribe(string audioPath, string language = "auto",
        IReadOnlyDictionary<string, int>? hotwords = null)
    {
        if (_session == null) throw new InvalidOperationException("Model not loaded");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        AppLogger.Info($"Transcribe start: {audioPath}, lang={language}, hotwords={(hotwords != null ? hotwords.Count : 0)}");

        // 1. Load & resample audio to 16kHz mono
        var samples = LoadAudioMono16k(audioPath);
        AppLogger.Info($"Audio loaded: {samples.Length} samples ({samples.Length / 16000.0:F2}s)");

        if (samples.Length < 1600)
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
            NamedOnnxValue.CreateFromTensor("speech_lengths", new DenseTensor<int>(new[] { 1 }) { [0] = numFrames }),
            NamedOnnxValue.CreateFromTensor("language", new DenseTensor<int>(new[] { 1 }) { [0] = langId }),
            NamedOnnxValue.CreateFromTensor("textnorm", new DenseTensor<int>(new[] { 1 }) { [0] = 1 }),
        };

        using var results = _session.Run(inputs);
        var logits = results[0].AsTensor<float>();
        AppLogger.Info($"ONNX output shape: [{string.Join(", ", Enumerable.Range(0, logits.Dimensions.Length).Select(i => logits.Dimensions[i]))}]");

        int maxT = logits.Dimensions[1];
        int vocab = logits.Dimensions[2];

        // Log top-3 tokens at 5 probe points
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

        // 5. Pre-compute hotword token IDs for biasing
        HashSet<int>? hotwordTokenIds = null;
        if (hotwords != null && hotwords.Count > 0)
        {
            hotwordTokenIds = ComputeHotwordTokenIds(hotwords);
            AppLogger.Info($"Hotword token IDs: {hotwordTokenIds.Count} unique tokens mapped from {hotwords.Count} hotwords");
        }

        // 6. CTC decode with optional hotword biasing
        var text = GreedyCtcDecode(logits, hotwordTokenIds);

        // 7. Post-process with hotword fuzzy matching
        if (hotwords != null && hotwords.Count > 0 && !string.IsNullOrEmpty(text))
        {
            var postProcessed = ApplyHotwordPostProcessing(text, hotwords);
            if (postProcessed != text)
            {
                AppLogger.Info($"Hotword post-processing: \"{text}\" -> \"{postProcessed}\"");
                text = postProcessed;
            }
        }

        AppLogger.Info($"Decoded text ({text.Length} chars): \"{text}\"");

        sw.Stop();
        AppLogger.Info($"Transcribe done: {sw.Elapsed.TotalSeconds:F3}s");
        return new SenseVoiceResult { Text = text, TimeSeconds = sw.Elapsed.TotalSeconds };
    }

    private float[] LoadAudioMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        var mono = new List<float>();
        var readBuf = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
        int read;
        while ((read = reader.Read(readBuf, 0, readBuf.Length)) > 0)
            for (int i = 0; i < read; i += reader.WaveFormat.Channels)
                mono.Add(readBuf[i]);

        var srcRate = reader.WaveFormat.SampleRate;
        if (srcRate == SampleRate)
            return mono.ToArray();

        // Resample using nearest-neighbor decimation (original approach)
        double ratio = (double)srcRate / SampleRate;
        int outLen = (int)(mono.Count / ratio);
        var output = new float[outLen];

        if (ratio >= 1.0) // Downsample
        {
            int step = (int)Math.Round(ratio);
            for (int i = 0; i < outLen && i * step < mono.Count; i++)
                output[i] = mono[i * step];
        }
        else // Upsample with linear interpolation
        {
            for (int i = 0; i < outLen; i++)
            {
                double srcIdx = i * ratio;
                int lo = (int)srcIdx;
                int hi = Math.Min(lo + 1, mono.Count - 1);
                double frac = srcIdx - lo;
                output[i] = (float)(mono[lo] * (1 - frac) + mono[hi] * frac);
            }
        }

        return output;
    }

    private float[,] CreateMelFilterbank()
    {
        var melBasis = new float[NumMels, FftSize / 2 + 1];

        double fMin = 20, fMax = 8000;
        double melMin = 2595 * Math.Log10(1 + fMin / 700);
        double melMax = 2595 * Math.Log10(1 + fMax / 700);

        // Mel-spaced center frequencies
        var melPoints = new double[NumMels + 2];
        for (int i = 0; i < NumMels + 2; i++)
        {
            double mel = melMin + (melMax - melMin) * i / (NumMels + 1);
            melPoints[i] = 700 * (Math.Pow(10, mel / 2595) - 1);
        }

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

    private string GreedyCtcDecode(Tensor<float> logits, HashSet<int>? hotwordTokenIds = null)
    {
        if (_tokens == null) return "";

        int maxTime = logits.Dimensions[1];
        int vocabSize = logits.Dimensions[2];
        const float hotwordBoost = 2.5f;

        var decoded = new List<int>();
        int prev = -1;

        for (int t = 0; t < maxTime; t++)
        {
            int best = 0;
            float bestScore = float.MinValue;
            for (int v = 0; v < vocabSize; v++)
            {
                var score = logits[0, t, v];
                if (hotwordTokenIds != null && hotwordTokenIds.Contains(v))
                    score += hotwordBoost;
                if (score > bestScore)
                {
                    bestScore = score;
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

    private HashSet<int> ComputeHotwordTokenIds(IReadOnlyDictionary<string, int> hotwords)
    {
        if (_tokens == null) return new HashSet<int>();

        var ids = new HashSet<int>();
        foreach (var word in hotwords.Keys)
        {
            if (string.IsNullOrEmpty(word) || word.Length < 2) continue;
            var lower = word.ToLowerInvariant();
            for (int i = 0; i < _tokens.Count; i++)
            {
                var t = _tokens[i];
                if (t.Length < 2) continue;
                var clean = t.Replace("▁", "").ToLowerInvariant();
                if (clean.Length >= 2 && lower.Contains(clean))
                    ids.Add(i);
            }
        }
        return ids;
    }

    private string ApplyHotwordPostProcessing(string text, IReadOnlyDictionary<string, int> hotwords)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) return text;

        bool changed = false;
        foreach (var (hotword, _) in hotwords.OrderByDescending(kv => kv.Value))
        {
            if (string.IsNullOrEmpty(hotword) || hotword.Length < 2) continue;

            var hwLower = hotword.ToLowerInvariant();
            var hwClean = hotword.Trim();

            // Try matching as a single multi-word phrase
            var joined = string.Join(" ", words).ToLowerInvariant();
            if (joined.Contains(hwLower))
            {
                int idx = joined.IndexOf(hwLower);
                int wordStart = joined[..idx].Count(c => c == ' ');
                int wordEnd = wordStart + hwLower.Count(c => c == ' ') + 1;
                for (int i = wordStart; i < wordEnd && i < words.Count; i++)
                {
                    words[i] = "";
                }
                words[wordStart] = hwClean;
                changed = true;
                continue;
            }

            // Try word-by-word fuzzy matching using character overlap
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i].Trim(".,!?，。！？、；：\"''（）()「」【】".ToCharArray());
                if (string.IsNullOrEmpty(w) || w.Length < 2) continue;

                var wLower = w.ToLowerInvariant();

                // Direct match
                if (wLower == hwLower)
                {
                    words[i] = hwClean;
                    changed = true;
                    continue;
                }

                // Check if hotword is a compound (e.g. "ComfyUI" -> "comfy" + "ui")
                if (hwLower.Contains(wLower) && hwLower.Length > wLower.Length + 2)
                {
                    words[i] = hwClean;
                    changed = true;
                }
            }
        }

        if (!changed) return text;
        var result = string.Join(" ", words.Where(w => !string.IsNullOrEmpty(w)));
        return string.Join(" ", result.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public void Dispose() => _session?.Dispose();
}
