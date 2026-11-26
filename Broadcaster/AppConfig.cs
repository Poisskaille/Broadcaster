using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        // 0 = best native rate at chosen resolution; otherwise closest native rate.
        public int CaptureTargetFps { get; set; } = 0;
        public bool PreviewVsync { get; set; } = false; // can cap preview to monitor refresh
        public bool ShowDebugOverlay { get; set; } = false; // Extras : panneau diagnostic désactivé par défaut
        public bool PreventSleepWhileCapturing { get; set; } = true;
        public bool DebugLogCaptureSession { get; set; } = false;
        public bool DebugLogFrameGeneration { get; set; } = false;
        public bool DebugLogDiscordPresence { get; set; } = false;
        // Off / X2 / X3 / X4 / X5. Does not modify HDMI capture FPS.
        public string FrameInterpolation { get; set; } = "Off";

        // Keep saved mode strings stable across all eight UI languages. Old X2
        // configurations remain valid; any unknown value safely becomes Off.
        public static int InterpolationFactor(string value)
        {
            switch ((value ?? "").ToUpperInvariant())
            {
                case "X2": return 2;
                case "X3": return 3;
                case "X4": return 4;
                case "X5": return 5;
                default: return 1;
            }
        }


        public DiscordPresenceSettings DiscordPresence { get; set; } = new DiscordPresenceSettings();
        public string DisplayDeviceName { get; set; } = "";
        public KeyBindings Bindings { get; set; } = new KeyBindings();
        public bool AutoReloadOnStart { get; set; } = true;
        public bool HasVideoDevice => !string.IsNullOrEmpty(VideoSymbolicLink);
        public bool NoSignalDetectionEnabled { get; set; } = true;
        public bool ClipsEnabled { get; set; } = false;
        public int ClipDurationSeconds { get; set; } = 30;
        public int ClipTargetFps { get; set; } = 30;
        public int ClipJpegQuality { get; set; } = 85;
        // auto = langue Windows (anglais si non prise en charge); sinon code de langue explicite.
        public string Language { get; set; } = "auto";

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

        private static readonly object ConfigSync = new object();
        private static string DiscordPath => Path.Combine(Path.GetDirectoryName(ConfigPath), "discord-presence.ini");

        // Lecture avec repli sur la sauvegarde precedente (ecriture atomique).
        private static Dictionary<string, string> ReadIni(string path)
        {
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string line in File.ReadAllLines(candidate, Encoding.UTF8))
                    {
                        int index = line.IndexOf('=');
                        if (index > 0) result[line.Substring(0, index).Trim()] = line.Substring(index + 1);
                    }
                    // Une ecriture interrompue ne doit pas remplacer une config valide.
                    if (result.Count > 0) return result;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void WriteAtomic(string path, Dictionary<string, string> values)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp,
                    values.Select(entry => entry.Key + "=" + SafeIni(entry.Value)).ToArray(),
                    new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temp, path, path + ".bak", true);
                else
                    File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }

        private static void PutDiscord(Dictionary<string, string> values, DiscordPresenceSettings settings)
        {
            settings = (settings ?? new DiscordPresenceSettings()).Clone();
            settings.Normalize();
            values["DiscordEnabled"] = settings.Enabled.ToString();
            values["DiscordApplicationId"] = SafeIni(settings.ApplicationId);
            values["DiscordMode"] = SafeIni(settings.Mode);
            values["DiscordPlatform"] = SafeIni(settings.Platform);
            values["DiscordManualGame"] = SafeIni(settings.ManualGame);
            values["DiscordImageKey"] = SafeIni(settings.ImageKey);
            values["DiscordNintendoPresenceUrl"] = SafeIni(settings.NintendoPresenceUrl);
            values["DiscordShowElapsed"] = settings.ShowElapsed.ToString();
        }

        // Sauvegarde immediate depuis le dialogue Discord. Le reste des options
        // n'a pas besoin d'etre valide pour retenir le jeu et l'Application ID.
        public static bool TrySaveDiscordPresence(DiscordPresenceSettings settings, out string error)
        {
            lock (ConfigSync)
            {
                try
                {
                    var sidecar = ReadIni(DiscordPath);
                    PutDiscord(sidecar, settings);
                    WriteAtomic(DiscordPath, sidecar);
                    // Retrocompatibilite : conserver aussi les cles dans config.ini.
                    // Le fichier dedie est la source de verite. Un echec du
                    // miroir legacy ne doit pas annuler la sauvegarde reussie.
                    try
                    {
                        var main = ReadIni(ConfigPath);
                        PutDiscord(main, settings);
                        WriteAtomic(ConfigPath, main);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    error = null;
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        public static AppConfig Load()
        {
            var config = new AppConfig();
            try
            {
                var values = ReadIni(ConfigPath);
                // Les preferences Discord vivent aussi dans leur propre fichier.
                // Elles survivent ainsi aux versions anciennes qui re-ecrivent config.ini.
                var discordValues = ReadIni(DiscordPath);
                foreach (var item in discordValues)
                    if (item.Key.StartsWith("Discord", StringComparison.OrdinalIgnoreCase))
                        values[item.Key] = item.Value;

                if (values.TryGetValue("VideoSymbolicLink", out var v)) config.VideoSymbolicLink = v;
                if (values.TryGetValue("AudioEndpointId", out var a)) config.AudioEndpointId = a;
                if (values.TryGetValue("AudioCaptureDeviceName", out var ac)) config.AudioCaptureDeviceName = ac;
                if (values.TryGetValue("Volume", out var vol) &&
                    (float.TryParse(vol, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var f) ||
                     float.TryParse(vol, out f))) config.Volume = f;
                if (values.TryGetValue("Width", out var w) && int.TryParse(w, out var _w)) config.Width = _w;
                if (values.TryGetValue("Height", out var h) && int.TryParse(h, out var _h)) config.Height = _h;
                if (values.TryGetValue("CaptureTargetFps", out var captureFps) && int.TryParse(captureFps, out var desired))
                    config.CaptureTargetFps = desired >= 0 && desired <= 240 ? desired : 0;
                if (values.TryGetValue("PreviewVsync", out var vsyncValue) && bool.TryParse(vsyncValue, out var vsync))
                    config.PreviewVsync = vsync;
                if (values.TryGetValue("ShowDebugOverlay", out var overlayValue) && bool.TryParse(overlayValue, out var overlay))
                    config.ShowDebugOverlay = overlay;
                if (values.TryGetValue("PreventSleepWhileCapturing", out var awakeValue) && bool.TryParse(awakeValue, out var awake))
                    config.PreventSleepWhileCapturing = awake;
                if (values.TryGetValue("DebugLogCaptureSession", out var debugCapture) && bool.TryParse(debugCapture, out var captureLog))
                    config.DebugLogCaptureSession = captureLog;
                if (values.TryGetValue("DebugLogFrameGeneration", out var debugFrameGen) && bool.TryParse(debugFrameGen, out var frameLog))
                    config.DebugLogFrameGeneration = frameLog;
                if (values.TryGetValue("DebugLogDiscordPresence", out var debugDiscord) && bool.TryParse(debugDiscord, out var discordLog))
                    config.DebugLogDiscordPresence = discordLog;
                if (values.TryGetValue("FrameInterpolation", out var interpolation))
                    config.FrameInterpolation = InterpolationFactor(interpolation) > 1 ? "X" + InterpolationFactor(interpolation) : "Off";
                if (values.TryGetValue("DiscordEnabled", out var dpEnable) && bool.TryParse(dpEnable, out var dpOn)) config.DiscordPresence.Enabled = dpOn;
                if (values.TryGetValue("DiscordApplicationId", out var dpApp)) config.DiscordPresence.ApplicationId = dpApp;
                if (values.TryGetValue("DiscordMode", out var dpMode)) config.DiscordPresence.Mode = dpMode;
                if (values.TryGetValue("DiscordPlatform", out var dpPlatform)) config.DiscordPresence.Platform = dpPlatform;
                if (values.TryGetValue("DiscordManualGame", out var dpGame)) config.DiscordPresence.ManualGame = dpGame;
                if (values.TryGetValue("DiscordImageKey", out var dpImg)) config.DiscordPresence.ImageKey = dpImg;
                if (values.TryGetValue("DiscordNintendoPresenceUrl", out var dpUrl)) config.DiscordPresence.NintendoPresenceUrl = dpUrl;
                if (values.TryGetValue("DiscordShowElapsed", out var dpElapsed) && bool.TryParse(dpElapsed, out var dpElapsedOn)) config.DiscordPresence.ShowElapsed = dpElapsedOn;
                config.DiscordPresence.Normalize();
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

                if (values.TryGetValue("Language", out var lang))
                    config.Language = Localization.NormalizeMode(lang);

                if (values.TryGetValue("ClipsEnabled", out var ce) && bool.TryParse(ce, out var enabled))
                    config.ClipsEnabled = enabled;
                if (values.TryGetValue("ClipDurationSeconds", out var cds) && int.TryParse(cds, out var secs))
                    config.ClipDurationSeconds = Math.Max(5, Math.Min(120, secs));
                if (values.TryGetValue("ClipTargetFps", out var cfps) && int.TryParse(cfps, out var fps))
                    config.ClipTargetFps = Math.Max(10, Math.Min(60, fps));
                if (values.TryGetValue("ClipJpegQuality", out var cq) && int.TryParse(cq, out var q))
                    config.ClipJpegQuality = Math.Max(60, Math.Min(95, q));

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

        // Configuration uses newline-delimited INI, so never let user-provided text add keys.
        private static string SafeIni(string value) => (value ?? "").Replace("\r", " ").Replace("\n", " ");

        // Tous les champs sont ecrits atomiquement, avec une version .bak.
        // Ne pas ecraser les cles inconnues : une mise a jour / retrogradation
        // ne doit pas effacer les preferences des fonctionnalites plus recentes.
        public bool TrySave(out string error)
        {
            lock (ConfigSync)
            {
                try
                {
                    var values = ReadIni(ConfigPath);
                    void Put(string key, object value) => values[key] = SafeIni(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                    Put("VideoSymbolicLink", VideoSymbolicLink);
                    Put("AudioEndpointId", AudioEndpointId);
                    Put("AudioCaptureDeviceName", AudioCaptureDeviceName);
                    Put("Volume", Volume);
                    Put("Width", Width);
                    Put("Height", Height);
                    Put("CaptureTargetFps", CaptureTargetFps);
                    Put("PreviewVsync", PreviewVsync);
                    Put("ShowDebugOverlay", ShowDebugOverlay);
                    Put("PreventSleepWhileCapturing", PreventSleepWhileCapturing);
                    Put("DebugLogCaptureSession", DebugLogCaptureSession);
                    Put("DebugLogFrameGeneration", DebugLogFrameGeneration);
                    Put("DebugLogDiscordPresence", DebugLogDiscordPresence);
                    Put("FrameInterpolation", FrameInterpolation);
                    PutDiscord(values, DiscordPresence);
                    Put("DisplayDeviceName", DisplayDeviceName);
                    Put("NoSignalDetectionEnabled", NoSignalDetectionEnabled);
                    Put("Key_OpenMenu", Bindings.OpenMenu);
                    Put("Key_Reload", Bindings.Reload);
                    Put("Key_ToggleCursor", Bindings.ToggleCursor);
                    Put("Key_Screenshot", Bindings.Screenshot);
                    Put("Key_Clip", Bindings.Clip);
                    Put("ClipsEnabled", ClipsEnabled);
                    Put("ClipDurationSeconds", ClipDurationSeconds);
                    Put("ClipTargetFps", ClipTargetFps);
                    Put("ClipJpegQuality", ClipJpegQuality);
                    Put("Language", Localization.NormalizeMode(Language));
                    Put("AutoReloadOnStart", AutoReloadOnStart);
                    // Ecrire le fichier independant en premier : s'il reste valide,
                    // un probleme d'ecriture du fichier global ne perd pas Discord.
                    var discord = ReadIni(DiscordPath);
                    PutDiscord(discord, DiscordPresence);
                    WriteAtomic(DiscordPath, discord);
                    WriteAtomic(ConfigPath, values);
                    error = null;
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        public void Save()
        {
            if (!TrySave(out string error))
                throw new IOException("Broadcaster: impossible d'enregistrer les options : " + error);
        }
    }
}
