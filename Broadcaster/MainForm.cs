using System;
using System.Drawing;
using System.Windows.Forms;

namespace Broadcaster
{
    public class MainForm : Form
    {
        private readonly CaptureSession _capture = new CaptureSession();
        private readonly Panel _renderSurface = new Panel();

        private const int NoSignalBorderMargin = 8;

        private readonly PictureBox _noSignalPictureBox = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            Visible = false
        };
        private NoSignalDetector _noSignalDetector;
        private AppConfig _config;

        private bool _isFullscreen;
        private FormWindowState _previousState;
        private FormBorderStyle _previousBorderStyle;
        private Rectangle _previousBounds;

        public MainForm()
        {
            Text = "Broadcaster";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BackColor = Color.Black;
            KeyPreview = true;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;


            _renderSurface.Dock = DockStyle.Fill;
            _renderSurface.BackColor = Color.Black;
            Controls.Add(_renderSurface);
            Controls.Add(_noSignalPictureBox);

            KeyDown += MainForm_KeyDown;
            Shown += MainForm_Shown;
            FormClosing += MainForm_FormClosing;

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
                    }));
                }
            };

            _renderSurface.Resize += (s, e) =>
            {
                _capture.ResizeVideoWindow(_renderSurface.ClientSize.Width, _renderSurface.ClientSize.Height);
                UpdateNoSignalBounds();
            };

            ResizeEnd += MainForm_ResizeEnd;

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
        }

        private Screen GetTargetScreen()
        {
            if (_config != null && !string.IsNullOrEmpty(_config.DisplayDeviceName))
            {
                var match = Array.Find(Screen.AllScreens, s => s.DeviceName == _config.DisplayDeviceName);
                if (match != null) return match;
            }
            return Screen.PrimaryScreen;
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
        private bool SampleIsDark()
        {
            return ScreenDarknessSampler.IsControlDark(_renderSurface, _noSignalPictureBox.Visible);
        }

        private void MainForm_ResizeEnd(object sender, EventArgs e)
        {
            if (_config != null && _config.HasVideoDevice)
                StartCaptureFromConfig();
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            Cursor.Show();
            _config = AppConfig.Load();
            if (_config.NoSignalDetectionEnabled)
                _noSignalDetector.Start();

            EnterFullscreen();

            if (_config.HasVideoDevice)
                StartCaptureFromConfig();
            else
                OpenOptions();
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11)
            {
                ToggleFullscreen();
                if (_config != null && _config.HasVideoDevice)
                    StartCaptureFromConfig();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                OpenOptions();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.R)
            {
                StartCaptureFromConfig();
                e.Handled = true;
            }
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

        private void MoveFullscreenToConfiguredScreen()
        {
            if (!_isFullscreen) return;

            var targetScreen = GetTargetScreen();
            WindowState = FormWindowState.Normal;
            Bounds = targetScreen.Bounds;
            WindowState = FormWindowState.Maximized;
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

        private void OpenOptions()
        {
            bool wasFullscreen = _isFullscreen;
            Cursor.Show();

            using (var form = new OptionsForm(_config, _capture))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    _config = form.ResultConfig;
                    _config.Save();
                    MoveFullscreenToConfiguredScreen();
                    StartCaptureFromConfig();
                }
            }

            if (_config.NoSignalDetectionEnabled)
            {
                _noSignalDetector.Start(); // sans effet si déjà démarré
            }
            else
            {
                _noSignalDetector.Stop();
                _noSignalPictureBox.Visible = false; // au cas où l'image était affichée au moment de la désactivation
            }

            if (wasFullscreen && _isFullscreen) Cursor.Hide();
        }

        private void StartCaptureFromConfig()
        {
            try
            {
                var videoDevices = MediaEngine.EnumerateVideoDevices();
                var videoDevice = videoDevices.Find(d => d.SymbolicLink == _config.VideoSymbolicLink);

                if (videoDevice == null)
                {
                    MessageBox.Show(this, "Le périphérique vidéo configuré est introuvable. Ouvre les options (Échap) pour en choisir un autre.",
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

                _capture.Start(videoDevice, audioOutputDevice, audioCaptureDevice, _renderSurface.Handle, _config.Width, _config.Height, _config.FPS);
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
            _capture.Dispose();
            _noSignalDetector?.Dispose();
        }
    }
}
