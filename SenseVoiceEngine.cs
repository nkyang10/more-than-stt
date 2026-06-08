using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SherpaOnnx;

namespace CantoneseDictation;

public class SenseVoiceResult
{
    public string Text { get; set; } = "";
    public double TimeSeconds { get; set; }
}

public class SenseVoiceEngine : IDisposable
{
    private readonly string _baseDir;
    private OfflineRecognizer? _recognizer;

    private static readonly char[] TrimChars = ".,!?，。！？、；：\"''（）()「」【】".ToCharArray();
    public static readonly string ModelDirName = "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09";
    private static readonly Regex SenseVoiceTagRegex = new(@"<\|[^|]+\|>", RegexOptions.Compiled);
    private static readonly string[] SupportedLanguages = { "auto", "zh", "en", "yue", "ja", "ko" };

    public SenseVoiceEngine(string baseDir)
    {
        _baseDir = baseDir;
    }

    public void Load()
    {
        var sherpaModelDir = Path.Combine(_baseDir, ModelDirName);
        var modelPath = Path.Combine(sherpaModelDir, "model.int8.onnx");
        var tokensPath = Path.Combine(sherpaModelDir, "tokens.txt");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"ONNX model not found. Download and extract {ModelDirName} from https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models");

        if (!File.Exists(tokensPath))
            throw new FileNotFoundException($"tokens.txt not found next to model");

        AppLogger.Info($"Model: {modelPath}");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig = new FeatureConfig { SampleRate = 16000, FeatureDim = 80 };
        config.ModelConfig = new OfflineModelConfig();
        config.ModelConfig.SenseVoice = new OfflineSenseVoiceModelConfig();
        config.ModelConfig.SenseVoice.Model = modelPath;
        config.ModelConfig.SenseVoice.Language = "auto";
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        config.ModelConfig.Tokens = tokensPath;
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        _recognizer = new OfflineRecognizer(config);
        AppLogger.Info("Recognizer created successfully");
    }

    public SenseVoiceResult Transcribe(string audioPath, string language = "auto",
        IReadOnlyDictionary<string, int>? hotwords = null)
    {
        if (_recognizer == null) throw new InvalidOperationException("Model not loaded");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        AppLogger.Info($"Transcribe: {audioPath}, lang={language}, hotwords={hotwords?.Count ?? 0}");

        // Read audio
        using var audio = new NAudio.Wave.AudioFileReader(audioPath);
        var samples = new List<float>();
        var buf = new float[8192];
        int read;
        while ((read = audio.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i += audio.WaveFormat.Channels)
                samples.Add(buf[i]);

        var srcRate = audio.WaveFormat.SampleRate;
        AppLogger.Info($"Audio: {samples.Count} samples @ {srcRate}Hz ({samples.Count / (double)srcRate:F2}s)");

        if (samples.Count < 1600)
        {
            sw.Stop();
            return new SenseVoiceResult { Text = "", TimeSeconds = sw.Elapsed.TotalSeconds };
        }

        OfflineStream? stream = null;
        try
        {
            stream = _recognizer.CreateStream();
            stream.AcceptWaveform(srcRate, samples.ToArray());
            _recognizer.Decode(stream);

            var text = CleanSenseVoiceText(stream.Result.Text ?? "");

            // Apply hotword post-processing (SenseVoice only supports greedy_search)
            if (hotwords != null && hotwords.Count > 0 && !string.IsNullOrEmpty(text))
                text = ApplyHotwordPostProcessing(text, hotwords);

            sw.Stop();
            AppLogger.Info($"Result ({text.Length} chars): \"{text}\" in {sw.Elapsed.TotalSeconds:F3}s");

            return new SenseVoiceResult
            {
                Text = text,
                TimeSeconds = sw.Elapsed.TotalSeconds
            };
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static string CleanSenseVoiceText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return SenseVoiceTagRegex.Replace(text, "").Trim();
    }

    internal static string ApplyHotwordPostProcessing(string text, IReadOnlyDictionary<string, int> hotwords)
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
                    words[i] = "";
                words[wordStart] = hwClean;
                changed = true;
                continue;
            }

            // Word-by-word matching
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i].Trim(TrimChars);
                if (string.IsNullOrEmpty(w) || w.Length < 2) continue;
                var wLower = w.ToLowerInvariant();

                if (wLower == hwLower || hwLower.Contains(wLower) && hwLower.Length > wLower.Length + 2)
                {
                    words[i] = hwClean;
                    changed = true;
                }
            }
        }

        if (!changed) return text;
        return string.Join(" ", words.Where(w => !string.IsNullOrEmpty(w)).Select(w => w.Trim()));
    }

    public void Dispose() => _recognizer?.Dispose();
}
