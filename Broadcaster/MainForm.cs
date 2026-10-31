using System;
using System.Drawing;
using System.Windows.Forms;

namespace Broadcaster
{
    public class MainForm : Form
    {
        private readonly CaptureSession _capture = new CaptureSession();
        private readonly Panel _renderSurface = new Panel();
        private AppConfig _config;

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

            Shown += MainForm_Shown;
            FormClosing += MainForm_FormClosing;
            ResizeEnd += MainForm_ResizeEnd;

            _capture.OnError += message =>
            {
                if (IsHandleCreated)
                {
                    BeginInvoke((Action)(() =>
                        MessageBox.Show(this, message, "Erreur de capture", MessageBoxButtons.OK, MessageBoxIcon.Error)));
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

            _autoReloadTimer.Tick += (s, e) =>
            {
                _autoReloadTimer.Stop(); // une seule fois
                if (!_isOptionsOpen) ReloadCapture();
            };
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            _config = AppConfig.Load();
            EnterFullscreen();

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
                    "Impossible d'enregistrer la capture d'écran : " + ex.Message,
                    "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

            try
            {
                using (var form = new OptionsForm(_config, _capture))
                {
                    if (form.ShowDialog(this) == DialogResult.OK)
                    {
                        _config = form.ResultConfig;
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
            }
        }

        // --- Capture ---
        private void StartCaptureFromConfig()
        {
            try
            {
                var videoDevices = MediaEngine.EnumerateVideoDevices();
                var videoDevice = videoDevices.Find(d => d.SymbolicLink == _config.VideoSymbolicLink);

                if (videoDevice == null)
                {
                    MessageBox.Show(this, "Le périphérique vidéo configuré est introuvable. Ouvre les options pour en choisir un autre.",
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
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Impossible de démarrer la capture : " + ex.Message,
                    "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _autoReloadTimer.Stop();
            _autoReloadTimer.Dispose();
            _noSignalDetector?.Dispose();
            _capture.Dispose();
        }
    }
}