using System;
using System.IO;
using System.Text;

namespace Broadcaster
{
    [Flags]
    internal enum DebugLogFlags
    {
        None = 0,
        CaptureSession = 1,
        FrameGeneration = 2,
        DiscordPresence = 4,
    }

    internal static class FrameGenerationDebugLog
    {
        private static readonly object Sync = new object();
        private static string _cachedPath;
        private static DebugLogFlags _enabled = DebugLogFlags.None;

        public static string LogPath
        {
            get
            {
                if (!string.IsNullOrEmpty(_cachedPath)) return _cachedPath;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Broadcaster");
                Directory.CreateDirectory(dir);
                _cachedPath = Path.Combine(dir, "frame-generation.log");
                return _cachedPath;
            }
        }

        public static void ApplySettings(AppConfig config)
        {
            DebugLogFlags flags = DebugLogFlags.None;
            if (config != null)
            {
                if (config.DebugLogCaptureSession) flags |= DebugLogFlags.CaptureSession;
                if (config.DebugLogFrameGeneration) flags |= DebugLogFlags.FrameGeneration;
                if (config.DebugLogDiscordPresence) flags |= DebugLogFlags.DiscordPresence;
            }
            _enabled = flags;
        }

        public static bool IsEnabled(DebugLogFlags flag) => (_enabled & flag) != 0;

        public static void StartSession(DebugLogFlags category, string title)
        {
            if (!IsEnabled(category)) return;
            lock (Sync)
            {
                try
                {
                    RotateIfNeeded();
                    File.AppendAllText(LogPath,
                        $"\r\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {title} [{category}] =====\r\n",
                        Encoding.UTF8);
                }
                catch { }
            }
        }

        public static void Log(DebugLogFlags category, string message)
        {
            if (!IsEnabled(category)) return;
            lock (Sync)
            {
                try
                {
                    RotateIfNeeded();
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}\r\n",
                        Encoding.UTF8);
                }
                catch { }
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var path = LogPath;
                if (!File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length < 512 * 1024) return;
                string backup = path + ".bak";
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                File.Move(path, backup);
            }
            catch { }
        }
    }
}
