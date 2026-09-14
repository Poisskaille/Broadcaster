using System;
using System.Windows.Forms;
using SharpDX.MediaFoundation;

namespace SwitchViewer
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Démarre le runtime Media Foundation pour tout le processus.
            // Doit être appelé une seule fois avant toute utilisation de MF, et fermé à la sortie.
            MediaManager.Startup();

            try
            {
                Application.Run(new MainForm());
            }
            finally
            {
                MediaManager.Shutdown();
            }
        }
    }
}
