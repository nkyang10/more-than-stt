using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CantoneseDictation;

Console.WriteLine("=== More-Than-STT Unit Tests ===\n");

int passed = 0, failed = 0;

void Assert(bool condition, string name)
{
    if (condition) { Console.WriteLine($"  PASS: {name}"); passed++; }
    else { Console.WriteLine($"  FAIL: {name}"); failed++; }
}

// ──────────────────────────────────────────────
// HotwordManager Tests
// ──────────────────────────────────────────────
Console.WriteLine("--- HotwordManager ---");

// Clean any persisted data from previous runs
try { File.Delete(AppPaths.HotwordFile); } catch { }
try { File.Delete(AppPaths.HistoryFile); } catch { }

var mgr = new HotwordManager();
mgr.Load();
Assert(mgr.Hotwords.Count == 0, "starts empty");

var learned = mgr.LearnFromCorrection("comefi u i", "ComfyUI");
Assert(learned.Count == 1, "learns 1 new word from correction");
Assert(learned[0] == "ComfyUI", "learned word is 'ComfyUI'");
Assert(mgr.Hotwords["ComfyUI"] == 20, "initial weight is 20");
Assert(mgr.HotwordSources["ComfyUI"] == "learned", "source is 'learned'");

learned = mgr.LearnFromCorrection("comefi u i", "ComfyUI");
Assert(learned.Count == 1, "re-learning reinforces weight (+5)");
Assert(learned[0] == "ComfyUI", "re-learned word is still 'ComfyUI'");
Assert(mgr.Hotwords["ComfyUI"] == 25, "weight increased to 25 on re-correction");

mgr.AddManual("StableDiffusion", 50);
Assert(mgr.Hotwords["StableDiffusion"] == 50, "manual add sets correct weight");
Assert(mgr.HotwordSources["StableDiffusion"] == "manual", "manual source is 'manual'");

mgr.AddManual("StableDiffusion", 10);
Assert(mgr.Hotwords["StableDiffusion"] == 50, "lower weight does not decrease");

mgr.Remove("StableDiffusion");
Assert(!mgr.Hotwords.ContainsKey("StableDiffusion"), "remove works");

learned = mgr.LearnFromCorrection("I want the thing", "I want the thing");
Assert(learned.Count == 0, "common/unchanged words are skipped");

var stats = mgr.GetStats();
Assert(stats.totalHotwords == 1, $"stats shows 1 hotword (got {stats.totalHotwords})");
Assert(stats.totalCorrections == 3, $"stats shows 3 corrections (got {stats.totalCorrections})");
Assert(stats.topHotwords.Count > 0, "top hotwords list not empty");

var recent = mgr.GetRecentCorrections(10);
Assert(recent.Count == 3, "recent corrections count is 3");

var hwStr = mgr.GetHotwordString();
Assert(hwStr.Contains("ComfyUI"), "hotword string contains 'ComfyUI'");
Assert(hwStr.Contains("25"), "hotword string contains weight 25");

// ──────────────────────────────────────────────
// Full Transcription Test (requires Sherpa-ONNX model)
// ──────────────────────────────────────────────
Console.WriteLine("\n--- Full Transcription ---");

var modelDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
var sherpaModelDir = Path.Combine(modelDir, "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09");
var modelPath = Path.Combine(sherpaModelDir, "model.int8.onnx");
var testWav = Path.Combine(modelDir, "TestRunner", "test1.mp3");

if (File.Exists(modelPath) && File.Exists(testWav))
{
    Console.WriteLine($"Model: {modelPath}");
    Console.WriteLine($"Test audio: {Path.GetFileName(testWav)}");

    try
    {
        var transcriber = new SenseVoiceEngine(modelDir);
        transcriber.Load();
        Console.WriteLine("Model loaded OK");

        // Transcribe without hotwords
        var result = transcriber.Transcribe(testWav, "yue");
        var hasText = !string.IsNullOrEmpty(result.Text);
        Console.WriteLine($"Transcription: \"{result.Text}\"");
        Console.WriteLine($"Time: {result.TimeSeconds:F3}s");
        Assert(result.TimeSeconds > 0, "transcription time > 0");
        Assert(hasText, "transcription produced text from Cantonese audio");

        // Add hotwords
        mgr.AddManual("Cantonese", 30);
        mgr.AddManual("Dictation", 25);

        // Transcribe WITH hotwords
        var result2 = transcriber.Transcribe(testWav, "yue", mgr.Hotwords);
        Console.WriteLine($"Transcription (with hotwords): \"{result2.Text}\"");
        Assert(result2.TimeSeconds > 0, "transcription with hotwords completes");
        Assert(result2.TimeSeconds <= result.TimeSeconds + 1.0, "hotword biasing adds minimal overhead");

        transcriber.Dispose();
        Console.WriteLine("Pipeline: LOAD ✓ INFERENCE ✓ DECODE ✓ HOTWORDS ✓");
        Console.WriteLine($"Result: \"{result.Text}\"");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL: Transcription error: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"  Stack: {ex.StackTrace?.Split('\n')[0]}");
        failed++;
    }
}
else
{
    Console.WriteLine("  SKIP: Sherpa-ONNX model not found");
    Console.WriteLine($"    model.int8.onnx exists: {File.Exists(modelPath)}");
    Console.WriteLine($"    test1.mp3 exists: {File.Exists(testWav)}");
}

// ──────────────────────────────────────────────
// Logger Tests
// ──────────────────────────────────────────────
Console.WriteLine("\n--- Logger ---");

AppLogger.Info("Test info message");
AppLogger.Warn("Test warning message");
AppLogger.Error("Test error message", new Exception("test exception"));

var logPath = AppLogger.GetLogPath();
Assert(File.Exists(logPath), "log file exists");

var logContent = File.ReadAllText(logPath);
Assert(logContent.Contains("Test info message"), "log contains info");
Assert(logContent.Contains("Test warning message"), "log contains warning");
Assert(logContent.Contains("Test error message"), "log contains error");
Assert(logContent.Contains("test exception"), "log contains exception detail");

// ──────────────────────────────────────────────
// Summary
// ──────────────────────────────────────────────
Console.WriteLine($"\n=== Results: {passed} passed, {failed} failed ===");

Environment.Exit(failed > 0 ? 1 : 0);
