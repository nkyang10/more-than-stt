#r "CantoneseDictation.dll"

using CantoneseDictation;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

// ──────────────────────────────────────────────
// Test HotwordManager
// ──────────────────────────────────────────────

var appDir = Path.Combine(Path.GetTempPath(), "CantoneseDictation_Test_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(appDir);

// Override Program paths via reflection
var progType = typeof(Program);

Console.WriteLine("=== HotwordManager Tests ===");

// 1. Basic learning
var mgr = new HotwordManager();
mgr.Load(); // no files yet, should be empty
Console.WriteLine($"Empty: hotwords={mgr.Hotwords.Count}, stats={mgr.GetStats().totalHotwords}");

var learned = mgr.LearnFromCorrection("comefi u i", "ComfyUI");
Console.WriteLine($"Learned: [{string.Join(", ", learned)}]");
Console.Assert(learned.Count == 1, "Should have learned 1 word");
Console.Assert(learned[0] == "ComfyUI", "Should be ComfyUI");
Console.Assert(mgr.Hotwords["ComfyUI"] == 20, "Initial weight should be 20");

// 2. Learning again reinforces
learned = mgr.LearnFromCorrection("comefi u i", "ComfyUI");
Console.Assert(mgr.Hotwords["ComfyUI"] == 25, "Weight should increase to 25 after 2nd correction");
Console.WriteLine($"Weight after re-learn: {mgr.Hotwords["ComfyUI"]}");

// 3. Manual add
mgr.AddManual("StableDiffusion", 50);
Console.Assert(mgr.Hotwords["StableDiffusion"] == 50, "Manual weight should be 50");
Console.Assert(mgr.HotwordSources["StableDiffusion"] == "manual", "Source should be manual");

// 4. Manual add with lower weight doesn't decrease
mgr.AddManual("StableDiffusion", 10);
Console.Assert(mgr.Hotwords["StableDiffusion"] == 50, "Lower weight should not decrease");

// 5. Remove
mgr.Remove("StableDiffusion");
Console.Assert(!mgr.Hotwords.ContainsKey("StableDiffusion"), "Should be removed");

// 6. Common words are skipped
learned = mgr.LearnFromCorrection("I want the thing", "I want the thing");
Console.Assert(learned.Count == 0, "Common words should be skipped");
Console.WriteLine("Common words filter: OK");

// 7. Hotword string
var hwStr = mgr.GetHotwordString();
Console.WriteLine($"Hotword string: {hwStr}");

// 8. Stats
var stats = mgr.GetStats();
Console.WriteLine($"Stats: hotwords={stats.totalHotwords}, corrections={stats.totalCorrections}, top={stats.topHotwords.Count}");
Console.Assert(stats.totalHotwords == 1, "Should have 1 hotword");

// 9. Persistence test via temp files
var tmpHotwordFile = Path.Combine(appDir, "hotwords.json");
var tmpHistoryFile = Path.Combine(appDir, "correction_history.json");

// We need to redirect Program paths - use reflection
var appDirField = progType.GetField("AppDir", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
Console.WriteLine($"AppDir: {appDirField?.GetValue(null)}");

// Since Program paths are readonly, test Save/Load via the internal serialization
// The paths are baked into Program.cs, so for a real test we'd create files there.
// But we can assert Save writes without error:
mgr.Save();
Console.WriteLine("Save() completed without error");

// 10. Recent corrections
var recent = mgr.GetRecentCorrections(10);
Console.Assert(recent.Count == 2, $"Should have 2 corrections, got {recent.Count}");
Console.WriteLine($"Recent corrections: {recent.Count}");

Console.WriteLine();
Console.WriteLine("=== All HotwordManager tests passed! ===");
Console.WriteLine();

// ──────────────────────────────────────────────
// Test hotword processing logic (via reflection)
// ──────────────────────────────────────────────

Console.WriteLine("=== SenseVoiceEngine Hotword Logic Tests ===");
var engineType = typeof(SenseVoiceEngine);

var applyMethod = engineType.GetMethod("ApplyHotwordPostProcessing",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

if (applyMethod != null)
{
    // Create engine instance (won't load model, just for method access)
    var engine = Activator.CreateInstance(engineType, appDir);
    var hotwords = new Dictionary<string, int> { ["ComfyUI"] = 20, ["Photoshop"] = 15 };

    // Test direct match
    var result = (string)applyMethod.Invoke(engine, new object[] { "hello comfyui world", hotwords })!;
    Console.WriteLine($"Direct match: \"hello comfyui world\" -> \"{result}\"");
    Console.Assert(result == "hello ComfyUI world", $"Expected 'hello ComfyUI world', got '{result}'");

    // Test no-match (text unchanged)
    result = (string)applyMethod.Invoke(engine, new object[] { "hello world nothing here", hotwords })!;
    Console.WriteLine($"No match: \"hello world nothing here\" -> \"{result}\"");
    Console.Assert(result == "hello world nothing here", "Should be unchanged");

    // Test multi-word phrase match
    result = (string)applyMethod.Invoke(engine, new object[] { "use adobe photoshop to edit", new Dictionary<string, int> { { "Adobe Photoshop", 20 } } })!;
    Console.WriteLine($"Phrase match: \"use adobe photoshop to edit\" -> \"{result}\"");

    Console.WriteLine("Hotword processing logic: OK");
}
else
{
    Console.WriteLine("ApplyHotwordPostProcessing method not accessible");
}

Console.WriteLine();
Console.WriteLine("=== All tests completed! ===");
