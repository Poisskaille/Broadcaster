using System;
using System.Drawing;
using System.Windows.Forms;

namespace Broadcaster
{
    /// <summary>
    /// Échantillonne l'écran affiché dans un Control (8x8 points) pour estimer si le
    /// contenu est globalement noir. Nécessaire car la carte de capture continue
    /// d'envoyer un flux actif rempli de noir en l'absence de signal HDMI — on ne peut
    /// pas détecter "pas de signal" côté Media Foundation sans toucher à la topologie,
    /// donc on observe le résultat final affiché à l'écran, en périphérie.
    /// </summary>
    internal static class ScreenDarknessSampler
    {
        private const int BrightnessThreshold = 14;
        private const double DarkFractionRequired = 0.95;
        private const int GridSize = 8; // 8x8 = 64 points, sur tout l'écran

        /// <summary>
        /// Deux modes distincts : grille complète (précise, capte un logo centré) quand rien
        /// ne recouvre le panel ; bordure seule (évite l'auto-observation) quand l'overlay
        /// "pas de signal" est déjà affiché par-dessus.
        /// </summary>
        public static bool IsControlDark(Control target, bool overlayCurrentlyVisible, int borderThickness = 8)
        {
            if (!target.IsHandleCreated) return false;

            return overlayCurrentlyVisible
                ? IsBorderDark(target, borderThickness)
                : IsFullGridDark(target);
        }

        private static bool IsFullGridDark(Control target)
        {
            Size size = target.ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return false;

            Point screenLocation = target.PointToScreen(Point.Empty);

            using (var snapshot = new Bitmap(size.Width, size.Height))
            {
                using (var g = Graphics.FromImage(snapshot))
                    g.CopyFromScreen(screenLocation, Point.Empty, size);

                int darkCount = 0, totalCount = 0;

                for (int gx = 0; gx < GridSize; gx++)
                {
                    for (int gy = 0; gy < GridSize; gy++)
                    {
                        int x = (int)((gx + 0.5) * size.Width / GridSize);
                        int y = (int)((gy + 0.5) * size.Height / GridSize);
                        if (x < 0 || x >= size.Width || y < 0 || y >= size.Height) continue;

                        Color pixel = snapshot.GetPixel(x, y);
                        int brightness = (pixel.R + pixel.G + pixel.B) / 3;

                        totalCount++;
                        if (brightness <= BrightnessThreshold) darkCount++;
                    }
                }

                return totalCount > 0 && (double)darkCount / totalCount >= DarkFractionRequired;
            }
        }

        private static bool IsBorderDark(Control target, int borderThickness)
        {
            Size size = target.ClientSize;
            if (size.Width <= borderThickness * 2 || size.Height <= borderThickness * 2) return false;

            Point screenLocation = target.PointToScreen(Point.Empty);
            int darkCount = 0, totalCount = 0;

            void SampleStrip(int x, int y, int w, int h, int pointsX, int pointsY)
            {
                if (w <= 0 || h <= 0) return;

                using (var strip = new Bitmap(w, h))
                {
                    using (var g = Graphics.FromImage(strip))
                        g.CopyFromScreen(screenLocation.X + x, screenLocation.Y + y, 0, 0, new Size(w, h));

                    for (int gx = 0; gx < pointsX; gx++)
                    {
                        for (int gy = 0; gy < pointsY; gy++)
                        {
                            int px = Math.Min(w - 1, (int)((gx + 0.5) * w / pointsX));
                            int py = Math.Min(h - 1, (int)((gy + 0.5) * h / pointsY));

                            Color pixel = strip.GetPixel(px, py);
                            int brightness = (pixel.R + pixel.G + pixel.B) / 3;

                            totalCount++;
                            if (brightness <= BrightnessThreshold) darkCount++;
                        }
                    }
                }
            }

            SampleStrip(0, 0, size.Width, borderThickness, 8, 1);
            SampleStrip(0, size.Height - borderThickness, size.Width, borderThickness, 8, 1);
            SampleStrip(0, 0, borderThickness, size.Height, 1, 8);
            SampleStrip(size.Width - borderThickness, 0, borderThickness, size.Height, 1, 8);

            return totalCount > 0 && (double)darkCount / totalCount >= DarkFractionRequired;
        }
    }

    /// <summary>
    /// Machine à état : compte les vérifications sombres consécutives, et ne signale
    /// "plus de signal" qu'après le délai demandé (évite de réagir à un simple fondu noir
    /// entre deux scènes). Le vrai échantillonnage de pixels est délégué à l'extérieur
    /// (via isDarkNow) pour que ce détecteur reste indépendant de comment on capture.
    /// </summary>
    public class NoSignalDetector : IDisposable
    {
        private readonly Timer _timer;
        private readonly Func<bool> _isDarkNow;
        private readonly int _requiredDarkTicks;
        private int _consecutiveDarkTicks;
        private bool _isShowingNoSignal;

        public event Action<bool> NoSignalStateChanged; // true = plus de signal, false = signal revenu

        public NoSignalDetector(Func<bool> isDarkNow, TimeSpan checkInterval, TimeSpan delayBeforeTrigger)
        {
            _isDarkNow = isDarkNow;
            _requiredDarkTicks = Math.Max(1, (int)(delayBeforeTrigger.TotalMilliseconds / checkInterval.TotalMilliseconds));

            _timer = new Timer { Interval = (int)checkInterval.TotalMilliseconds };
            _timer.Tick += (s, e) => CheckOnce();
        }

        public void Start() => _timer.Start();

        public void Stop()
        {
            _timer.Stop();
            _consecutiveDarkTicks = 0;
            _isShowingNoSignal = false;
        }

        private void CheckOnce()
        {
            bool dark;
            try { dark = _isDarkNow(); }
            catch { return; } // capture d'écran indisponible ponctuellement (ex : minimisé) : on ignore ce tick

            if (dark)
            {
                _consecutiveDarkTicks++;
                if (_consecutiveDarkTicks == _requiredDarkTicks && !_isShowingNoSignal)
                {
                    _isShowingNoSignal = true;
                    NoSignalStateChanged?.Invoke(true);
                }
            }
            else
            {
                _consecutiveDarkTicks = 0;
                if (_isShowingNoSignal)
                {
                    _isShowingNoSignal = false;
                    NoSignalStateChanged?.Invoke(false);
                }
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
        }
    }
}