using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SharpDX;
using SharpDX.Mathematics.Interop;
using SharpDX.MediaFoundation;
using NAudio.CoreAudioApi;

namespace Broadcaster
{
    public static class MediaEngine
    {
        // Sous-types Media Foundation pour les périphériques de capture HDMI.
        internal static readonly Guid VideoYuy2 = new Guid("32595559-0000-0010-8000-00AA00389B71");
        internal static readonly Guid VideoNv12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        internal static readonly Guid VideoMjpg = new Guid("47504A4D-0000-0010-8000-00AA00389B71");
        internal static readonly Guid VideoRgb32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        internal static readonly Guid VideoUyvy = new Guid("59565955-0000-0010-8000-00AA00389B71");
        internal static readonly Guid VideoH264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        internal static readonly Guid VideoHevc = new Guid("43564548-0000-0010-8000-00AA00389B71");
        internal static readonly Guid VideoP010 = new Guid("30313050-0000-0010-8000-00AA00389B71");

        private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID =
            new Guid("8AC3587A-4AE7-42D8-99E0-0A6013EEF90F");

        private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_GUID =
            new Guid("14DD9A1C-7CFF-41BE-B1B9-BA1AC6ECB571");

        public static List<VideoDeviceInfo> EnumerateVideoDevices()
        {
            var result = new List<VideoDeviceInfo>();

            using (var attributes = new MediaAttributes(1))
            {
                attributes.Set(CaptureDeviceAttributeKeys.SourceType, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);

                Activate[] devices = MediaFactory.EnumDeviceSources(attributes);
                if (devices == null) return result;

                foreach (var device in devices)
                {
                    string name = device.Get(CaptureDeviceAttributeKeys.FriendlyName);
                    string link = device.Get(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink);
                    result.Add(new VideoDeviceInfo { FriendlyName = name, SymbolicLink = link, Activate = device });
                }
            }

            return result;
        }

        public static List<AudioCaptureDeviceInfo> EnumerateAudioCaptureDevices()
        {
            var result = new List<AudioCaptureDeviceInfo>();

            using (var attributes = new MediaAttributes(1))
            {
                attributes.Set(CaptureDeviceAttributeKeys.SourceType, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_GUID);

                Activate[] devices = MediaFactory.EnumDeviceSources(attributes);
                if (devices == null) return result;

                foreach (var device in devices)
                {
                    string name = device.Get(CaptureDeviceAttributeKeys.FriendlyName);
                    result.Add(new AudioCaptureDeviceInfo { FriendlyName = name, Activate = device });
                }
            }

            return result;
        }

        public static List<AudioDeviceInfo> EnumerateAudioOutputDevices()
        {
            var result = new List<AudioDeviceInfo>();
            using (var enumerator = new MMDeviceEnumerator())
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    result.Add(new AudioDeviceInfo { FriendlyName = device.FriendlyName, EndpointId = device.ID });
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Pilote DEUX sessions Media Foundation totalement indépendantes : une pour la vidéo,
    /// une pour l'audio capturé séparément (si fourni). Les garder séparées évite qu'une
    /// topologie unique ne fasse dépendre le rendu vidéo du bon fonctionnement de la piste audio
    /// (utile si le device se comporte mal quand les deux flux sont ouverts ensemble).
    /// </summary>
    public class CaptureSession : IDisposable
    {
        private MediaSession _videoSession;
        private D3D11CaptureRenderer _gpuRenderer;
        private ColorProfile _videoSettings;
        public string ActiveSourceFormat { get; private set; } = "";
        public string ActiveVideoWarning { get; private set; } = "";
        internal List<CaptureVideoMode> AvailableVideoModes { get; } = new List<CaptureVideoMode>();
        private double _selectedSourceFps = 60.0;
        // Mode réellement accepté par le pilote, différent de la résolution demandée en cas de repli.
        internal CaptureVideoMode ActiveVideoMode { get; private set; }
        // -1 : pipeline EVR natif (aucun callback de samples, impossible de mesurer ces FPS).
        public long ReceivedVideoSamples => _gpuRenderer == null ? -1 : _gpuRenderer.ReceivedSamples;
        public long PresentedVideoSamples => _gpuRenderer == null ? -1 : _gpuRenderer.PresentedSamples;
        public double MeasuredGpuFps => _gpuRenderer?.MeasuredFps ?? 0.0;
        private Topology _videoTopology;
        private MediaSource _videoSource;
        // IMFActivate conserve l'instance activée jusqu'à ShutdownObject().
        // Appeler seulement MediaSource.Shutdown() laisse une source morte en cache
        // et provoque MF_E_SHUTDOWN (0xC00D3E85) lors d'une nouvelle activation.
        private Activate _videoSourceActivate;
        private Activate _videoRendererActivate;
        private Thread _videoEventThread;
        private volatile bool _videoRunning;
        public bool HasVideoDisplayControl => _videoDisplayControl != null;
        private MediaSession _audioSession;
        private Topology _audioTopology;
        private MediaSource _audioSource;
        private Activate _audioSourceActivate;
        private Activate _audioRendererActivate;
        private int _lastRequestedWidth;
        private int _lastRequestedHeight;
        private VideoDisplayPositionControl _videoDisplayControl;
        private Thread _audioEventThread;
        private volatile bool _audioRunning;

        private string _audioEndpointId;

        public event Action<string> OnError;
        // Distinct des erreurs audio : seules les erreurs du flux vidéo peuvent
        // déclencher un repli du format manuel vers Auto côté interface.
        public event Action<string> OnVideoRenderFailure;
        public event Action OnVideoSessionStarted;

        public float CurrentVolume { get; private set; } = 1.0f;
        public string ActiveVideoSymbolicLink { get; private set; }
        public string ActiveVideoFriendlyName { get; private set; }


        public void Start(VideoDeviceInfo videoDevice, AudioDeviceInfo audioOutputDevice, AudioCaptureDeviceInfo audioCaptureDevice,
                           IntPtr renderHwnd, int targetWidth, int targetHeight, ColorProfile videoSettings = null,
                           int captureTargetFps = 0, bool previewVsync = false)
        {
            Stop();

            if (videoDevice == null)
                throw new ArgumentNullException(nameof(videoDevice));
            if (videoDevice.Activate == null)
                throw new InvalidOperationException(Localization.T("Le device vidéo n'a pas d'activateur valide. Ouvre les options et resélectionne-le dans la liste."));

            _audioEndpointId = audioOutputDevice?.EndpointId;
            _videoSettings = videoSettings != null && !videoSettings.IsDefault ? videoSettings : null;

            try
            {
                StartVideoSession(videoDevice, renderHwnd, targetWidth, targetHeight, captureTargetFps, previewVsync);

                if (audioCaptureDevice?.Activate != null)
                    StartAudioSession(audioCaptureDevice);

                SetVolume(CurrentVolume);
            }
            catch
            {
                // Une ouverture partielle ne doit jamais empoisonner la prochaine capture.
                try { Stop(); } catch { /* Conserver l'erreur de démarrage initiale. */ }
                throw;
            }
        }

        public void EnsureVideoDisplayControl(int attemptsLeft = 6)
        {
            if (_gpuRenderer != null || _videoDisplayControl != null) return;

            _videoDisplayControl = VideoDisplayPositionControl.TryCreate(_videoSession);

            if (_videoDisplayControl != null)
            {
                // Applique tout de suite la dernière taille de fenêtre demandée, plutôt que
                // d'attendre que l'utilisateur redimensionne à nouveau pour que ça "rattrape".
                if (_lastRequestedWidth > 0 && _lastRequestedHeight > 0)
                    _videoDisplayControl.Resize(_lastRequestedWidth, _lastRequestedHeight);
                return;
            }

            if (attemptsLeft > 0)
            {
                var retryTimer = new System.Windows.Forms.Timer { Interval = 400 };
                retryTimer.Tick += (s, e) =>
                {
                    retryTimer.Stop();
                    retryTimer.Dispose();
                    EnsureVideoDisplayControl(attemptsLeft - 1);
                };
                retryTimer.Start();
            }
        }

        private void StartVideoSession(VideoDeviceInfo videoDevice, IntPtr renderHwnd, int targetWidth, int targetHeight, int targetFps, bool previewVsync)
        {
            _videoSourceActivate = videoDevice.Activate;
            _videoSource = _videoSourceActivate.ActivateObject<MediaSource>();
            _videoProcAmp = VideoProcAmpControl.TryCreate(_videoSource);
            ActiveVideoSymbolicLink = videoDevice.SymbolicLink;
            ActiveVideoFriendlyName = videoDevice.FriendlyName;

            MediaFactory.CreateTopology(out Topology topology);
            _videoTopology = topology;

            _videoSource.CreatePresentationDescriptor(out PresentationDescriptor presDescriptor);

            if (_videoSettings != null && _videoSettings.NeedsGpuRenderer)
            {
                // Le moteur Direct3D 11 lit les samples de Media Foundation et applique
                // la conversion YUV puis l'unsharp mask dans un pixel shader.
                for (int i = 0; i < presDescriptor.StreamDescriptorCount; i++)
                {
                    presDescriptor.GetStreamDescriptorByIndex(i, out RawBool selected, out StreamDescriptor streamDescriptor);
                    if (!selected || streamDescriptor.MediaTypeHandler.MajorType != MediaTypeGuids.Video) continue;
                    ActiveSourceFormat = ApplyBestVideoFormat(streamDescriptor.MediaTypeHandler, targetWidth, targetHeight, targetFps, _videoSettings.SourceFormat);
                    break;
                }
                D3D11CaptureRenderer renderer = null;
                renderer = new D3D11CaptureRenderer(_videoSource, renderHwnd, _videoSettings, _selectedSourceFps, previewVsync,
                    () => { if (ReferenceEquals(_gpuRenderer, renderer)) OnVideoSessionStarted?.Invoke(); },
                    message => { if (ReferenceEquals(_gpuRenderer, renderer)) OnVideoRenderFailure?.Invoke(message); });
                _gpuRenderer = renderer;
                _videoRunning = true;
                _gpuRenderer.Start();
                return;
            }

            for (int i = 0; i < presDescriptor.StreamDescriptorCount; i++)
            {
                presDescriptor.GetStreamDescriptorByIndex(i, out RawBool selected, out StreamDescriptor streamDescriptor);
                if (!selected) continue;

                MediaTypeHandler handler = streamDescriptor.MediaTypeHandler;
                if (handler.MajorType != MediaTypeGuids.Video) continue;

                ActiveSourceFormat = ApplyBestVideoFormat(handler, targetWidth, targetHeight, targetFps, _videoSettings?.SourceFormat);

                TopologyNode sourceNode = CreateSourceNode(_videoTopology, _videoSource, presDescriptor, streamDescriptor);
                MediaFactory.CreateVideoRendererActivate(renderHwnd, out Activate videoRendererActivate);
                _videoRendererActivate = videoRendererActivate;
                TopologyNode outputNode = CreateOutputNode(_videoTopology, videoRendererActivate);
                sourceNode.ConnectOutput(0, outputNode, 0);
            }

            MediaFactory.CreateMediaSession(null, out MediaSession session);
            _videoSession = session;
            _videoSession.SetTopology(SessionSetTopologyFlags.None, _videoTopology);

            _videoRunning = true;
            _videoEventThread = new Thread(() => EventLoop(_videoSession, () => _videoRunning, "vidéo"))
            { IsBackground = true, Name = "MF-Video-EventLoop" };
            _videoEventThread.Start();

            var varStart = new SharpDX.Win32.Variant();
            _videoSession.Start(null, varStart);
        }

        public DeviceColorProfileSet EnsureFactoryColorProfile()
        {
            if (_videoProcAmp == null || string.IsNullOrEmpty(ActiveVideoSymbolicLink))
                return null;

            var snapshot = new ColorProfile();
            _videoProcAmp.TryGet(VideoProcAmpProperty.Brightness, out var b); snapshot.Brightness = b;
            _videoProcAmp.TryGet(VideoProcAmpProperty.Contrast, out var c); snapshot.Contrast = c;
            _videoProcAmp.TryGet(VideoProcAmpProperty.Hue, out var h); snapshot.Hue = h;
            _videoProcAmp.TryGet(VideoProcAmpProperty.Saturation, out var s); snapshot.Saturation = s;

            return DeviceColorProfileStore.EnsureFactoryProfileExists(ActiveVideoSymbolicLink, ActiveVideoFriendlyName, snapshot);
        }

        public void ApplyColorCorrection(ColorProfile profile, int attemptsLeft = 20)
        {
            if (_videoProcAmp == null || profile == null)
                return;

            _videoProcAmp.TrySet(VideoProcAmpProperty.Brightness, profile.Brightness);
            _videoProcAmp.TrySet(VideoProcAmpProperty.Contrast, profile.Contrast);
            _videoProcAmp.TrySet(VideoProcAmpProperty.Hue, profile.Hue);
            _videoProcAmp.TrySet(VideoProcAmpProperty.Saturation, profile.Saturation);

            // Après un démarrage à froid (redémarrage Windows), le pilote peut ne pas être
            // totalement prêt même une fois SessionStarted levé. On vérifie avec un Get,
            // et on retente un peu plus tard si la valeur n'a pas été acceptée.
            bool confirmed = _videoProcAmp.TryGet(VideoProcAmpProperty.Brightness, out var actual)
                              && actual == profile.Brightness;

            if (!confirmed && attemptsLeft > 0)
            {
                var retryTimer = new System.Windows.Forms.Timer { Interval = 400 };
                retryTimer.Tick += (s, e) =>
                {
                    retryTimer.Stop();
                    retryTimer.Dispose();
                    ApplyColorCorrection(profile, attemptsLeft - 1);
                };
                retryTimer.Start();
            }
        }

        private void StartAudioSession(AudioCaptureDeviceInfo audioCaptureDevice)
        {
            _audioSourceActivate = audioCaptureDevice.Activate;
            _audioSource = _audioSourceActivate.ActivateObject<MediaSource>();

            MediaFactory.CreateTopology(out Topology topology);
            _audioTopology = topology;

            _audioSource.CreatePresentationDescriptor(out PresentationDescriptor presDescriptor);

            for (int i = 0; i < presDescriptor.StreamDescriptorCount; i++)
            {
                presDescriptor.GetStreamDescriptorByIndex(i, out RawBool selected, out StreamDescriptor streamDescriptor);
                if (!selected) continue;

                MediaTypeHandler handler = streamDescriptor.MediaTypeHandler;
                if (handler.MajorType != MediaTypeGuids.Audio) continue;

                TopologyNode sourceNode = CreateSourceNode(_audioTopology, _audioSource, presDescriptor, streamDescriptor);

                MediaFactory.CreateAudioRendererActivate(out Activate audioActivate);
                _audioRendererActivate = audioActivate;
                if (!string.IsNullOrEmpty(_audioEndpointId))
                {
                    audioActivate.Set(AudioRendererAttributeKeys.EndpointId, _audioEndpointId);
                }

                TopologyNode outputNode = CreateOutputNode(_audioTopology, audioActivate);
                sourceNode.ConnectOutput(0, outputNode, 0);
            }

            MediaFactory.CreateMediaSession(null, out MediaSession session);
            _audioSession = session;
            _audioSession.SetTopology(SessionSetTopologyFlags.None, _audioTopology);

            _audioRunning = true;
            _audioEventThread = new Thread(() => EventLoop(_audioSession, () => _audioRunning, "audio"))
            { IsBackground = true, Name = "MF-Audio-EventLoop" };
            _audioEventThread.Start();

            var varStart = new SharpDX.Win32.Variant();
            _audioSession.Start(null, varStart);
        }


        internal static string NameOfFormat(Guid subtype)
        {
            if (subtype == MediaEngine.VideoYuy2) return "YUY2";
            if (subtype == MediaEngine.VideoNv12) return "NV12";
            if (subtype == MediaEngine.VideoMjpg) return "MJPEG";
            if (subtype == MediaEngine.VideoRgb32) return "RGB32";
            if (subtype == MediaEngine.VideoUyvy) return "UYVY";
            if (subtype == MediaEngine.VideoH264) return "H264";
            if (subtype == MediaEngine.VideoHevc) return "HEVC";
            if (subtype == MediaEngine.VideoP010) return "P010";
            return subtype.ToString();
        }

        private string ApplyBestVideoFormat(MediaTypeHandler handler, int wantedWidth,
            int wantedHeight, int targetFps, string requestedFormat)
        {
            var modes = CaptureModeSelector.Enumerate(handler);
            AvailableVideoModes.AddRange(modes);
            var ranked = CaptureModeSelector.Rank(modes, wantedWidth,
                wantedHeight, targetFps, requestedFormat);
            bool manualFormat = !string.IsNullOrWhiteSpace(requestedFormat) &&
                !string.Equals(requestedFormat, "Auto", StringComparison.OrdinalIgnoreCase);
            if (manualFormat && ranked.Count == 0)
                throw new InvalidOperationException(Localization.F(
                    "Format {0} indisponible : le pilote ne propose aucun mode de capture dans ce format. Choisis Auto ou un format annoncé dans Modes de la carte.",
                    requestedFormat));
            CaptureVideoMode chosen = null;
            Exception lastError = null;
            // A driver can advertise a type that it then rejects. Try the next
            // native mode instead of aborting the entire capture immediately.
            foreach (var candidate in ranked)
            {
                try
                {
                    using (var type = handler.GetMediaTypeByIndex(candidate.NativeIndex))
                        handler.CurrentMediaType = type;
                    // Some drivers accept SetCurrentMediaType but negotiate a different
                    // subtype. Validate the readback so a forced YUY2 cannot be shown
                    // as successful if the device actually selected NV12.
                    using (var accepted = handler.CurrentMediaType)
                    {
                        string actualFormat = NameOfFormat(accepted.Get<Guid>(MediaTypeAttributeKeys.Subtype));
                        if (actualFormat != candidate.Format)
                            throw new InvalidOperationException(Localization.F(
                                "Le pilote a remplacé le format demandé {0} par {1}.",
                                candidate.Format, actualFormat));
                    }
                    chosen = candidate;
                    break;
                }
                catch (Exception ex) { lastError = ex; }
            }
            if (chosen == null)
            {
                if (manualFormat)
                    throw new InvalidOperationException(Localization.F(
                        "Format {0} refusé : aucun mode annoncé dans ce format n'a été accepté par le pilote. La capture ne bascule pas automatiquement vers NV12 ; choisis Auto si tu veux un repli.",
                        requestedFormat), lastError);
                throw new InvalidOperationException(
                    Localization.T("Aucun mode vidéo utilisable (YUY2/UYVY/NV12/RGB32/P010/MJPEG/H264/HEVC) n’a été accepté par ce périphérique."), lastError);
            }

            _selectedSourceFps = chosen.Fps;
            ActiveVideoMode = chosen;
            var warning = new List<string>();
            if (chosen.Width != wantedWidth || chosen.Height != wantedHeight)
                warning.Add(Localization.T("Résolution de repli : la carte ne propose pas le mode demandé."));
            if (targetFps > 0 && Math.Abs(chosen.Fps - targetFps) > 1.1)
                warning.Add(Localization.T("FPS de repli : fréquence demandée indisponible."));
            // With an explicit format, a lower FPS / resolution is allowed, but
            // another format is never an acceptable fallback.
            if (chosen.Format == "P010")
                warning.Add(Localization.T("P010 10 bits : rendu SDR uniquement, sans tone mapping HDR."));
            if (chosen.UncompressedMiBPerSecond > 350)
                warning.Add(Localization.F("Flux brut ~{0} Mio/s : vérifier le débit USB/PCIe réel.",
                    chosen.UncompressedMiBPerSecond.ToString("0")));
            else if (chosen.UncompressedMiBPerSecond > 110)
                warning.Add(Localization.T("Flux non compressé : connexion USB 3.x/PCIe conseillée."));
            ActiveVideoWarning = string.Join("  ", warning);
            return chosen.Description;
        }

        private static TopologyNode CreateSourceNode(Topology topology, MediaSource source, PresentationDescriptor pd, StreamDescriptor sd)
        {
            MediaFactory.CreateTopologyNode(TopologyType.SourceStreamNode, out TopologyNode node);
            node.Set(TopologyNodeAttributeKeys.Source, source);
            node.Set(TopologyNodeAttributeKeys.PresentationDescriptor, pd);
            node.Set(TopologyNodeAttributeKeys.StreamDescriptor, sd);
            topology.AddNode(node);
            return node;
        }

        private static TopologyNode CreateOutputNode(Topology topology, Activate rendererActivate)
        {
            MediaFactory.CreateTopologyNode(TopologyType.OutputNode, out TopologyNode node);
            node.Object = rendererActivate;
            topology.AddNode(node);
            return node;
        }

        private void EventLoop(MediaSession session, Func<bool> isRunning, string label)
        {
            while (isRunning() && session != null &&
                (label == "vidéo" ? ReferenceEquals(session, _videoSession) : ReferenceEquals(session, _audioSession)))
            {
                try
                {
                    using (MediaEvent evt = session.GetEvent(false))
                    {
                        // Ignore les notifications tardives d'une ancienne session.
                        if (label == "vidéo" && !ReferenceEquals(session, _videoSession)) break;
                        if (label != "vidéo" && !ReferenceEquals(session, _audioSession)) break;
                        if (evt.TypeInfo == MediaEventTypes.SessionStarted && label == "vidéo")
                        {
                            OnVideoSessionStarted?.Invoke();
                        }

                        if (evt.TypeInfo == MediaEventTypes.SessionEnded ||
                            evt.TypeInfo == MediaEventTypes.SessionClosed)
                        {
                            break;
                        }

                        int status = (int)evt.Status;
                        if (status < 0)
                        {
                            string message = Localization.F("Erreur Media Foundation ({0}, code 0x{1:X8}) sur l'évènement {2}.", label, status, evt.TypeInfo);
                            if (label == "vidéo") OnVideoRenderFailure?.Invoke(message);
                            else OnError?.Invoke(message);
                        }
                    }
                }
                catch (SharpDXException)
                {
                    break;
                }
            }
        }

        public void ResizeVideoWindow(int width, int height)
        {
            _lastRequestedWidth = width;
            _lastRequestedHeight = height;
            _videoDisplayControl?.Resize(width, height);
            _gpuRenderer?.Resize(width, height);
        }

        public void SetVolume(float volume0to1)
        {
            CurrentVolume = Math.Max(0f, Math.Min(1f, volume0to1));

            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    MMDevice device = !string.IsNullOrEmpty(_audioEndpointId)
                        ? enumerator.GetDevice(_audioEndpointId)
                        : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        if (sessions[i].GetProcessID == pid)
                        {
                            sessions[i].SimpleAudioVolume.Volume = CurrentVolume;
                        }
                    }
                }
            }
            catch
            {
                // Le contrôle de volume par session peut échouer selon le pilote audio ; non bloquant.
            }
        }

        public void Stop()
        {
            _videoRunning = false;
            _audioRunning = false;
            // Le lecteur DirectX doit quitter ReadSample AVANT l'arrêt de sa source.
            if (_gpuRenderer != null)
            {
                try { _gpuRenderer.Dispose(); }
                catch (Exception)
                {
                    // Le thread du pilote peut refuser de terminer. On poursuit
                    // malgré tout la libération de la session et des activateurs ;
                    // ne jamais empêcher l'utilisateur d'ouvrir les Options.
                }
                finally { _gpuRenderer = null; }
            }

            try { _videoSession?.Stop(); } catch { }
            try { _videoSession?.Close(); } catch { }
            try { _audioSession?.Stop(); } catch { }
            try { _audioSession?.Close(); } catch { }

            _videoEventThread?.Join(500);
            _audioEventThread?.Join(500);

            try { _videoSession?.Dispose(); } catch { } finally { _videoSession = null; }
            try { _audioSession?.Dispose(); } catch { } finally { _audioSession = null; }

            try { _videoTopology?.Dispose(); } catch { } finally { _videoTopology = null; }
            try { _audioTopology?.Dispose(); } catch { } finally { _audioTopology = null; }

            try { _videoProcAmp?.Dispose(); } catch { } finally { _videoProcAmp = null; }
            ActiveVideoSymbolicLink = null;
            ActiveVideoFriendlyName = null;
            ActiveSourceFormat = "";
            ActiveVideoWarning = "";
            AvailableVideoModes.Clear();
            _selectedSourceFps = 60.0;
            ActiveVideoMode = null;
            _videoSettings = null;

            try { _videoDisplayControl?.Dispose(); } catch { } finally { _videoDisplayControl = null; }

            // Important : c'est l'activateur qui doit libérer sa référence cachée.
            // ShutdownObject() détruit l'instance actuelle et permet à ActivateObject()
            // de créer une source NEUVE après un ReloadCapture() (vidéo ET audio).
            ShutdownActivatedSource(_videoSourceActivate, _videoSource);
            try { _videoSource?.Dispose(); } catch { } finally { _videoSource = null; }
            _videoSourceActivate = null;
            ShutdownActivatedSource(_audioSourceActivate, _audioSource);
            try { _audioSource?.Dispose(); } catch { } finally { _audioSource = null; }
            _audioSourceActivate = null;

            try { _videoRendererActivate?.Dispose(); } catch { } finally { _videoRendererActivate = null; }
            try { _audioRendererActivate?.Dispose(); } catch { } finally { _audioRendererActivate = null; }
        }

        private static void ShutdownActivatedSource(Activate activate, MediaSource source)
        {
            if (activate != null)
            {
                try
                {
                    activate.ShutdownObject();
                    return;
                }
                catch
                {
                    // Pilote ou activation déjà arrêtée : libérer au mieux la source.
                }
            }
            try { source?.Shutdown(); } catch { }
        }

        public void Dispose() => Stop();

        private VideoProcAmpControl _videoProcAmp;

        /// <summary>
        /// Null tant qu'aucune capture vidéo n'est active, ou si le device ne supporte pas
        /// les réglages matériels (Brightness/Contrast/Hue/Saturation).
        /// </summary>
        public VideoProcAmpControl VideoProcAmp => _videoProcAmp;
    }
}
