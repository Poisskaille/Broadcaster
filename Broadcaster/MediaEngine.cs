using System;
using System.Collections.Generic;
using System.Threading;
using SharpDX;
using SharpDX.Mathematics.Interop;
using SharpDX.MediaFoundation;
using NAudio.CoreAudioApi;

namespace Broadcaster
{
    public static class MediaEngine
    {
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
        private Topology _videoTopology;
        private MediaSource _videoSource;
        private Activate _videoRendererActivate;
        private Thread _videoEventThread;
        private volatile bool _videoRunning;
        public bool HasVideoDisplayControl => _videoDisplayControl != null;
        private MediaSession _audioSession;
        private Topology _audioTopology;
        private MediaSource _audioSource;
        private Activate _audioRendererActivate;
        private int _lastRequestedWidth;
        private int _lastRequestedHeight;
        private VideoDisplayPositionControl _videoDisplayControl;
        private Thread _audioEventThread;
        private volatile bool _audioRunning;

        private string _audioEndpointId;

        public event Action<string> OnError;
        public event Action OnVideoSessionStarted;

        public float CurrentVolume { get; private set; } = 1.0f;
        public string ActiveVideoSymbolicLink { get; private set; }
        public string ActiveVideoFriendlyName { get; private set; }


        public void Start(VideoDeviceInfo videoDevice, AudioDeviceInfo audioOutputDevice, AudioCaptureDeviceInfo audioCaptureDevice,
                           IntPtr renderHwnd, int targetWidth, int targetHeight, int targetFps)
        {
            Stop();

            if (videoDevice == null)
                throw new ArgumentNullException(nameof(videoDevice));
            if (videoDevice.Activate == null)
                throw new InvalidOperationException("Le device vidéo n'a pas d'activateur valide. Ouvre les options et resélectionne-le dans la liste.");

            _audioEndpointId = audioOutputDevice?.EndpointId;

            StartVideoSession(videoDevice, renderHwnd, targetWidth, targetHeight, targetFps);

            if (audioCaptureDevice?.Activate != null)
            {
                StartAudioSession(audioCaptureDevice);
            }

            SetVolume(CurrentVolume);
        }

        public void EnsureVideoDisplayControl(int attemptsLeft = 6)
        {
            if (_videoDisplayControl != null) return;

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

        private void StartVideoSession(VideoDeviceInfo videoDevice, IntPtr renderHwnd, int targetWidth, int targetHeight, int targetFps)
        {
            _videoSource = videoDevice.Activate.ActivateObject<MediaSource>();
            _videoProcAmp = VideoProcAmpControl.TryCreate(_videoSource);
            ActiveVideoSymbolicLink = videoDevice.SymbolicLink;
            ActiveVideoFriendlyName = videoDevice.FriendlyName;

            MediaFactory.CreateTopology(out Topology topology);
            _videoTopology = topology;

            _videoSource.CreatePresentationDescriptor(out PresentationDescriptor presDescriptor);

            for (int i = 0; i < presDescriptor.StreamDescriptorCount; i++)
            {
                presDescriptor.GetStreamDescriptorByIndex(i, out RawBool selected, out StreamDescriptor streamDescriptor);
                if (!selected) continue;

                MediaTypeHandler handler = streamDescriptor.MediaTypeHandler;
                if (handler.MajorType != MediaTypeGuids.Video) continue;

                ApplyBestVideoFormat(handler, targetWidth, targetHeight, targetFps);

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
            _audioSource = audioCaptureDevice.Activate.ActivateObject<MediaSource>();

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

        private static void ApplyBestVideoFormat(MediaTypeHandler handler, int wantedWidth, int wantedHeight, int wantedFps)
        {
            MediaType best = null;
            long bestScore = long.MinValue;

            int count = handler.MediaTypeCount;
            for (int i = 0; i < count; i++)
            {
                MediaType mt = handler.GetMediaTypeByIndex(i);

                ulong packedSize = (ulong)mt.Get(MediaTypeAttributeKeys.FrameSize);
                int width = (int)(packedSize >> 32);
                int height = (int)(packedSize & 0xFFFFFFFF);

                ulong packedRate = (ulong)mt.Get(MediaTypeAttributeKeys.FrameRate);
                int fpsNum = (int)(packedRate >> 32);
                int fpsDen = (int)(packedRate & 0xFFFFFFFF);
                double fps = fpsDen == 0 ? 0 : (double)fpsNum / fpsDen;

                long score = 0;
                if (width == wantedWidth && height == wantedHeight) score += 1_000_000;
                if (Math.Abs(fps - wantedFps) < 0.5) score += 500_000;
                score -= Math.Abs(width - wantedWidth) + Math.Abs(height - wantedHeight);
                score -= (long)(Math.Abs(fps - wantedFps) * 100);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = mt;
                }
            }

            if (best != null)
                handler.CurrentMediaType = best;
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
            while (isRunning() && session != null)
            {
                try
                {
                    using (MediaEvent evt = session.GetEvent(false))
                    {
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
                            OnError?.Invoke($"Erreur Media Foundation ({label}, code 0x{status:X8}) sur l'évènement {evt.TypeInfo}.");
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

            try { _videoSession?.Stop(); } catch { }
            try { _videoSession?.Close(); } catch { }
            try { _audioSession?.Stop(); } catch { }
            try { _audioSession?.Close(); } catch { }

            _videoEventThread?.Join(500);
            _audioEventThread?.Join(500);

            _videoSession?.Dispose(); _videoSession = null;
            _audioSession?.Dispose(); _audioSession = null;

            _videoTopology?.Dispose(); _videoTopology = null;
            _audioTopology?.Dispose(); _audioTopology = null;

            _videoProcAmp?.Dispose(); _videoProcAmp = null;
            ActiveVideoSymbolicLink = null;
            ActiveVideoFriendlyName = null;

            _videoDisplayControl?.Dispose(); _videoDisplayControl = null;

            _videoSource?.Shutdown(); _videoSource?.Dispose(); _videoSource = null;
            _audioSource?.Shutdown(); _audioSource?.Dispose(); _audioSource = null;

            _videoRendererActivate?.Dispose(); _videoRendererActivate = null;
            _audioRendererActivate?.Dispose(); _audioRendererActivate = null;
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
