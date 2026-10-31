using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace Broadcaster
{
    public class AppConfig
    {
        public string VideoSymbolicLink { get; set; } = "";
        public string AudioEndpointId { get; set; } = "";
        public string AudioCaptureDeviceName { get; set; } = "";
        public float Volume { get; set; } = 1.0f;
        public bool UseColorCorrection { get; set; } = false;
        public int Brightness { get; set; } = 0;
        public int Contrast { get; set; } = 0;
        public int Hue { get; set; } = 0;
        public int Saturation { get; set; } = 0;

        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;

        public string DisplayDeviceName { get; set; } = "";
        public KeyBindings Bindings { get; set; } = new KeyBindings();
        public bool AutoReloadOnStart { get; set; } = true;
        public bool HasVideoDevice => !string.IsNullOrEmpty(VideoSymbolicLink);
        public bool NoSignalDetectionEnabled { get; set; } = true;
        public bool ClipsEnabled { get; set; } = false;
        public int ClipDurationSeconds { get; set; } = 30;
        public int ClipTargetFps { get; set; } = 30;

        private static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Broadcaster");
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
                if (values.TryGetValue("Width", out var w) && int.TryParse(w, out var _w)) config.Width = _w;
                if (values.TryGetValue("Height", out var h) && int.TryParse(h, out var _h)) config.Height = _h;
                if (values.TryGetValue("DisplayDeviceName", out var dn)) config.DisplayDeviceName = dn;
                if (values.TryGetValue("NoSignalDetectionEnabled", out var nsd) && bool.TryParse(nsd, out var _nsd)) config.NoSignalDetectionEnabled = _nsd;

                config.Bindings.OpenMenu = ParseKey(values, "Key_OpenMenu", config.Bindings.OpenMenu);
                config.Bindings.Reload = ParseKey(values, "Key_Reload", config.Bindings.Reload);
                config.Bindings.ToggleCursor = ParseKey(values, "Key_ToggleCursor", config.Bindings.ToggleCursor);
                config.Bindings.Screenshot = ParseKey(values, "Key_Screenshot", config.Bindings.Screenshot);
                // On conserve les anciens raccourcis, puis on choisit une touche libre pour les clips.
                config.Bindings.Clip = ParseKey(values, "Key_Clip", config.Bindings.Clip);
                if (!values.ContainsKey("Key_Screenshot") ||
                    config.Bindings.Screenshot == config.Bindings.OpenMenu ||
                    config.Bindings.Screenshot == config.Bindings.Reload ||
                    config.Bindings.Screenshot == config.Bindings.ToggleCursor ||
                    config.Bindings.Screenshot == KeyBindings.ReservedKey)
                {
                    config.Bindings.Screenshot = KeyBindings.ChooseScreenshotKey(config.Bindings);
                }
                if (config.Bindings.Clip == KeyBindings.ReservedKey ||
                    config.Bindings.Clip == config.Bindings.OpenMenu ||
                    config.Bindings.Clip == config.Bindings.Reload ||
                    config.Bindings.Clip == config.Bindings.ToggleCursor ||
                    config.Bindings.Clip == config.Bindings.Screenshot)
                    config.Bindings.Clip = KeyBindings.ChooseClipKey(config.Bindings);

                if (values.TryGetValue("ClipsEnabled", out var ce) && bool.TryParse(ce, out var enabled))
                    config.ClipsEnabled = enabled;
                if (values.TryGetValue("ClipDurationSeconds", out var cds) && int.TryParse(cds, out var secs))
                    config.ClipDurationSeconds = Math.Max(5, Math.Min(120, secs));
                if (values.TryGetValue("ClipTargetFps", out var cfps) && int.TryParse(cfps, out var fps))
                    config.ClipTargetFps = Math.Max(10, Math.Min(60, fps));

                if (!config.Bindings.IsValid()) config.Bindings = new KeyBindings(); // garde-fou anti-enfermement

                if (values.TryGetValue("AutoReloadOnStart", out var arl) && bool.TryParse(arl, out var _arl)) config.AutoReloadOnStart = _arl;
            }
            catch
            {
                
            }
            return config;
        }

        private static Keys ParseKey(Dictionary<string, string> values, string name, Keys fallback)
        {
            if (values.TryGetValue(name, out var raw) && Enum.TryParse<Keys>(raw, out var parsed) && parsed != Keys.None)
                return parsed;
            return fallback;
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
                    $"Width={Width}",
                    $"Height={Height}",
                    $"DisplayDeviceName={DisplayDeviceName}",
                    $"NoSignalDetectionEnabled={NoSignalDetectionEnabled}",
                    $"Key_OpenMenu={Bindings.OpenMenu}",
                    $"Key_Reload={Bindings.Reload}",
                    $"Key_ToggleCursor={Bindings.ToggleCursor}",
                    $"Key_Screenshot={Bindings.Screenshot}",
                    $"Key_Clip={Bindings.Clip}",
                    $"ClipsEnabled={ClipsEnabled}",
                    $"ClipDurationSeconds={ClipDurationSeconds}",
                    $"ClipTargetFps={ClipTargetFps}",
                    $"AutoReloadOnStart={AutoReloadOnStart}"
                });
            }
            catch
            {
                // Non bloquant si l'écriture échoue (droits, disque plein, etc.)
            }
        }
    }
}
