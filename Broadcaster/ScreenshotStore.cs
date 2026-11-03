using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Broadcaster
{
    /// <summary>
    /// Capture l'image ACTUELLEMENT visible dans le panneau vidéo de Broadcaster.
    /// Avec Media Foundation / EVR, DrawToBitmap() ne capture pas le flux vidéo ;
    /// CopyFromScreen() récupère l'image réellement affichée à l'écran.
    /// NB : une autre fenêtre recouvrant le panneau apparaîtra aussi dans l'image.
    /// </summary>
    public static class ScreenshotStore
    {
        public static string GetFolder()
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures))
                pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");

            string folder = Path.Combine(pictures, "Broadcaster", "Screenshots");
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static string Save(Control videoSurface)
        {
            if (videoSurface == null || !videoSurface.IsHandleCreated ||
                videoSurface.ClientSize.Width <= 0 || videoSurface.ClientSize.Height <= 0)
            {
                throw new InvalidOperationException(Localization.T("Le flux vidéo n’est pas disponible."));
            }

            // Capture le panneau vidéo, pas l'écran entier ni les bordures de fenêtre.
            Size size = videoSurface.ClientSize;
            Point origin = videoSurface.PointToScreen(Point.Empty);

            using (var image = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(image))
                    graphics.CopyFromScreen(origin, Point.Empty, size, CopyPixelOperation.SourceCopy);

                string folder = GetFolder();
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");

                // Horodatage + identifiant unique : les captures précédentes ne sont jamais écrasées.
                string name = "Broadcaster_" + timestamp + "_" +
                              Guid.NewGuid().ToString("N").Substring(0, 8) + ".png";
                string path = Path.Combine(folder, name);

                bool created = false;
                try
                {
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        created = true;
                        image.Save(stream, ImageFormat.Png);
                    }
                    return path;
                }
                catch
                {
                    // Nettoyer uniquement le fichier que CET appel vient de créer.
                    if (created)
                    {
                        try { File.Delete(path); } catch { }
                    }
                    throw;
                }
            }
        }
    }
}
