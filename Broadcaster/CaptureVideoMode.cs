using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SharpDX.MediaFoundation;

namespace Broadcaster
{
    /// <summary>
    /// Describes a native mode actually advertised by the capture driver. No synthetic
    /// frame rates are invented: 60000/1001 remains distinct from 60/1.
    /// </summary>
    internal sealed class CaptureVideoMode
    {
        public int NativeIndex { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public uint RateNumerator { get; set; }
        public uint RateDenominator { get; set; }
        public string Format { get; set; }
        public double Fps => RateDenominator == 0 ? 0 : (double)RateNumerator / RateDenominator;
        public bool Supported => Format == "NV12" || Format == "YUY2" || Format == "UYVY" ||
                                 Format == "RGB32" || Format == "P010" || Format == "MJPEG" || Format == "H264" || Format == "HEVC";

        public string Description => Format + " " + Width + "×" + Height + " @ " +
            Fps.ToString("0.##", CultureInfo.InvariantCulture) + " FPS";

        public double UncompressedMiBPerSecond
        {
            get
            {
                double bytesPerPixel = Format == "NV12" ? 1.5 :
                    Format == "P010" ? 3.0 :
                    (Format == "YUY2" || Format == "UYVY") ? 2.0 :
                    Format == "RGB32" ? 4.0 : 0;
                return Width * (double)Height * Fps * bytesPerPixel / (1024.0 * 1024.0);
            }
        }
    }

    internal static class CaptureModeSelector
    {
        public static List<CaptureVideoMode> Enumerate(MediaTypeHandler handler)
        {
            var result = new List<CaptureVideoMode>();
            for (int index = 0; index < handler.MediaTypeCount; index++)
            {
                MediaType mediaType = null;
                try
                {
                    mediaType = handler.GetMediaTypeByIndex(index);
                    ulong dimensions = (ulong)mediaType.Get(MediaTypeAttributeKeys.FrameSize);
                    ulong rate = (ulong)mediaType.Get(MediaTypeAttributeKeys.FrameRate);
                    var mode = new CaptureVideoMode
                    {
                        NativeIndex = index,
                        Width = (int)(dimensions >> 32),
                        Height = (int)(dimensions & 0xFFFFFFFF),
                        RateNumerator = (uint)(rate >> 32),
                        RateDenominator = (uint)rate,
                        Format = CaptureSession.NameOfFormat(mediaType.Get<Guid>(MediaTypeAttributeKeys.Subtype))
                    };
                    if (mode.Width > 0 && mode.Height > 0 && mode.Fps > 0)
                        result.Add(mode);
                }
                catch (Exception)
                {
                    // UVC drivers sometimes expose incomplete or malformed native types.
                    // An invalid type must not prevent discovery of the remaining modes.
                }
                finally { mediaType?.Dispose(); }
            }
            return result;
        }

        /// <summary>
        /// Auto keeps the historical best-specs scoring, including all supported formats.
        /// An explicit choice is a hard format constraint: it may select a lower FPS or
        /// resolution, but it must never silently switch to another pixel format.
        /// </summary>
        public static List<CaptureVideoMode> Rank(IList<CaptureVideoMode> modes, int desiredWidth,
            int desiredHeight, int desiredFps, string preferredFormat)
        {
            string format = string.IsNullOrWhiteSpace(preferredFormat) ? "Auto" : preferredFormat.Trim();
            bool automatic = string.Equals(format, "Auto", StringComparison.OrdinalIgnoreCase);
            if (modes == null) return new List<CaptureVideoMode>();

            return modes.Where(m => m != null && m.Supported &&
                    (automatic || string.Equals(m.Format, format, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(m => Score(m, desiredWidth, desiredHeight, desiredFps, "Auto"))
                .ThenBy(m => m.NativeIndex)
                .ToList();
        }

        public static CaptureVideoMode Select(IList<CaptureVideoMode> modes, int desiredWidth,
            int desiredHeight, int desiredFps, string preferredFormat)
        {
            return Rank(modes, desiredWidth, desiredHeight, desiredFps, preferredFormat)
                .FirstOrDefault();
        }

        internal static double Score(CaptureVideoMode mode, int width, int height,
            int requestedFps, string format)
        {
            if (mode == null || !mode.Supported) return double.NegativeInfinity;
            // Keep Score consistent with strict manual selection as well as Rank.
            if (!string.IsNullOrWhiteSpace(format) &&
                !string.Equals(format, "Auto", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode.Format, format.Trim(), StringComparison.OrdinalIgnoreCase))
                return double.NegativeInfinity;
            // Resolution first, without rewarding huge frame sizes when a smaller
            // resolution was explicitly requested for a 1080p USB capture card.
            bool exact = mode.Width == width && mode.Height == height;
            double relativeError = Math.Abs(Math.Log((double)mode.Width / Math.Max(1, width))) +
                                   Math.Abs(Math.Log((double)mode.Height / Math.Max(1, height)));
            double score = exact ? 100000000 : 10000000 - relativeError * 1000000;

            // Highest possible FPS when Auto FPS is selected; otherwise prefer
            // the requested cadence. With an explicit pixel format, this scoring
            // is applied only to modes of that format (even if they are 30 FPS).
            if (requestedFps <= 0)
                score += Math.Min(mode.Fps, 1000) * 10000;
            else
            {
                double delta = mode.Fps - requestedFps;
                score += 3000000 - Math.Abs(delta) * 24000;
                if (delta > 1.0) score -= delta * 10000;
            }

            if (!string.IsNullOrEmpty(format) && format != "Auto" && mode.Format == format)
                score += 8000;
            else if (string.IsNullOrEmpty(format) || format == "Auto")
            {
                // Prefer uncompressed formats at equal resolution and frame rate,
                // without implying that USB bandwidth can always sustain them.
                score += mode.Format == "NV12" ? 7000 :
                         mode.Format == "YUY2" ? 6500 :
                         mode.Format == "UYVY" ? 6000 :
                         mode.Format == "RGB32" ? 5500 :
                         mode.Format == "P010" ? 5000 :
                         mode.Format == "MJPEG" ? 4000 : 2000;
            }
            return score;
        }
    }
}
