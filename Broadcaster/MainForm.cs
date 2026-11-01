using System;
using System.Drawing;
using System.Windows.Forms;

namespace Broadcaster
{
    public class MainForm : Form, IMessageFilter
    {
        private readonly CaptureSession _capture = new CaptureSession();
        private readonly Panel _renderSurface = new Panel();
        private AppConfig _config;

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

            _capture.OnError += message =>
            {
                if (IsHandleCreated)
                {
                    BeginInvoke((Action)(() =>
                        MessageBox.Show(this, message, Localization.T("Erreur de capture"), MessageBoxButtons.OK, MessageBoxIcon.Error)));
                }
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
                _isOptionsOpen || _config == null || !_config.ClipsEnabled ||
                !(ContainsFocus || Form.ActiveForm == this))
                return false;

            Keys pressed = ((Keys)message.WParam.ToInt32() & Keys.KeyCode) | ModifierKeys;
            if (pressed != _config.Bindings.Clip) return false;
            if ((message.LParam.ToInt64() & 0x40000000) == 0)
                TakeClip();
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
                SetCursorHidden(cursorWasHidden);
                UpdateClipCaptureBounds();
            }
        }

        // --- Capture ---
        private void StartCaptureFromConfig()
        {
            // L'ancienne session ne doit pas rester dans le replay après un reload.
            _clipRecorder.Stop();
            try
            {
                var videoDevices = MediaEngine.EnumerateVideoDevices();
                var videoDevice = videoDevices.Find(d => d.SymbolicLink == _config.VideoSymbolicLink);

                if (videoDevice == null)
                {
                    MessageBox.Show(this, Localization.T("Le périphérique vidéo configuré est introuvable. Ouvre les options pour en choisir un autre."),
                        "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

                _capture.Start(videoDevice, audioOutputDevice, audioCaptureDevice, _renderSurface.Handle, _config.Width, _config.Height);
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
                MessageBox.Show(this, Localization.F("Impossible de démarrer la capture : {0}", ex.Message),
                    "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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