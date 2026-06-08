using System;
using System.IO;
using System.Windows.Forms;

namespace CantoneseDictation;

static class Program
{
    public static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CantoneseDictation");

    public static readonly string ModelDir = Path.Combine(AppDir, "model_quant.onnx");
    public static readonly string HotwordFile = Path.Combine(AppDir, "hotwords.json");
    public static readonly string HistoryFile = Path.Combine(AppDir, "correction_history.json");

    [STAThread]
    static void Main()
    {
        Directory.CreateDirectory(AppDir);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
