using System;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;

namespace Broadcaster
{
    public class MainForm : Form, IMessageFilter
    {
        private readonly CaptureSession _capture = new CaptureSession();
        private readonly Panel _renderSurface = new Panel();
        private AppConfig _config;
        // Overlay indépendant du HWND D3D/EVR. Ne modifie ni le pipeline ni les samples.
        private DebugPerformanceOverlay _debugOverlay;
        private ProcessPerformanceMonitor _debugMonitor;
        private readonly System.Windows.Forms.Timer _debugRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly Stopwatch _debugFpsClock = new Stopwatch();
        private long _lastReceivedSamples = -1;
        private long _lastPresentedSamples = -1;

        // Replay : tampon vidéo/audio des dernières secondes (désactivé par défaut).
        private readonly ReplayClipRecorder _clipRecorder = new ReplayClipRecorder();
        private readonly System.Windows.Forms.Timer _clipRegionTimer =
            new System.Windows.Forms.Timer { Interval = 200 };

        // --- Plein écran ---
        private bool _isFullscreen;
        private FormWindowState _previousState;
        private FormBorderStyle _previousBorderStyle;
        private Rectangle _previousBounds;

        // --- Image "pas de signal" ---
        private const int NoSignalBorderMargin = 8;
        private readonly PictureBox _noSignalPictureBox = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            Visible = false
        };
        private NoSignalDetector _noSignalDetector;

        // --- Reload automatique au démarrage ---
        private const int AutoReloadDelayMs = 3000;
        private readonly System.Windows.Forms.Timer _autoReloadTimer = new System.Windows.Forms.Timer { Interval = AutoReloadDelayMs };
        private bool _autoReloadScheduled;

        // --- Menu options / curseur ---
        private bool _isOptionsOpen;
        private bool _cursorHidden;
        // Une erreur asynchrone de démarrage GPU ne doit pas emprisonner l'utilisateur
        // dans une fenêtre vidéo noire : au plus un repli Auto par session ratée.
        private bool _videoRecoveryPending;
        private bool _unrecoverableErrorShown;
        private bool _reopenOptionsScheduled;

        public MainForm()
        {
            Text = "Broadcaster";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BackColor = Color.Black;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;

            _renderSurface.Dock = DockStyle.Fill;
            _renderSurface.BackColor = Color.Black;
            Controls.Add(_renderSurface);
            Controls.Add(_noSignalPictureBox);
            Application.AddMessageFilter(this);

            Shown += MainForm_Shown;
            FormClosing += MainForm_FormClosing;
            ResizeEnd += MainForm_ResizeEnd;
            Move += (s, e) => PositionDebugOverlay();
            SizeChanged += (s, e) => PositionDebugOverlay();
            _debugRefreshTimer.Tick += (s, e) => RefreshDebugOverlay();

            _capture.OnError += message =>
            {
                // Erreurs audio et autres diagnostics : une seule boîte par session,
                // pour ne pas empiler des dialogues modaux sur les paramètres.
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke((Action)(() => ShowCaptureErrorOnce(message)));
            };
            _capture.OnVideoRenderFailure += message =>
            {
                // Les échecs SourceReader / D3D11 arrivent sur un thread de capture,
                // parfois APRES le retour réussi de CaptureSession.Start().
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke((Action)(() => HandleVideoRenderFailure(message)));
            };

            _capture.OnVideoSessionStarted += () =>
            {
                if (IsHandleCreated)
                {
                    BeginInvoke((Action)(() =>
                    {
                        var profileSet = _capture.EnsureFactoryColorProfile();
                        _capture.ApplyColorCorrection(profileSet?.ActiveProfile);

                        _capture.EnsureVideoDisplayControl();
                        _capture.ResizeVideoWindow(_renderSurface.ClientSize.Width, _renderSurface.ClientSize.Height);
                        // Le tampon est lancé directement après _capture.Start().
                        // Ne jamais le redémarrer ici : l'évènement SessionStarted peut
                        // arriver après les premières images et vider le tampon.
                        UpdateClipCaptureBounds();

                        // Reload auto : seulement au PREMIER démarrage de la session, sinon
                        // reload -> SessionStarted -> reload -> ... en boucle.
                        if (_config != null && _config.AutoReloadOnStart && !_autoReloadScheduled)
                        {
                            _autoReloadScheduled = true;
                            _autoReloadTimer.Start();
                        }
                    }));
                }
            };

            _renderSurface.Resize += (s, e) =>
            {
                _capture.ResizeVideoWindow(_renderSurface.ClientSize.Width, _renderSurface.ClientSize.Height);
                UpdateNoSignalBounds();
                PositionDebugOverlay();
            };

            _noSignalDetector = new NoSignalDetector(SampleIsDark, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
            _noSignalDetector.NoSignalStateChanged += noSignal =>
            {
                if (noSignal && BackgroundImageStore.Exists())
                {
                    _noSignalPictureBox.Image?.Dispose();
                    _noSignalPictureBox.Image = BackgroundImageStore.LoadDetachedCopy();
                    if (_noSignalPictureBox.Image == null) return;

                    UpdateNoSignalBounds();
                    _noSignalPictureBox.Visible = true;
                    _noSignalPictureBox.BringToFront();
                }
                else
                {
                    _noSignalPictureBox.Visible = false;
                }
            };

            _clipRegionTimer.Tick += (s, e) => UpdateClipCaptureBounds();

            _autoReloadTimer.Tick += (s, e) =>
            {
                _autoReloadTimer.Stop(); // une seule fois
                if (!_isOptionsOpen) ReloadCapture();
            };
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            _config = AppConfig.Load();
            Localization.UseLanguage(_config.Language);
            EnterFullscreen();
            _clipRegionTimer.Start();
            ApplyDebugOverlaySetting();

            if (_config.NoSignalDetectionEnabled)
                _noSignalDetector.Start();

            if (_config.HasVideoDevice)
                StartCaptureFromConfig();
            else
                OpenOptions();
        }

        private void MainForm_ResizeEnd(object sender, EventArgs e)
        {
            ReloadCapture();
        }

        // Le moteur Media Foundation peut prendre le focus dans une fenêtre native.
        // Un filtre des messages de CE thread WinForms récupère F10 même si
        // ProcessCmdKey du formulaire ne le reçoit pas. Pas de raccourci global.
        public bool PreFilterMessage(ref Message message)
        {
            if ((message.Msg != 0x100 && message.Msg != 0x104) ||
                _isOptionsOpen || _config == null ||
                !(ContainsFocus || Form.ActiveForm == this))
                return false;

            Keys pressed = ((Keys)message.WParam.ToInt32() & Keys.KeyCode) | ModifierKeys;
            bool firstPress = (message.LParam.ToInt64() & 0x40000000) == 0;
            // Le raccourci des Options doit rester accessible même si la capture
            // a échoué et même si le composant vidéo possède le focus clavier.
            if (pressed == _config.Bindings.OpenMenu)
            {
                if (firstPress) OpenOptions();
                return true;
            }
            if (!_config.ClipsEnabled || pressed != _config.Bindings.Clip) return false;
            if (firstPress) TakeClip();
            return true;
        }

        // ProcessCmdKey est le premier endroit où WinForms voit une touche : il reçoit TOUT
        // (Échap, Tab, flèches...), contrairement à KeyDown. Même mécanisme que la fenêtre
        // de configuration des raccourcis, donc toute touche assignable fonctionne ici.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_config != null)
            {
                // Bit 30 de lParam = touche déjà enfoncée avant ce message (répétition
                // automatique). On l'ignore : maintenir R ne doit pas relancer en rafale.
                bool isRepeat = (msg.LParam.ToInt64() & 0x40000000) != 0;

                Action action = null;
                if (keyData == KeyBindings.ReservedKey) action = () => { ToggleFullscreen(); ReloadCapture(); };
                else if (keyData == _config.Bindings.OpenMenu) action = OpenOptions;
                else if (keyData == _config.Bindings.Reload) action = ReloadCapture;
                else if (keyData == _config.Bindings.ToggleCursor) action = ToggleCursorVisibility;
                else if (keyData == _config.Bindings.Screenshot) action = TakeScreenshot;
                else if (keyData == _config.Bindings.Clip && _config.ClipsEnabled) action = TakeClip;

                if (action != null)
                {
                    if (!isRepeat) action();
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ReloadCapture()
        {
            if (_config != null && _config.HasVideoDevice)
                StartCaptureFromConfig();
        }

        // --- Capture d'écran (F12 par défaut ; personnalisable dans Extras) ---
        private void TakeScreenshot()
        {
            if (_isOptionsOpen || _config == null ||
                !_renderSurface.IsHandleCreated ||
                string.IsNullOrEmpty(_capture.ActiveVideoSymbolicLink) ||
                _noSignalPictureBox.Visible)
            {
                System.Media.SystemSounds.Exclamation.Play();
                return;
            }

            try
            {
                ScreenshotStore.Save(_renderSurface);
                // Confirmation discrète, sans afficher de fenêtre sur le jeu.
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    Localization.F("Impossible d'enregistrer la capture d'écran : {0}", ex.Message),
                    "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateClipCaptureBounds()
        {
            if (!_clipRecorder.IsRunning) return;
            if (_isOptionsOpen || WindowState == FormWindowState.Minimized ||
                !_renderSurface.IsHandleCreated || !_renderSurface.Visible ||
                _renderSurface.ClientSize.Width <= 0 || _renderSurface.ClientSize.Height <= 0 ||
                string.IsNullOrEmpty(_capture.ActiveVideoSymbolicLink))
            {
                _clipRecorder.SetCaptureBounds(Rectangle.Empty);
                return;
            }
            _clipRecorder.SetCaptureBounds(new Rectangle(
                _renderSurface.PointToScreen(Point.Empty), _renderSurface.ClientSize));
        }

        private async void TakeClip()
        {
            if (_isOptionsOpen || _config == null || !_config.ClipsEnabled ||
                _clipRecorder.IsSaving)
                return;

            if (!_clipRecorder.IsRunning)
            {
                _clipRecorder.WriteDiagnostic("F10 : le tampon des clips n'est pas démarré.");
                MessageBox.Show(this, Localization.T("Le tampon de clips n'est pas démarré. Vérifie la capture vidéo et réessaie."),
                    "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                await _clipRecorder.SaveReplayAsync();
                // Pas d'overlay de débogage sur la vidéo : seule une confirmation sonore.
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            }
            catch (Exception ex)
            {
                _clipRecorder.WriteDiagnostic("Echec d'enregistrement : " + ex);
                if (!IsDisposed && IsHandleCreated)
                {
                    MessageBox.Show(this, Localization.F("Impossible d'enregistrer le clip : {0}\n\nJournal : {1}", ex.Message, _clipRecorder.DiagnosticPath),
                        "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        // --- Curseur ---
        // Cursor.Hide() / Cursor.Show() sont comptés : deux Hide() demandent deux Show() pour
        // revenir. On mémorise donc l'état nous-mêmes et on n'appelle jamais deux fois dans
        // le même sens.
        private void SetCursorHidden(bool hidden)
        {
            if (hidden == _cursorHidden) return;
            _cursorHidden = hidden;
            if (hidden) Cursor.Hide(); else Cursor.Show();
        }

        private void ToggleCursorVisibility() => SetCursorHidden(!_cursorHidden);

        // --- Plein écran / écran cible ---
        private Screen GetTargetScreen()
        {
            if (_config != null && !string.IsNullOrEmpty(_config.DisplayDeviceName))
            {
                var match = Array.Find(Screen.AllScreens, s => s.DeviceName == _config.DisplayDeviceName);
                if (match != null) return match;
            }
            return Screen.PrimaryScreen;
        }

        private void ToggleFullscreen()
        {
            if (_isFullscreen) ExitFullscreen();
            else EnterFullscreen();
        }

        private void EnterFullscreen()
        {
            if (_isFullscreen) return;

            _previousState = WindowState;
            _previousBorderStyle = FormBorderStyle;
            _previousBounds = Bounds;

            var targetScreen = GetTargetScreen();

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = targetScreen.Bounds;
            WindowState = FormWindowState.Maximized;

            _isFullscreen = true;
        }

        private void ExitFullscreen()
        {
            if (!_isFullscreen) return;

            TopMost = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            Bounds = _previousBounds.Width > 0 ? _previousBounds : new Rectangle(100, 100, 1280, 720);

            _isFullscreen = false;
        }

        // Si on change d'écran dans les options PENDANT qu'on est déjà en plein écran.
        private void MoveFullscreenToConfiguredScreen()
        {
            if (!_isFullscreen) return;

            var targetScreen = GetTargetScreen();
            WindowState = FormWindowState.Normal;
            Bounds = targetScreen.Bounds;
            WindowState = FormWindowState.Maximized;
        }

        // --- Image "pas de signal" ---
        private bool SampleIsDark()
        {
            return ScreenDarknessSampler.IsControlDark(_renderSurface, _noSignalPictureBox.Visible);
        }

        private void UpdateNoSignalBounds()
        {
            var r = _renderSurface.Bounds;
            _noSignalPictureBox.Bounds = new Rectangle(
                r.Left + NoSignalBorderMargin,
                r.Top + NoSignalBorderMargin,
                Math.Max(0, r.Width - NoSignalBorderMargin * 2),
                Math.Max(0, r.Height - NoSignalBorderMargin * 2));
        }

        // --- Options ---
        private void OpenOptions()
        {
            // Le curseur doit être visible pour utiliser la fenêtre d'options ; on restaure l'état après.
            bool cursorWasHidden = _cursorHidden;
            SetCursorHidden(false);
            _isOptionsOpen = true;
            _debugOverlay?.Hide();
            _clipRecorder.SetCaptureBounds(Rectangle.Empty);
            _clipRecorder.ClearBuffer();

            try
            {
                using (var form = new OptionsForm(_config, _capture))
                {
                    if (form.ShowDialog(this) == DialogResult.OK)
                    {
                        _config = form.ResultConfig;
                        Localization.UseLanguage(_config.Language);
                        _config.Save();
                        MoveFullscreenToConfiguredScreen();
                        ApplyDebugOverlaySetting();

                        if (_config.NoSignalDetectionEnabled)
                        {
                            _noSignalDetector.Start();
                        }
                        else
                        {
                            _noSignalDetector.Stop();
                            _noSignalPictureBox.Visible = false;
                        }

                        StartCaptureFromConfig();
                    }
                }
            }
            finally
            {
                _isOptionsOpen = false;
                PositionDebugOverlay();
                SetCursorHidden(cursorWasHidden);
                UpdateClipCaptureBounds();
            }
        }

        // --- Capture ---
        private bool TrySwitchActiveFormatToAuto(out ColorProfile automaticProfile)
        {
            automaticProfile = null;
            if (_config == null || string.IsNullOrEmpty(_config.VideoSymbolicLink)) return false;
            var set = DeviceColorProfileStore.Load(_config.VideoSymbolicLink);
            var profile = set?.ActiveProfile;
            if (profile == null || profile.IsDefault ||
                string.IsNullOrWhiteSpace(profile.SourceFormat) ||
                string.Equals(profile.SourceFormat, "Auto", StringComparison.OrdinalIgnoreCase))
                return false;

            profile.SourceFormat = "Auto";
            automaticProfile = profile;
            // Persister immédiatement : la prochaine ouverture des Options et le
            // prochain démarrage doivent afficher Auto, et non l'ancien mode cassé.
            DeviceColorProfileStore.SaveProfile(_config.VideoSymbolicLink, profile);
            return true;
        }

        private void ShowCaptureErrorOnce(string message)
        {
            if (_unrecoverableErrorShown || IsDisposed) return;
            _unrecoverableErrorShown = true;
            MessageBox.Show(this, message, Localization.T("Erreur de capture"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void OpenOptionsAfterCaptureFailure(string message)
        {
            _autoReloadTimer.Stop();
            ShowCaptureErrorOnce(message);
            // Ne jamais enfermer l'utilisateur sur un écran noir. Si la fenêtre
            // Options est déjà ouverte, la laisser libre et ne pas en ouvrir une seconde.
            if (!_reopenOptionsScheduled && !IsDisposed && IsHandleCreated)
            {
                _reopenOptionsScheduled = true;
                BeginInvoke((Action)(() =>
                {
                    _reopenOptionsScheduled = false;
                    if (!_isOptionsOpen && !IsDisposed) OpenOptions();
                }));
            }
        }

        private void HandleVideoRenderFailure(string message)
        {
            if (_isOptionsOpen || _videoRecoveryPending || IsDisposed) return;
            _videoRecoveryPending = true;
            try
            {
                if (TrySwitchActiveFormatToAuto(out ColorProfile automaticProfile))
                {
                    _autoReloadTimer.Stop();
                    // La source GPU a quitté son thread après avoir signalé l'erreur.
                    // Recréer l'activateur Media Foundation et la session en Auto.
                    StartCaptureFromConfig(automaticProfile);
                    return;
                }

                // Auto aussi a échoué : pas de boucle de tentatives infinie.
                // L'utilisateur retrouve les Options pour changer de carte / mode.
                try { _capture.Stop(); } catch { }
                OpenOptionsAfterCaptureFailure(message + "\n" +
                    Localization.T("Ouvre les options pour choisir un autre mode ou périphérique."));
            }
            finally { _videoRecoveryPending = false; }
        }

        private void StartCaptureFromConfig(ColorProfile recoveryProfile = null)
        {
            // L'ancienne session ne doit pas rester dans le replay après un reload.
            _clipRecorder.Stop();
            try
            {
                var videoDevices = MediaEngine.EnumerateVideoDevices();
                var videoDevice = videoDevices.Find(d => d.SymbolicLink == _config.VideoSymbolicLink);

                if (videoDevice == null)
                {
                    OpenOptionsAfterCaptureFailure(Localization.T(
                        "Le périphérique vidéo configuré est introuvable. Ouvre les options pour en choisir un autre."));
                    return;
                }

                AudioDeviceInfo audioOutputDevice = null;
                if (!string.IsNullOrEmpty(_config.AudioEndpointId))
                {
                    audioOutputDevice = MediaEngine.EnumerateAudioOutputDevices()
                        .Find(d => d.EndpointId == _config.AudioEndpointId);
                }

                AudioCaptureDeviceInfo audioCaptureDevice = null;
                if (!string.IsNullOrEmpty(_config.AudioCaptureDeviceName))
                {
                    audioCaptureDevice = MediaEngine.EnumerateAudioCaptureDevices()
                        .Find(d => d.FriendlyName == _config.AudioCaptureDeviceName);
                }

                var activeProfile = recoveryProfile ?? DeviceColorProfileStore.Load(_config.VideoSymbolicLink)?.ActiveProfile;
                try
                {
                    _capture.Start(videoDevice, audioOutputDevice, audioCaptureDevice, _renderSurface.Handle,
                        _config.Width, _config.Height, activeProfile, _config.CaptureTargetFps, _config.PreviewVsync);
                }
                catch (Exception)
                {
                    // Mode manuel indisponible/refusé : un SEUL nouvel essai en Auto.
                    // Ne pas relancer en boucle si Auto échoue également.
                    if (!TrySwitchActiveFormatToAuto(out ColorProfile automaticProfile)) throw;
                    activeProfile = automaticProfile;
                    _capture.Start(videoDevice, audioOutputDevice, audioCaptureDevice, _renderSurface.Handle,
                        _config.Width, _config.Height, activeProfile, _config.CaptureTargetFps, _config.PreviewVsync);
                }
                _unrecoverableErrorShown = false;
                _capture.SetVolume(_config.Volume);

                // Lancer le tampon SANS attendre SessionStarted : le callback n'est
                // pas garanti sur tous les pilotes/carte de capture.
                if (_config.ClipsEnabled)
                {
                    try
                    {
                        _clipRecorder.Start(_config.ClipDurationSeconds, _config.AudioEndpointId,
                            _config.Width, _config.Height, _config.ClipTargetFps);
                        UpdateClipCaptureBounds();
                    }
                    catch (Exception clipError)
                    {
                        _clipRecorder.WriteDiagnostic("Démarrage du tampon impossible : " + clipError);
                        MessageBox.Show(this, Localization.F("Impossible de démarrer les clips : {0}", clipError.Message),
                            "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                OpenOptionsAfterCaptureFailure(Localization.F(
                    "Impossible de démarrer la capture : {0}", ex.Message));
            }
        }

        // L'overlay est volontairement une petite fenêtre transparente aux clics,
        // possédée par ce formulaire, pour passer AU-DESSUS du rendu natif D3D11/EVR.
        private void ApplyDebugOverlaySetting()
        {
            if (_config == null || !_config.ShowDebugOverlay)
            {
                _debugRefreshTimer.Stop();
                _debugOverlay?.Close();
                _debugOverlay?.Dispose();
                _debugOverlay = null;
                _debugMonitor?.Dispose();
                _debugMonitor = null;
                return;
            }

            if (_debugOverlay == null)
            {
                _debugOverlay = new DebugPerformanceOverlay();
                _debugMonitor = new ProcessPerformanceMonitor();
                _debugMonitor.Start();
                _lastReceivedSamples = -1;
                _lastPresentedSamples = -1;
                _debugFpsClock.Restart();
                if (!_isOptionsOpen) _debugOverlay.Show(this);
            }
            PositionDebugOverlay();
            _debugRefreshTimer.Start();
            RefreshDebugOverlay();
        }

        private void PositionDebugOverlay()
        {
            if (_debugOverlay == null || _debugOverlay.IsDisposed || !_renderSurface.IsHandleCreated)
                return;
            bool show = !_isOptionsOpen && WindowState != FormWindowState.Minimized && Visible;
            if (!show) { _debugOverlay.Hide(); return; }
            // Bandeau sur toute la largeur, au bord supérieur de la vidéo,
            // quelle que soit la résolution ou la taille de la fenêtre Windows.
            Rectangle surfaceBounds = _renderSurface.RectangleToScreen(_renderSurface.ClientRectangle);
            if (surfaceBounds.Width < 1 || surfaceBounds.Height < 1)
            {
                _debugOverlay.Hide();
                return;
            }
            var bounds = new Rectangle(surfaceBounds.Left, surfaceBounds.Top,
                surfaceBounds.Width, Math.Min(DebugPerformanceOverlay.BandHeight, surfaceBounds.Height));
            if (_debugOverlay.Bounds != bounds) _debugOverlay.Bounds = bounds;
            if (!_debugOverlay.Visible) _debugOverlay.Show(this);
        }

        private void RefreshDebugOverlay()
        {
            if (_debugOverlay == null || _debugOverlay.IsDisposed) return;
            PositionDebugOverlay();
            long received = _capture.ReceivedVideoSamples;
            long presented = _capture.PresentedVideoSamples;
            double elapsed = _debugFpsClock.Elapsed.TotalSeconds;
            bool measurable = received >= 0 && presented >= 0;
            double fpsReceived = 0, fpsPresented = 0;
            if (elapsed > 0.3 && measurable && _lastReceivedSamples >= 0 && _lastPresentedSamples >= 0)
            {
                // Un rechargement de session repart de zéro : pas de FPS négatifs.
                fpsReceived = Math.Max(0, received - (received < _lastReceivedSamples ? 0 : _lastReceivedSamples)) / elapsed;
                fpsPresented = Math.Max(0, presented - (presented < _lastPresentedSamples ? 0 : _lastPresentedSamples)) / elapsed;
            }
            _lastReceivedSamples = received;
            _lastPresentedSamples = presented;
            _debugFpsClock.Restart();

            var stats = _debugMonitor?.Latest;
            var mode = _capture.ActiveVideoMode;
            string cpu = stats != null && stats.CpuPercent >= 0 ?
                stats.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + " %" : Localization.T("n/d");
            string gpu = stats != null && stats.GpuEnginePercent >= 0 ?
                stats.GpuEnginePercent.ToString("0.0", CultureInfo.InvariantCulture) + " %" : Localization.T("n/d");
            string format = mode == null ? Localization.T("Aucune source") : mode.Description;
            string receivedText = !measurable ? Localization.T("n/d (EVR)") :
                fpsReceived.ToString("0.0", CultureInfo.InvariantCulture) + " FPS";
            string presentedText = !measurable ? Localization.T("n/d (EVR)") :
                fpsPresented.ToString("0.0", CultureInfo.InvariantCulture) + " FPS";
            string bitrate = Localization.T("n/d");
            if (mode != null)
            {
                double bpp = mode.Format == "NV12" ? 1.5 : mode.Format == "P010" ? 3.0 :
                    mode.Format == "YUY2" || mode.Format == "UYVY" ? 2.0 :
                    mode.Format == "RGB32" ? 4.0 : 0;
                if (bpp > 0)
                {
                    // Estimation sans overhead USB/PCIe ni blanking HDMI :
                    // en DX11 basée sur les frames reçues, sinon sur le mode négocié.
                    double rate = measurable ? fpsReceived : mode.Fps;
                    double gbps = mode.Width * (double)mode.Height * bpp * rate * 8 / 1e9;
                    bitrate = "~" + gbps.ToString("0.00", CultureInfo.InvariantCulture) + Localization.T(" Gb/s brut");
                }
                else bitrate = Localization.T("n/d (compressé)");
            }
            _debugOverlay.SetMetrics(cpu, gpu, receivedText, presentedText, bitrate, format);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_clipRecorder.IsSaving)
            {
                MessageBox.Show(this,
                    Localization.T("Un clip est en cours d'écriture. Réessaie de fermer Broadcaster après le son de confirmation."),
                    "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
                return;
            }
            _debugRefreshTimer.Stop();
            _debugRefreshTimer.Dispose();
            _debugOverlay?.Close();
            _debugOverlay?.Dispose();
            _debugMonitor?.Dispose();
            Application.RemoveMessageFilter(this);
            _clipRegionTimer.Stop();
            _clipRegionTimer.Dispose();
            _clipRecorder.Dispose();
            _autoReloadTimer.Stop();
            _autoReloadTimer.Dispose();
            _noSignalDetector?.Dispose();
            _capture.Dispose();
        }
    }
}