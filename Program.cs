using System.IO;
using System.Windows.Forms;

namespace CantoneseDictation;

static class Program
{
    [STAThread]
    static void Main()
    {
        Directory.CreateDirectory(AppPaths.AppDir);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
