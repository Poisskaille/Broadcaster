using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Broadcaster
{
    /// <summary>
    /// Tampon circulaire des dernières secondes de l'image affichée par Broadcaster.
    /// Capture le panneau vidéo via le bureau Windows (même principe que ScreenshotStore).
    /// NE capture PAS un flux matériel indépendant : la fenêtre doit être visible et non masquée.
    /// L'audio est récupéré par WASAPI loopback sur la sortie audio choisie dans les options.
    /// Les séquences sont enregistrées en MJPEG AVI + PCM, sans logiciel externe.
    /// </summary>
    public sealed class ReplayClipRecorder : IDisposable
    {
        // Sécurité mémoire pour le tampon MJPEG (les très grandes résolutions
        // peuvent raccourcir la durée disponible ; jamais de dépassement volontaire).
        private const long MaxBufferedVideoBytes = 512L * 1024 * 1024;
        private const long MaximumAviSize = 1900L * 1024 * 1024;
        // Ne jamais accumuler une file de centaines d'images brutes pendant
        // que le JPEG est en train d'être compressé.
        private const int MaxPendingFrames = 6;
        private const int JpegWorkerCount = 2;
        private const int JpegQuality = 85;

        private readonly object _sync = new object();
        private readonly List<ReplayVideoFrame> _frames = new List<ReplayVideoFrame>();
        private readonly List<ReplayAudioChunk> _audio = new List<ReplayAudioChunk>();
        private readonly Queue<PendingVideoFrame> _pendingFrames = new Queue<PendingVideoFrame>();
        private readonly ManualResetEventSlim _wakeup = new ManualResetEventSlim(false);
        private Thread _captureThread;
        private Thread[] _encodeThreads;
        private WasapiLoopbackCapture _loopback;
        private MMDevice _audioDevice;
        private Rectangle _captureBounds;
        private long _videoBytes;
        private bool _running;
        private int _captureGeneration;
        private int _bufferEpoch;
        private long _capturedFrames;
        private long _encodedFrames;
        private long _droppedFrames;
        private bool _highResolutionTimerActive;
        private bool _disposed;
        private int _durationSeconds = 30;
        private int _width = 1920;
        private int _height = 1080;
        private int _targetFps = 30;
        private int _audioSampleRate;
        private int _saving;
        private string _lastCaptureError;
        private DateTime _lastCaptureErrorTime;
        private int _consecutiveCaptureFailures;
        private static readonly object DiagnosticSync = new object();

        public string DiagnosticPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Broadcaster", "Clips", "diagnostics.log");

        public void WriteDiagnostic(string details)
        {
            try
            {
                lock (DiagnosticSync)
                {
                    string path = DiagnosticPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    // Garder le journal compact et facilement partageable.
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                        File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                        " | " + details + Environment.NewLine);
                }
            }
            catch { /* Le journal ne doit jamais bloquer l'enregistrement. */ }
        }

        public bool IsRunning
        {
            get { lock (_sync) return _running; }
        }

        public bool IsSaving => Interlocked.CompareExchange(ref _saving, 0, 0) != 0;

        public bool HasAudio
        {
            get { lock (_sync) return _loopback != null; }
        }

        public static string GetClipsFolder()
        {
            string baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(baseFolder))
                baseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            string folder = Path.Combine(baseFolder, "Broadcaster", "Clips");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>À appeler depuis le thread WinForms. Rect = coordonnées absolues écran.</summary>
        public void SetCaptureBounds(Rectangle bounds)
        {
            lock (_sync) _captureBounds = bounds;
        }

        public void Start(int durationSeconds, string audioOutputEndpointId,
            int outputWidth, int outputHeight, int targetFps)
        {
            if (outputWidth < 160 || outputWidth > 3840 ||
                outputHeight < 120 || outputHeight > 2160)
                throw new ArgumentOutOfRangeException(nameof(outputWidth),
                    Localization.T("Résolution de clip non prise en charge (maximum 3840 x 2160)."));
            if (targetFps < 10 || targetFps > 60)
                throw new ArgumentOutOfRangeException(nameof(targetFps),
                    Localization.T("Le FPS cible doit être compris entre 10 et 60."));
            Stop();
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ReplayClipRecorder));
                _durationSeconds = Math.Max(5, Math.Min(120, durationSeconds));
                _width = outputWidth;
                _height = outputHeight;
                _targetFps = targetFps;
                _lastCaptureError = null;
                _consecutiveCaptureFailures = 0;
                _capturedFrames = 0;
                _encodedFrames = 0;
                _droppedFrames = 0;
                _running = true;
                int generation = ++_captureGeneration;
                _captureThread = new Thread(() => CaptureLoop(generation))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = "Broadcaster Clip - Capture Ecran"
                };
                _encodeThreads = new Thread[JpegWorkerCount];
                for (int i = 0; i < _encodeThreads.Length; i++)
                {
                    _encodeThreads[i] = new Thread(() => EncodeLoop(generation))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Normal,
                        Name = "Broadcaster Clip - JPEG " + (i + 1)
                    };
                }
            }

            // Le timer Windows standard peut faire des pauses de 15,6 ms :
            // à 60 images/s cela crée des à-coups même avec un encodeur rapide.
            // On demande 1 ms uniquement pendant l'activation des clips.
            _highResolutionTimerActive = timeBeginPeriod(1) == 0;

            // L'enregistrement vidéo ne dépend pas du périphérique audio.
            TryStartLoopback(audioOutputEndpointId);
            foreach (Thread encoder in _encodeThreads) encoder.Start();
            _captureThread.Start();
            WriteDiagnostic("Tampon démarré : " + _durationSeconds + " s, " +
                outputWidth + "x" + outputHeight + " @ " + targetFps + " FPS cible, audio " +
                (HasAudio ? "actif" : "indisponible (vidéo seule)"));
        }

        public void ClearBuffer()
        {
            lock (_sync)
            {
                _bufferEpoch++;
                ClearPendingFramesUnderLock();
                _frames.Clear();
                _audio.Clear();
                _videoBytes = 0;
                Monitor.PulseAll(_sync);
            }
        }

        private void ClearPendingFramesUnderLock()
        {
            while (_pendingFrames.Count > 0)
                _pendingFrames.Dequeue().Bitmap.Dispose();
        }

        private void TryStartLoopback(string endpointId)
        {
            MMDevice device = null;
            WasapiLoopbackCapture capture = null;
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    device = string.IsNullOrEmpty(endpointId)
                        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        : enumerator.GetDevice(endpointId);
                }

                capture = new WasapiLoopbackCapture(device);
                WaveFormat format = capture.WaveFormat;
                bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
                if (format is WaveFormatExtensible extensible)
                    isFloat = extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");

                if (format.Channels < 1 ||
                    !((isFloat && format.BitsPerSample == 32) ||
                      (!isFloat && format.BitsPerSample == 16)))
                {
                    WriteDiagnostic("Format audio non géré : " + format);
                    return;
                }

                int sampleRate = format.SampleRate;
                int channels = format.Channels;
                int bits = format.BitsPerSample;
                capture.DataAvailable += (s, e) =>
                    OnAudioData(e.Buffer, e.BytesRecorded, sampleRate, channels, bits, isFloat);

                lock (_sync)
                {
                    if (!_running) return;
                    _audioSampleRate = sampleRate;
                }
                capture.StartRecording();
                lock (_sync)
                {
                    if (!_running) return;
                    _audioDevice = device;
                    _loopback = capture;
                }
                capture = null;
                device = null;
            }
            catch (Exception ex)
            {
                WriteDiagnostic("Audio optionnel indisponible : " + ex.Message);
            }
            finally
            {
                if (capture != null) capture.Dispose();
                if (device != null) device.Dispose();
            }
        }

        private void OnAudioData(byte[] input, int inputLength, int sampleRate,
            int channels, int bits, bool isFloat)
        {
            if (inputLength <= 0) return;
            int frames = inputLength / (channels * (bits / 8));
            if (frames <= 0) return;
            byte[] pcm = new byte[frames * 4]; // stéréo PCM 16 bits
            for (int i = 0; i < frames; i++)
            {
                // Canaux supplémentaires ignorés (L/R), mono dupliqué.
                short left = ConvertSample(input, (i * channels) * (bits / 8), isFloat);
                short right = channels == 1 ? left : ConvertSample(input,
                    (i * channels + 1) * (bits / 8), isFloat);
                int dst = i * 4;
                pcm[dst] = (byte)left;
                pcm[dst + 1] = (byte)(left >> 8);
                pcm[dst + 2] = (byte)right;
                pcm[dst + 3] = (byte)(right >> 8);
            }

            // DataAvailable donne une tranche passée : on en reconstruit son début.
            DateTime end = DateTime.UtcNow;
            DateTime begin = end.AddSeconds(-frames / (double)sampleRate);
            lock (_sync)
            {
                if (!_running || _audioSampleRate != sampleRate) return;
                _audio.Add(new ReplayAudioChunk(begin, pcm));
                TrimBuffers(DateTime.UtcNow);
            }
        }

        private static short ConvertSample(byte[] bytes, int offset, bool isFloat)
        {
            if (isFloat)
            {
                float value = BitConverter.ToSingle(bytes, offset);
                if (float.IsNaN(value)) return 0;
                return (short)Math.Round(Math.Max(-1.0, Math.Min(1.0, value)) * 32767);
            }
            return BitConverter.ToInt16(bytes, offset);
        }

        // La copie de l'écran et l'encodage JPEG ne doivent PAS se produire sur
        // le même thread. Sinon un JPEG 1080p/4K lent bloque la capture et transforme
        // le choix « 60 FPS » en 15-25 vraies images répétées dans le fichier AVI.
        private void CaptureLoop(int generation)
        {
            var clock = Stopwatch.StartNew();
            long nextFrame = 0;
            int targetFps, width, height;
            lock (_sync) { targetFps = _targetFps; width = _width; height = _height; }
            while (true)
            {
                Rectangle bounds;
                lock (_sync)
                {
                    if (!_running || _captureGeneration != generation) break;
                    bounds = _captureBounds;
                }
                if (bounds.Width >= 4 && bounds.Height >= 4)
                {
                    Bitmap bitmap = null;
                    try
                    {
                        bitmap = CaptureBitmap(bounds, width, height);
                        DateTime timestamp = DateTime.UtcNow; // horodatage AVANT compression
                        lock (_sync)
                        {
                            if (_running && _captureGeneration == generation)
                            {
                                _capturedFrames++;
                                while (_pendingFrames.Count >= MaxPendingFrames)
                                {
                                    _pendingFrames.Dequeue().Bitmap.Dispose();
                                    _droppedFrames++;
                                }
                                _pendingFrames.Enqueue(new PendingVideoFrame(timestamp, bitmap, _bufferEpoch));
                                bitmap = null; // la file devient propriétaire de l'image
                                _consecutiveCaptureFailures = 0;
                                _lastCaptureError = null;
                                Monitor.PulseAll(_sync);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RecordFrameError("Capture de l'écran", ex);
                    }
                    finally { if (bitmap != null) bitmap.Dispose(); }
                }

                // Horloge monotone ; le thread ne ralentit plus pour attendre le JPEG.
                nextFrame++;
                long dueMs = nextFrame * 1000L / targetFps;
                long behindMs = clock.ElapsedMilliseconds - dueMs;
                if (behindMs > 1000 / targetFps)
                {
                    nextFrame = clock.ElapsedMilliseconds * targetFps / 1000;
                    dueMs = (nextFrame + 1) * 1000L / targetFps;
                    nextFrame++;
                }
                int waitMs = (int)Math.Max(1, dueMs - clock.ElapsedMilliseconds);
                if (_wakeup.Wait(waitMs)) break;
            }
        }

        private void EncodeLoop(int generation)
        {
            try
            {
                // Encoder et paramètres réutilisés pour toutes les images :
                // évite d'énumérer les codecs GDI+ soixante fois par seconde.
                ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders()
                    .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (var quality = new EncoderParameters(1))
                {
                    quality.Param[0] = new EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality, (long)JpegQuality);
                    while (true)
                    {
                        PendingVideoFrame pending;
                        lock (_sync)
                        {
                            while (_running && _captureGeneration == generation &&
                                   _pendingFrames.Count == 0)
                                Monitor.Wait(_sync, 250);
                            if (!_running || _captureGeneration != generation) break;
                            pending = _pendingFrames.Dequeue();
                        }
                        try
                        {
                            byte[] jpeg;
                            using (pending.Bitmap)
                            using (var stream = new MemoryStream())
                            {
                                pending.Bitmap.Save(stream, jpegCodec, quality);
                                jpeg = stream.ToArray();
                            }
                            lock (_sync)
                            {
                                if (_running && _captureGeneration == generation &&
                                    _bufferEpoch == pending.Epoch)
                                {
                                    // Les deux threads JPEG peuvent se terminer dans un
                                    // ordre différent de celui de la capture. Le writer AVI
                                    // exige des images classées chronologiquement.
                                    int insertAt = _frames.Count;
                                    while (insertAt > 0 &&
                                           _frames[insertAt - 1].Time > pending.Time)
                                        insertAt--;
                                    _frames.Insert(insertAt,
                                        new ReplayVideoFrame(pending.Time, jpeg));
                                    _videoBytes += jpeg.Length;
                                    _encodedFrames++;
                                    TrimBuffers(DateTime.UtcNow);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // L'image appartient à l'encodeur, même en cas d'échec.
                            try { pending.Bitmap.Dispose(); } catch { }
                            RecordFrameError("Compression JPEG", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RecordFrameError("Initialisation compression JPEG", ex);
            }
        }

        private void RecordFrameError(string operation, Exception ex)
        {
            bool shouldLog;
            lock (_sync)
            {
                _consecutiveCaptureFailures++;
                _lastCaptureError = operation + " : " + ex.Message;
                shouldLog = _consecutiveCaptureFailures == 1 ||
                    (DateTime.UtcNow - _lastCaptureErrorTime).TotalSeconds >= 10;
                if (shouldLog) _lastCaptureErrorTime = DateTime.UtcNow;
            }
            if (shouldLog) WriteDiagnostic(operation + " : " + ex);
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StretchBlt(IntPtr destDC, int xDest, int yDest,
            int destWidth, int destHeight, IntPtr srcDC, int xSrc, int ySrc,
            int srcWidth, int srcHeight, uint rop);

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr dc, int mode);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previousOrigin);

        private const uint SrcCopy = 0x00CC0020;
        // HALFTONE donne un redimensionnement plus net que COLORONCOLOR (v1.6.2).
        // Le chemin sans redimensionnement continue d'utiliser CopyFromScreen.
        private const int Halftone = 4;

        private static Bitmap CaptureBitmap(Rectangle bounds, int outputWidth, int outputHeight)
        {
            var output = new Bitmap(outputWidth, outputHeight, PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics canvas = Graphics.FromImage(output))
                {
                    if (bounds.Width == outputWidth && bounds.Height == outputHeight)
                    {
                        // Chemin rapide sans mise à l'échelle.
                        canvas.CopyFromScreen(bounds.Location, Point.Empty,
                            bounds.Size, CopyPixelOperation.SourceCopy);
                    }
                    else
                    {
                        // Ne jamais utiliser HighQualityBicubic sur 60 grandes images/s.
                        // StretchBlt copie directement du bureau dans le bitmap final,
                        // sans bitmap intermédiaire ni interpolation bicubique.
                        canvas.Clear(Color.Black);
                        double scale = Math.Min(outputWidth / (double)bounds.Width,
                                                outputHeight / (double)bounds.Height);
                        int w = Math.Max(1, (int)Math.Round(bounds.Width * scale));
                        int h = Math.Max(1, (int)Math.Round(bounds.Height * scale));
                        IntPtr srcDC = GetDC(IntPtr.Zero);
                        if (srcDC == IntPtr.Zero)
                            throw new InvalidOperationException(Localization.T("Impossible d'obtenir le bureau Windows."));
                        try
                        {
                            IntPtr destDC = canvas.GetHdc();
                            try
                            {
                                // Restaure la qualité visuelle en cas de mise à l'échelle,
                                // sans revenir à un DrawImage bicubique sur chaque frame.
                                if (SetStretchBltMode(destDC, Halftone) == 0)
                                    throw new System.ComponentModel.Win32Exception(
                                        Marshal.GetLastWin32Error(), Localization.T("Impossible d'activer le redimensionnement de qualité."));
                                SetBrushOrgEx(destDC, 0, 0, IntPtr.Zero);
                                if (!StretchBlt(destDC, (outputWidth - w) / 2,
                                    (outputHeight - h) / 2, w, h, srcDC,
                                    bounds.X, bounds.Y, bounds.Width, bounds.Height, SrcCopy))
                                    throw new System.ComponentModel.Win32Exception(
                                        Marshal.GetLastWin32Error(), Localization.T("Échec de la copie vidéo redimensionnée."));
                            }
                            finally { canvas.ReleaseHdc(destDC); }
                        }
                        finally { ReleaseDC(IntPtr.Zero, srcDC); }
                    }
                }
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }

        private sealed class PendingVideoFrame
        {
            internal readonly DateTime Time;
            internal readonly Bitmap Bitmap;
            internal readonly int Epoch;
            internal PendingVideoFrame(DateTime time, Bitmap bitmap, int epoch)
            {
                Time = time;
                Bitmap = bitmap;
                Epoch = epoch;
            }
        }

        private void TrimBuffers(DateTime now)
        {
            DateTime minTime = now.AddSeconds(-_durationSeconds - 1);
            while (_frames.Count > 0 &&
                   (_frames[0].Time < minTime || _videoBytes > MaxBufferedVideoBytes))
            {
                _videoBytes -= _frames[0].Jpeg.Length;
                _frames.RemoveAt(0);
            }
            while (_audio.Count > 0 && _audio[0].Start.AddSeconds(
                _audio[0].Pcm.Length / (double)Math.Max(1, _audioSampleRate * 4)) < minTime)
                _audio.RemoveAt(0);
        }

        public async Task<string> SaveReplayAsync()
        {
            if (Interlocked.Exchange(ref _saving, 1) != 0)
                throw new InvalidOperationException(Localization.T("Un clip est déjà en cours d'enregistrement."));
            try
            {
                List<ReplayVideoFrame> frames;
                List<ReplayAudioChunk> audio;
                int sampleRate;
                int width, height, targetFps;
                Rectangle screenCaptureBounds;
                DateTime now = DateTime.UtcNow;
                long captureCount, encodedCount, droppedCount;
                lock (_sync)
                {
                    if (!_running) throw new InvalidOperationException(Localization.T("Active les clips dans Options > Extras."));
                    DateTime minTime = now.AddSeconds(-_durationSeconds);
                    frames = _frames.Where(f => f.Time >= minTime).ToList();
                    captureCount = _capturedFrames;
                    encodedCount = _encodedFrames;
                    droppedCount = _droppedFrames;
                    audio = _audio.ToList();
                    sampleRate = _loopback != null ? _audioSampleRate : 0;
                    width = _width; height = _height; targetFps = _targetFps;
                    screenCaptureBounds = _captureBounds;
                }
                if (frames.Count < 5 || (now - frames[frames.Count - 1].Time).TotalSeconds > 2)
                {
                    string reason;
                    lock (_sync)
                    {
                        reason = _captureBounds.Width < 4 || _captureBounds.Height < 4
                            ? Localization.T("La zone de capture vidéo n'est pas disponible.")
                            : _lastCaptureError != null
                                ? Localization.F("La capture échoue : {0}", _lastCaptureError)
                                : Localization.T("Le tampon n'a pas encore assez d'images.");
                    }
                    throw new InvalidOperationException(reason +
                        Localization.F(" Vérifie que la vidéo est visible et attends quelques secondes. Images récentes : {0}.", frames.Count));
                }

                // Vérifier aussi la résolution JPEG REELLE ; le header AVI seul
                // pourrait annoncer du 1080p avec des images d'une autre taille.
                // Le code de capture fournit normalement exactement _width x _height.
                using (var jpegStream = new MemoryStream(frames[0].Jpeg, false))
                using (var firstFrame = Image.FromStream(jpegStream))
                {
                    if (firstFrame.Width != width || firstFrame.Height != height)
                        throw new InvalidOperationException(
                            Localization.F("Résolution du tampon incorrecte : les images sont en {0}x{1} alors que les paramètres demandent {2}x{3}.",
                                firstFrame.Width, firstFrame.Height, width, height));
                }

                string folder = GetClipsFolder();
                string filename = "Broadcaster_Clip_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") +
                    "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".avi";
                string path = Path.Combine(folder, filename);

                return await Task.Run(() =>
                {
                    try
                    {
                        ReplayAviWriter.Write(path, frames, audio, sampleRate,
                            width, height, targetFps, MaximumAviSize);
                        // Le fichier doit contenir l'en-tête AVI et les données.
                        if (!File.Exists(path) || new FileInfo(path).Length < 1024)
                            throw new IOException(Localization.T("Le fichier AVI produit est vide ou incomplet."));
                        double seconds = frames.Count >= 2
                            ? (frames[frames.Count - 1].Time - frames[0].Time).TotalSeconds
                            : 0;
                        double actualFps = seconds > 0.1
                            ? (frames.Count - 1) / seconds : 0;
                        WriteDiagnostic("Clip enregistré : " + path +
                            " (" + frames.Count + " images réelles / " +
                            seconds.ToString("F2") + " s, FPS réels environ " +
                            actualFps.ToString("F1") + ", objectif " + targetFps +
                            ", captures total " + captureCount +
                            ", JPEG total " + encodedCount +
                            ", images abandonnées " + droppedCount +
                            ", résolution AVI/JPEG " + width + "x" + height +
                            ", zone écran " + screenCaptureBounds.Width + "x" +
                            screenCaptureBounds.Height + ")");
                        if (screenCaptureBounds.Width < width || screenCaptureBounds.Height < height)
                            WriteDiagnostic("Qualité : l'image à l'écran est plus petite que le clip " +
                                "(agrandissement). Pour obtenir les détails natifs, " +
                                "afficher Broadcaster sur un écran suffisamment grand.");
                        return path;
                    }
                    catch
                    {
                        try { File.Delete(path); } catch { }
                        throw;
                    }
                });
            }
            finally { Interlocked.Exchange(ref _saving, 0); }
        }

        public void Stop()
        {
            Thread thread;
            Thread[] encoderThreads;
            WasapiLoopbackCapture capture;
            MMDevice device;
            lock (_sync)
            {
                _running = false;
                _captureGeneration++;
                _captureBounds = Rectangle.Empty;
                thread = _captureThread;
                _captureThread = null;
                encoderThreads = _encodeThreads;
                _encodeThreads = null;
                Monitor.PulseAll(_sync);
                capture = _loopback;
                _loopback = null;
                device = _audioDevice;
                _audioDevice = null;
            }
            _wakeup.Set();
            if (thread != null && thread != Thread.CurrentThread)
                thread.Join(3000);
            // La compression en cours peut prendre du temps en 4K, mais un ancien
            // encodeur ne peut plus écrire dans le nouveau tampon (generation).
            if (encoderThreads != null)
                foreach (Thread encoder in encoderThreads)
                    if (encoder != Thread.CurrentThread) encoder.Join(3000);
            _wakeup.Reset();
            if (_highResolutionTimerActive)
            {
                _highResolutionTimerActive = false;
                timeEndPeriod(1);
            }
            if (capture != null)
            {
                try { capture.StopRecording(); } catch { }
                try { capture.Dispose(); } catch { }
            }
            if (device != null)
                try { device.Dispose(); } catch { }
            lock (_sync)
            {
                _bufferEpoch++;
                ClearPendingFramesUnderLock();
                _frames.Clear();
                _audio.Clear();
                _videoBytes = 0;
                _audioSampleRate = 0;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            _wakeup.Dispose();
        }
    }

    internal sealed class ReplayVideoFrame
    {
        internal DateTime Time { get; }
        internal byte[] Jpeg { get; }
        internal ReplayVideoFrame(DateTime time, byte[] jpeg) { Time = time; Jpeg = jpeg; }
    }

    internal sealed class ReplayAudioChunk
    {
        internal DateTime Start { get; }
        internal byte[] Pcm { get; }
        internal ReplayAudioChunk(DateTime start, byte[] pcm) { Start = start; Pcm = pcm; }
    }
}
