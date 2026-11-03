using System;
using System.Drawing;
using System.Windows.Forms;

namespace Broadcaster
{
    /// <summary>
    /// Bandeau discret et horizontal en haut de la zone vidéo. Fenêtre native
    /// indépendante du rendu EVR/DXGI : aucun effet sur les images capturées.
    /// </summary>
    internal sealed class DebugPerformanceOverlay : Form
    {
        public const int BandHeight = 32;
        private readonly Font _brandFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        private readonly Font _metricFont = new Font("Segoe UI", 9f, FontStyle.Regular);
        private readonly Font _valueFont = new Font("Segoe UI", 9f, FontStyle.Bold);

        private string _cpu = Localization.T("n/d");
        private string _gpu = Localization.T("n/d");
        private string _capturedFps = Localization.T("n/d");
        private string _presentedFps = Localization.T("n/d");
        private string _bitrate = Localization.T("n/d");
        private string _mode = Localization.T("Aucune source");

        private static readonly Color LabelColor = Color.FromArgb(186, 194, 204);
        private static readonly Color ValueColor = Color.White;
        private static readonly Color AccentColor = Color.FromArgb(143, 206, 238);
        private static readonly Color SeparatorColor = Color.FromArgb(111, 120, 132);

        public DebugPerformanceOverlay()
        {
            // La largeur est adaptée à la fenêtre de capture par MainForm.
            Width = 960;
            Height = BandHeight;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(45, 48, 53);
            ForeColor = Color.White;
            Opacity = 0.83;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20;       // WS_EX_TRANSPARENT : clics vers la vidéo
                cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW : pas dans Alt+Tab
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE : ne prend pas le focus
                return cp;
            }
        }

        public void SetMetrics(string cpu, string gpu, string capturedFps,
            string presentedFps, string bitrate, string mode)
        {
            if (IsDisposed) return;
            _cpu = cpu ?? Localization.T("n/d");
            _gpu = gpu ?? Localization.T("n/d");
            _capturedFps = capturedFps ?? Localization.T("n/d");
            _presentedFps = presentedFps ?? Localization.T("n/d");
            _bitrate = bitrate ?? Localization.T("n/d");
            _mode = mode ?? Localization.T("Aucune source");
            Invalidate();
        }

        private static readonly TextFormatFlags TextFlags =
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        private int TextWidth(Graphics g, string text, Font font)
        {
            return TextRenderer.MeasureText(g, text, font, new Size(16384, Height), TextFlags).Width;
        }

        private int DrawText(Graphics g, string text, Font font, Color color, int x)
        {
            int width = TextWidth(g, text, font);
            TextRenderer.DrawText(g, text, font,
                new Rectangle(x, 0, width + 2, Height - 1), color,
                TextFlags | TextFormatFlags.VerticalCenter);
            return x + width;
        }

        private static void Separator(Graphics g, int x, int height)
        {
            using (var pen = new Pen(SeparatorColor))
                g.DrawLine(pen, x, 9, x, height - 10);
        }

        private int SegmentWidth(Graphics g, string label, string value)
        {
            return TextWidth(g, label, _metricFont) + TextWidth(g, value, _valueFont);
        }

        private int DrawSegment(Graphics g, string label, string value, int x)
        {
            x = DrawText(g, label, _metricFont, LabelColor, x);
            return DrawText(g, value, _valueFont, ValueColor, x);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            if (Width <= 16) return;

            using (var pen = new Pen(Color.FromArgb(88, 100, 110)))
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

            // Une seule ligne, sans retour à la ligne. En petite fenêtre, le titre
            // et le mode de capture sont sacrifiés avant les métriques essentielles.
            bool compact = Width < 920;
            string[] labels = compact
                ? new[] { Localization.T("CPU "), Localization.T("GPU "), Localization.T("CAP "), Localization.T("AFF "), Localization.T("DÉBIT ") }
                : new[] { Localization.T("CPU "), Localization.T("GPU "), Localization.T("FPS carte "), Localization.T("FPS écran "), Localization.T("Débit ") };
            string[] values = { _cpu, _gpu, _capturedFps, _presentedFps, _bitrate };

            const int pad = 12;
            const int separatorSpace = 18;
            int essentialWidth = 0;
            for (int i = 0; i < labels.Length; i++)
                essentialWidth += SegmentWidth(g, labels[i], values[i]);
            essentialWidth += (labels.Length - 1) * separatorSpace;

            int brandWidth = TextWidth(g, "BROADCASTER", _brandFont) + separatorSpace;
            bool showBrand = Width >= pad * 2 + essentialWidth + brandWidth + 4;
            int x = pad;
            if (showBrand)
            {
                x = DrawText(g, "BROADCASTER", _brandFont, AccentColor, x);
                x += 9;
                Separator(g, x, Height);
                x += 9;
            }

            for (int i = 0; i < labels.Length; i++)
            {
                // Évite qu'un champ trop long dessine hors du bandeau.
                if (x + SegmentWidth(g, labels[i], values[i]) > Width - pad)
                    break;
                x = DrawSegment(g, labels[i], values[i], x);
                if (i < labels.Length - 1)
                {
                    x += 9;
                    if (x < Width - pad) Separator(g, x, Height);
                    x += 9;
                }
            }

            // Le mode est facultatif : il s'affiche uniquement quand il reste
            // suffisamment de largeur, et ne provoque jamais de deuxième ligne.
            string modeLabel = _mode;
            int modeWidth = TextWidth(g, modeLabel, _metricFont);
            if (x + modeWidth + pad + 8 < Width)
            {
                // Fixe le mode actif au bord droit de la banderole.
                int modeX = Math.Max(x + 12, Width - pad - modeWidth);
                DrawText(g, modeLabel, _metricFont, AccentColor, modeX);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                _brandFont.Dispose();
                _metricFont.Dispose();
                _valueFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
