using System;
using System.Collections.Generic;
using System.IO;

namespace SwitchViewer
{
    /// <summary>
    /// Configuration persistée entre deux lancements : quel périphérique vidéo/audio utiliser,
    /// et le volume choisi. Stockée en clé=valeur dans un fichier texte simple, pour éviter
    /// toute dépendance supplémentaire (pas besoin de JSON pour 3 valeurs).
    /// </summary>
    public class AppConfig
    {
        public string VideoSymbolicLink { get; set; } = "";
        public string AudioEndpointId { get; set; } = "";
        public string AudioCaptureDeviceName { get; set; } = "";
        public float Volume { get; set; } = 1.0f;
        public int FPS { get; set; } = 60;
        public bool UseColorCorrection { get; set; } = false;
        public int Brightness { get; set; } = 0;
        public int Contrast { get; set; } = 0;
        public int Hue { get; set; } = 0;
        public int Saturation { get; set; } = 0;

        public bool HasVideoDevice => !string.IsNullOrEmpty(VideoSymbolicLink);

        private static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SwitchViewer");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.ini");
            }
        }

        public static AppConfig Load()
        {
            var config = new AppConfig();
            try
            {
                if (!File.Exists(ConfigPath)) return config;

                var values = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(ConfigPath))
                {
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    values[line.Substring(0, idx)] = line.Substring(idx + 1);
                }

                if (values.TryGetValue("VideoSymbolicLink", out var v)) config.VideoSymbolicLink = v;
                if (values.TryGetValue("AudioEndpointId", out var a)) config.AudioEndpointId = a;
                if (values.TryGetValue("AudioCaptureDeviceName", out var ac)) config.AudioCaptureDeviceName = ac;
                if (values.TryGetValue("Volume", out var vol) && float.TryParse(vol, out var f)) config.Volume = f;
                if (values.TryGetValue("FPS", out var fps) && int.TryParse(fps, out var _f)) config.FPS = _f;
            }
            catch
            {
                // Config corrompue ou illisible : on repart sur une config par défaut.
            }
            return config;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(ConfigPath, new[]
                {
                    $"VideoSymbolicLink={VideoSymbolicLink}",
                    $"AudioEndpointId={AudioEndpointId}",
                    $"AudioCaptureDeviceName={AudioCaptureDeviceName}",
                    $"Volume={Volume}",
                    $"FPS={FPS}"
                });
            }
            catch
            {
                // Non bloquant si l'écriture échoue (droits, disque plein, etc.)
            }
        }
    }
}
