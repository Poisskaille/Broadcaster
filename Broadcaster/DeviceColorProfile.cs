using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Broadcaster
{
    public class ColorProfile
    {
        public string Name { get; set; }
        public bool IsDefault { get; set; }
        public int Brightness { get; set; }
        public int Contrast { get; set; }
        public int Hue { get; set; }
        public int Saturation { get; set; }

        public ColorProfile Clone(string newName) => new ColorProfile
        {
            Name = newName,
            IsDefault = false,
            Brightness = Brightness,
            Contrast = Contrast,
            Hue = Hue,
            Saturation = Saturation
        };
    }

    public class DeviceColorProfileSet
    {
        public List<ColorProfile> Profiles { get; } = new List<ColorProfile>();
        public string ActiveProfileName { get; set; }

        public ColorProfile DefaultProfile => Profiles.FirstOrDefault(p => p.IsDefault);
        public ColorProfile ActiveProfile => Profiles.FirstOrDefault(p => p.Name == ActiveProfileName) ?? DefaultProfile;
    }

    /// <summary>
    /// Persiste les profils couleur en JSON, un fichier par profil, dans un dossier
    /// dédié à chaque device. Choix volontaire : chaque profil reste un fichier isolé,
    /// consultable/sauvegardable individuellement par l'utilisateur.
    /// </summary>
    public static class DeviceColorProfileStore
    {
        private const string DefaultFileName = "default.json";
        private const string StateFileName = "state.json";
        private const string DeviceInfoFileName = "device.json";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private static string RootFolder =>
            EnsureFolder(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Broadcaster", "ColorProfiles"));

        private static string DeviceFolder(string videoSymbolicLink)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(videoSymbolicLink));
                string hex = string.Concat(hash.Select(b => b.ToString("x2")));
                return EnsureFolder(Path.Combine(RootFolder, hex));
            }
        }

        private static string EnsureFolder(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }

        // Un nom de profil peut contenir des caractères interdits dans un nom de fichier
        // (ex : "Nuit / Extérieur"). On les remplace pour le nom de fichier, tout en
        // gardant le vrai nom lisible DANS le contenu JSON.
        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(clean) ? "profil" : clean;
        }

        private class StateFile { public string ActiveProfileName { get; set; } }
        private class DeviceInfoFile { public string FriendlyName { get; set; } }

        public static DeviceColorProfileSet Load(string videoSymbolicLink)
        {
            if (string.IsNullOrEmpty(videoSymbolicLink)) return null;

            string folder = DeviceFolder(videoSymbolicLink);
            var jsonFiles = Directory.GetFiles(folder, "*.json")
                .Where(f => !Path.GetFileName(f).Equals(StateFileName, StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(f).Equals(DeviceInfoFileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (jsonFiles.Length == 0) return null;

            var set = new DeviceColorProfileSet();
            foreach (var file in jsonFiles)
            {
                var profile = ReadJson<ColorProfile>(file);
                if (profile != null) set.Profiles.Add(profile);
            }

            if (set.Profiles.Count == 0) return null;

            string statePath = Path.Combine(folder, StateFileName);
            set.ActiveProfileName = ReadJson<StateFile>(statePath)?.ActiveProfileName ?? set.DefaultProfile?.Name;
            return set;
        }

        private static T ReadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
            }
            catch
            {
                return null; // Fichier corrompu ou édité à la main de travers : on l'ignore plutôt que de planter.
            }
        }

        /// <summary>
        /// Écrit/actualise le fichier d'UN profil (création, ou mise à jour des valeurs
        /// après "Appliquer"). N'affecte jamais les autres profils du device.
        /// </summary>
        public static void SaveProfile(string videoSymbolicLink, ColorProfile profile)
        {
            if (string.IsNullOrEmpty(videoSymbolicLink) || profile == null) return;

            try
            {
                string folder = DeviceFolder(videoSymbolicLink);
                string fileName = profile.IsDefault ? DefaultFileName : SanitizeFileName(profile.Name) + ".json";
                File.WriteAllText(Path.Combine(folder, fileName), JsonSerializer.Serialize(profile, JsonOptions));
            }
            catch { /* Non bloquant. */ }
        }

        /// <summary>
        /// Supprime le fichier d'un profil. Ignore silencieusement toute tentative sur
        /// "Par défaut" (double sécurité, en plus du bouton déjà désactivé côté UI).
        /// </summary>
        public static void DeleteProfile(string videoSymbolicLink, ColorProfile profile)
        {
            if (string.IsNullOrEmpty(videoSymbolicLink) || profile == null || profile.IsDefault) return;

            try
            {
                string path = Path.Combine(DeviceFolder(videoSymbolicLink), SanitizeFileName(profile.Name) + ".json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* Non bloquant. */ }
        }

        /// <summary>
        /// Renomme le fichier d'un profil pour suivre son nouveau nom, sinon on se
        /// retrouve avec un fichier fantôme sous l'ancien nom en plus du nouveau.
        /// </summary>
        public static void RenameProfile(string videoSymbolicLink, string oldName, ColorProfile renamedProfile)
        {
            if (string.IsNullOrEmpty(videoSymbolicLink) || renamedProfile == null || renamedProfile.IsDefault) return;

            string oldPath = Path.Combine(DeviceFolder(videoSymbolicLink), SanitizeFileName(oldName) + ".json");
            try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }

            SaveProfile(videoSymbolicLink, renamedProfile);
        }

        public static void SetActiveProfile(string videoSymbolicLink, string profileName)
        {
            if (string.IsNullOrEmpty(videoSymbolicLink)) return;

            try
            {
                string path = Path.Combine(DeviceFolder(videoSymbolicLink), StateFileName);
                File.WriteAllText(path, JsonSerializer.Serialize(new StateFile { ActiveProfileName = profileName }, JsonOptions));
            }
            catch { /* Non bloquant. */ }
        }

        /// <summary>
        /// Si "default.json" n'existe pas encore pour ce device : capture l'état matériel
        /// ACTUEL et l'écrit comme profil "Par défaut", une seule fois, jamais réécrit
        /// ensuite. Si le fichier existe déjà, on n'y touche surtout pas.
        /// </summary>
        public static DeviceColorProfileSet EnsureFactoryProfileExists(string videoSymbolicLink, string friendlyName, ColorProfile currentHardwareValues)
        {
            string folder = DeviceFolder(videoSymbolicLink);
            string defaultPath = Path.Combine(folder, DefaultFileName);

            if (!File.Exists(defaultPath))
            {
                var factory = currentHardwareValues.Clone("Par défaut");
                factory.IsDefault = true;
                SaveProfile(videoSymbolicLink, factory);
                SetActiveProfile(videoSymbolicLink, factory.Name);

                try
                {
                    File.WriteAllText(Path.Combine(folder, DeviceInfoFileName),
                        JsonSerializer.Serialize(new DeviceInfoFile { FriendlyName = friendlyName }, JsonOptions));
                }
                catch { /* purement informatif, non bloquant */ }
            }

            return Load(videoSymbolicLink);
        }
    }
}