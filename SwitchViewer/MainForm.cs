using System;
using System.Drawing;
using System.Windows.Forms;

namespace SwitchViewer
{
    public class MainForm : Form
    {
        private readonly CaptureSession _capture = new CaptureSession();
        private readonly Panel _renderSurface = new Panel();
        private AppConfig _config;

        private bool _isFullscreen;
        private FormWindowState _previousState;
        private FormBorderStyle _previousBorderStyle;
        private Rectangle _previousBounds;

        public MainForm()
        {
            Text = "Broadcaster";
            BackColor = Color.Black;
            KeyPreview = true;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;

            _renderSurface.Dock = DockStyle.Fill;
            _renderSurface.BackColor = Color.Black;
            Controls.Add(_renderSurface);

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
                    }));
                }
            };
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            Cursor.Show();
            _config = AppConfig.Load();
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

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.PrimaryScreen.Bounds;
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
                    StartCaptureFromConfig();
                }
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
                        "SwitchViewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

                _capture.Start(videoDevice, audioOutputDevice, audioCaptureDevice, _renderSurface.Handle, 1920, 1080, _config.FPS);
                _capture.SetVolume(_config.Volume);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Impossible de démarrer la capture : " + ex.Message,
                    "SwitchViewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _capture.Dispose();
        }
    }
}
