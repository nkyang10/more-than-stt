using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CantoneseDictation;

public class CorrectionEntry
{
    public string original { get; set; } = "";
    public string corrected { get; set; } = "";
    public List<string> learned_words { get; set; } = new();
    public string timestamp { get; set; } = "";
}

public class HotwordManager
{
    private Dictionary<string, int> _hotwords = new();
    private List<CorrectionEntry> _corrections = new();

    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","is","are","was","of","in","to","for","it",
        "我","你","佢","係","嘅","咗","個","有","唔","會","都"
    };

    private static readonly char[] trimChars = ".,!?，。！？、；：\"''（）()「」【】".ToCharArray();

    public IReadOnlyDictionary<string, int> Hotwords => _hotwords;

    public void Load()
    {
        if (File.Exists(Program.HotwordFile))
        {
            var json = File.ReadAllText(Program.HotwordFile);
            var data = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (data != null && data.TryGetValue("hotwords", out var hw))
            {
                _hotwords = JsonSerializer.Deserialize<Dictionary<string, int>>(hw.ToString()!) ?? new();
            }
        }

        if (File.Exists(Program.HistoryFile))
        {
            var json = File.ReadAllText(Program.HistoryFile);
            _corrections = JsonSerializer.Deserialize<List<CorrectionEntry>>(json) ?? new();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(Program.HotwordFile);
        if (dir != null) Directory.CreateDirectory(dir);

        File.WriteAllText(Program.HotwordFile,
            JsonSerializer.Serialize(new { hotwords = _hotwords }, new JsonSerializerOptions { WriteIndented = true }));

        // Keep last 500
        if (_corrections.Count > 500)
            _corrections = _corrections.TakeLast(500).ToList();

        File.WriteAllText(Program.HistoryFile,
            JsonSerializer.Serialize(_corrections, new JsonSerializerOptions { WriteIndented = true }));
    }

    public List<string> LearnFromCorrection(string asrText, string correctedText)
    {
        var asrWords = new HashSet<string>(asrText.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var correctWords = new HashSet<string>(correctedText.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var newWords = new List<string>();
        foreach (var word in correctWords)
        {
            var clean = word.Trim(trimChars);
            if (clean.Length < 2) continue;
            if (CommonWords.Contains(clean)) continue;
            if (asrWords.Contains(clean)) continue;

            if (_hotwords.ContainsKey(clean))
                _hotwords[clean] += 5;
            else
                _hotwords[clean] = 20;

            newWords.Add(clean);
        }

        _corrections.Add(new CorrectionEntry
        {
            original = asrText,
            corrected = correctedText,
            learned_words = newWords,
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });

        Save();
        return newWords;
    }

    public string GetHotwordString()
    {
        return string.Join(" ", _hotwords.OrderByDescending(kv => kv.Value)
                                         .Select(kv => $"{kv.Key} {kv.Value}"));
    }

    public void AddManual(string word, int weight = 20)
    {
        if (_hotwords.ContainsKey(word))
            _hotwords[word] = Math.Max(_hotwords[word], weight);
        else
            _hotwords[word] = weight;
        Save();
    }

    public void Remove(string word)
    {
        _hotwords.Remove(word);
        Save();
    }

    public List<CorrectionEntry> GetRecentCorrections(int count = 50)
    {
        return _corrections.TakeLast(count).ToList();
    }

    public (int totalHotwords, int totalCorrections, List<(string, int)> topHotwords) GetStats()
    {
        return (
            _hotwords.Count,
            _corrections.Count,
            _hotwords.OrderByDescending(kv => kv.Value).Take(10)
                     .Select(kv => (kv.Key, kv.Value)).ToList()
        );
    }
}
