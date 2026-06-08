using System;
using System.IO;

namespace CantoneseDictation;

public static class AppPaths
{
    public static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CantoneseDictation");

    public static readonly string ModelDir = Path.Combine(AppDir, "model_quant.onnx");
    public static readonly string HotwordFile = Path.Combine(AppDir, "hotwords.json");
    public static readonly string HistoryFile = Path.Combine(AppDir, "correction_history.json");
}
