using System;
using System.Windows.Forms;

namespace AppleCinemaBrightness;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
