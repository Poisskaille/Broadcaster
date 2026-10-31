using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SharpDX.MediaFoundation;

namespace Broadcaster
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = @"Local\Broadcaster-SingleInstance-3F2A9C1E-7B44-4D0A-9E55-1C8B6A2D4F70";
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [STAThread]
        static void Main()
        {
            using (var mutex = new Mutex(false, SingleInstanceMutexName, out bool createdNew))
            {
                if (!createdNew)
                {
                    BringExistingInstanceToFront();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

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

        private static void BringExistingInstanceToFront()
        {
            using (var current = Process.GetCurrentProcess())
            {
                foreach (var other in Process.GetProcessesByName(current.ProcessName))
                {
                    using (other)
                    {
                        if (other.Id == current.Id) continue;

                        IntPtr handle = other.MainWindowHandle;
                        if (handle == IntPtr.Zero) continue;

                        if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                        SetForegroundWindow(handle);
                        return;
                    }
                }
            }
        }
    }
}