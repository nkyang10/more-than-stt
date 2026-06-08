using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SherpaOnnx;

namespace CantoneseDictation;

public class SenseVoiceResult
{
    public string Text { get; set; } = "";
    public string Language { get; set; } = "";
    public double TimeSeconds { get; set; }
}

public class SenseVoiceEngine : IDisposable
{
    private readonly string _baseDir;
    private OfflineRecognizer? _recognizer;
    private string? _modelPath;
    private string? _tokensPath;
    private static readonly string[] SupportedLanguages = { "auto", "zh", "en", "yue", "ja", "ko" };

    public SenseVoiceEngine(string baseDir)
    {
        _baseDir = baseDir;
    }

    public void Load()
    {
        // Check for Sherpa-ONNX model first, then fallback to legacy model
        var sherpaModelDir = Path.Combine(_baseDir, "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09");
        _modelPath = Path.Combine(sherpaModelDir, "model.int8.onnx");
        _tokensPath = Path.Combine(sherpaModelDir, "tokens.txt");

        if (!File.Exists(_modelPath))
        {
            // Fallback: try older model
            sherpaModelDir = Path.Combine(_baseDir, "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17");
            _modelPath = Path.Combine(sherpaModelDir, "model.int8.onnx");
            _tokensPath = Path.Combine(sherpaModelDir, "tokens.txt");
        }

        if (!File.Exists(_modelPath))
        {
            // Fallback: try legacy model_quant.onnx (will use old method - not supported)
            _modelPath = Path.Combine(_baseDir, "model_quant.onnx");
            if (!File.Exists(_modelPath))
                _modelPath = AppPaths.ModelDir;
            _tokensPath = Path.Combine(_baseDir, "tokens.txt");
            if (!File.Exists(_tokensPath))
                _tokensPath = Path.Combine(AppPaths.AppDir, "tokens.txt");
        }

        if (!File.Exists(_modelPath))
            throw new FileNotFoundException($"ONNX model not found. Place model.int8.onnx in {Path.Combine(_baseDir, "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09")} or download from https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models");

        if (!File.Exists(_tokensPath))
            throw new FileNotFoundException($"tokens.txt not found next to model");

        AppLogger.Info($"Sherpa-ONNX model: {_modelPath}");
        AppLogger.Info($"Sherpa-ONNX tokens: {_tokensPath}");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig = new FeatureConfig { SampleRate = 16000, FeatureDim = 80 };
        config.ModelConfig = new OfflineModelConfig();
        config.ModelConfig.SenseVoice = new OfflineSenseVoiceModelConfig();
        config.ModelConfig.SenseVoice.Model = _modelPath;
        config.ModelConfig.SenseVoice.Language = "auto";
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        config.ModelConfig.Tokens = _tokensPath;
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        _recognizer = new OfflineRecognizer(config);
        AppLogger.Info("Sherpa-ONNX recognizer created successfully");
    }

    public SenseVoiceResult Transcribe(string audioPath, string language = "auto",
        IReadOnlyDictionary<string, int>? hotwords = null)
    {
        if (_recognizer == null) throw new InvalidOperationException("Model not loaded");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        AppLogger.Info($"Transcribe: {audioPath}, lang={language}, hotwords={hotwords?.Count ?? 0}");

        // Map language parameter to Sherpa-ONNX values
        var lang = SupportedLanguages.Contains(language) ? language : "auto";

        // Build hotwords file if needed
        string? hotwordsFile = null;
        if (hotwords != null && hotwords.Count > 0)
        {
            hotwordsFile = Path.GetTempFileName();
            var lines = hotwords.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} {kv.Value}");
            File.WriteAllLines(hotwordsFile, lines);
        }

        // Read audio using NAudio
        using var audio = new NAudio.Wave.AudioFileReader(audioPath);
        var samples = new List<float>();
        var buf = new float[8192];
        int read;
        while ((read = audio.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i += audio.WaveFormat.Channels)
                samples.Add(buf[i]);

        var srcRate = audio.WaveFormat.SampleRate;
        double duration = samples.Count / (double)srcRate;
        AppLogger.Info($"Audio: {samples.Count} samples @ {srcRate}Hz ({duration:F2}s)");

        if (samples.Count < 1600)
        {
            AppLogger.Warn($"Audio too short: {samples.Count} samples");
            sw.Stop();
            CleanupHotwordsFile(hotwordsFile);
            return new SenseVoiceResult { Text = "", TimeSeconds = sw.Elapsed.TotalSeconds };
        }

        var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(srcRate, samples.ToArray());

        _recognizer.Decode(stream);

        var result = stream.Result;
        var text = result.Text?.Trim() ?? "";

        // Sherpa-ONNX returns text with language prefix/suffix tokens - clean it
        text = CleanSenseVoiceText(text);

        sw.Stop();
        AppLogger.Info($"Result ({text.Length} chars): \"{text}\" in {sw.Elapsed.TotalSeconds:F3}s");

        stream.Dispose();
        CleanupHotwordsFile(hotwordsFile);

        return new SenseVoiceResult
        {
            Text = text,
            TimeSeconds = sw.Elapsed.TotalSeconds
        };
    }

    private static string CleanSenseVoiceText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Remove common SenseVoice artifacts
        return text
            .Replace("<|nospeech|>", "")
            .Replace("<|Speech|>", "")
            .Replace("<|NEUTRAL|>", "")
            .Replace("<|HAPPY|>", "")
            .Replace("<|SAD|>", "")
            .Replace("<|ANGRY|>", "")
            .Replace("<|zh|>", "")
            .Replace("<|en|>", "")
            .Replace("<|yue|>", "")
            .Replace("<|ja|>", "")
            .Replace("<|ko|>", "")
            .Replace("<|Event_UNK|>", "")
            .Trim();
    }

    private static void CleanupHotwordsFile(string? path)
    {
        if (path != null)
        {
            try { File.Delete(path); } catch { }
        }
    }

    public void Dispose() => _recognizer?.Dispose();
}
