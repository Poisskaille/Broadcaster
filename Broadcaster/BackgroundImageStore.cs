using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Broadcaster
{
    public static class BackgroundImageStore
    {
        public static string GetPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SwitchViewer");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "background.png");
        }

        public static bool Exists() => File.Exists(GetPath());

        // Toujours réencodé en PNG, peu importe le format d'origine (jpg, bmp...),
        // pour garder un format cible unique et prévisible.
        public static void SetFrom(string sourceFilePath)
        {
            using (var image = Image.FromFile(sourceFilePath))
                image.Save(GetPath(), ImageFormat.Png);
        }

        public static void Clear()
        {
            try { if (File.Exists(GetPath())) File.Delete(GetPath()); }
            catch { /* Non bloquant. */ }
        }

        public static Image LoadDetachedCopy()
        {
            string path = GetPath();
            if (!File.Exists(path)) return null;

            using (var ms = new MemoryStream(File.ReadAllBytes(path)))
            using (var temp = Image.FromStream(ms))
                return new Bitmap(temp); // copie totalement indépendante du fichier et du stream
        }
    }
}