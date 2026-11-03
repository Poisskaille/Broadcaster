using System;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;

namespace Broadcaster
{
    public class OptionsForm : Form
    {
        // --- Onglet Général ---
        private readonly ComboBox _videoCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _audioCaptureCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _audioOutputCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TrackBar _volumeTrack = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10 };
        private readonly Label _volumeLabel = new Label { AutoSize = true };
        private readonly ComboBox _resolutionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _captureFpsCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 225 };
        private static readonly int[] CaptureFpsPresets = { 0, 24, 25, 30, 50, 60, 90, 100, 120, 144, 165, 240 };
        private readonly CheckBox _previewVsyncCheck = new CheckBox { Text = "V-Sync (DX11)", AutoSize = true };

        private readonly ComboBox _screenCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _languageCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };

        private readonly Button _checkUpdateButton = new Button { Text = "Rechercher une mise à jour", AutoSize = true };
        private readonly Button _installUpdateButton = new Button { Text = "Installer la mise à jour", AutoSize = true, Visible = false };
        private UpdateInfo _availableUpdate;
        private bool _isInstallingUpdate;
        private readonly Button _openScreenshotsButton = new Button { Text = "Ouvrir le dossier des captures", AutoSize = true };
        private readonly Label _updateStatusLabel = new Label { AutoSize = true, MaximumSize = new Size(460, 0) };
        private Screen[] _orderedScreens;

        private static readonly (string Label, int Width, int Height)[] ResolutionPresets = new[]
        {
        ("1280 x 720 (HD)", 1280, 720),
        ("1920 x 1080 (Full HD)", 1920, 1080),
        ("2560 x 1440 (2K / QHD)", 2560, 1440),
        ("3840 x 2160 (4K UHD)", 3840, 2160),
};

        private readonly CheckBox _debugOverlayCheck = new CheckBox
        {
            Text = "Afficher la banderole de performances (CPU, GPU, FPS, débit)", AutoSize = true
        };
        private readonly Label _debugOverlayHint = new Label
        {
            Text = "Banderole discrète en haut de la fenêtre vidéo, sans effet sur le flux HDMI.\n" +
                   "Les métriques indisponibles sont signalées.",
            ForeColor = Color.DimGray, AutoSize = true
        };
        private readonly CheckBox _autoReloadCheck = new CheckBox
        {
            Text = "Relancer automatiquement la capture 3 s après le démarrage (corrige le décalage du son)",
            AutoSize = true
        };
        private readonly CheckBox _enableClipsCheck = new CheckBox
        {
            Text = "Activer les clips des dernières secondes", AutoSize = true
        };
        private readonly NumericUpDown _clipDurationInput = new NumericUpDown
        {
            Minimum = 5, Maximum = 120, Increment = 5, Width = 70
        };
        private readonly Label _clipDurationLabel = new Label { Text = "Durée du clip (secondes) :", AutoSize = true };
        private readonly Label _clipFpsLabel = new Label { Text = "FPS cible du clip :", AutoSize = true };
        private readonly ComboBox _clipFpsCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 90
        };
        private static readonly int[] ClipFpsPresets = { 15, 20, 24, 25, 30, 50, 60 };
        private readonly Label _clipInfoLabel = new Label
        {
            Text = "AVI MJPEG à la résolution choisie dans Général (jusqu'à 4K).\n" +
                   "FPS cible : selon les performances du PC ; les grands clips utilisent de la RAM.\n" +
                   "La fenêtre doit rester visible et non recouverte (son Windows si disponible).",
            AutoSize = true, ForeColor = Color.DimGray
        };
        private readonly Button _openClipsButton = new Button { Text = "Ouvrir le dossier des clips", AutoSize = true };
        private readonly Button _keyBindingsButton = new Button { Text = "Raccourcis clavier...", AutoSize = true };
        private KeyBindings _bindings;

        // --- Onglet Vidéo ---
        private readonly ComboBox _profileCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button _newProfileButton = new Button { Text = "Nouveau profil...", AutoSize = true };
        private readonly Button _renameProfileButton = new Button { Text = "Renommer...", AutoSize = true };
        private readonly Button _deleteProfileButton = new Button { Text = "Supprimer", AutoSize = true };
        private readonly Label _colorUnavailableLabel = new Label
        {
            Text = "Sélectionne une source vidéo et clique sur Appliquer une première fois pour pouvoir régler la couleur.",
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            ForeColor = Color.DarkRed,
            Visible = false
        };

        private readonly CheckBox _noSignalEnabledCheck = new CheckBox
        {
            Text = "Afficher l'image en l'absence de signal (10s)",
            AutoSize = true
        };

        private class ColorRow
        {
            public VideoProcAmpProperty Property;
            public readonly TrackBar Track = new TrackBar { TickStyle = TickStyle.BottomRight };
            public readonly Label ValueLabel = new Label { AutoSize = true };
            public bool Supported;
            public int OriginalValue;
        }

        private readonly ColorRow _brightnessRow = new ColorRow { Property = VideoProcAmpProperty.Brightness };
        private readonly ColorRow _contrastRow = new ColorRow { Property = VideoProcAmpProperty.Contrast };
        private readonly ColorRow _hueRow = new ColorRow { Property = VideoProcAmpProperty.Hue };
        private readonly ColorRow _saturationRow = new ColorRow { Property = VideoProcAmpProperty.Saturation };

        // --- Traitement GPU / qualité de la source, stockés par profil personnalisé ---
        private readonly CheckBox _sharpenEnabled = new CheckBox { Text = "Activer la netteté (DirectX 11 / masque flou)", AutoSize = true };
        private readonly TrackBar _sharpenAmount = new TrackBar { Minimum = -100, Maximum = 100, TickFrequency = 20, Width = 375 };
        private readonly Label _sharpenValue = new Label { AutoSize = true };
        private readonly ComboBox _antiAliasingCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 210
        };
        private readonly ComboBox _chromaCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 205
        };
        private readonly CheckBox _debandEnabled = new CheckBox
        {
            Text = "Activer le debanding (dégradés plus lisses)", AutoSize = true
        };
        private readonly TrackBar _debandStrength = new TrackBar
        {
            Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 365
        };
        private readonly Label _debandValue = new Label { AutoSize = true };
        private sealed class SourceChoice
        {
            public string Value { get; }
            public string Label { get; }
            public SourceChoice(string value, string label) { Value = value; Label = label; }
            public override string ToString() => Label;
        }
        private static void AddChoices(ComboBox combo, params string[] values)
        {
            combo.Items.Clear();
            foreach (string value in values)
                combo.Items.Add(new SourceChoice(value, Localization.T(value)));
        }
        private static string SelectedChoice(ComboBox combo) =>
            (combo.SelectedItem as SourceChoice)?.Value ?? "Auto";
        private static void SelectChoice(ComboBox combo, string saved)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if ((combo.Items[i] as SourceChoice)?.Value == saved)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }
        private readonly ComboBox _sourceFormatCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        private readonly ComboBox _yuvMatrixCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        private readonly ComboBox _yuvRangeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        private readonly Button _showCaptureModes = new Button { Text = "Modes de la carte...", Width = 165 };
        private readonly Label _sourceFormatStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(615, 0) };
        private readonly System.Windows.Forms.Timer _videoFpsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private bool _updatingVideoUi;
        private int _previousProfileIndex = -1;

        private readonly Button _okButton = new Button { Text = "Appliquer", DialogResult = DialogResult.OK };
        private readonly Button _cancelButton = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel };
        private readonly PictureBox _backgroundPreview = new PictureBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black
        };
        private readonly Button _chooseBackgroundButton = new Button { Text = "Choisir une image...", AutoSize = true };
        private readonly Button _clearBackgroundButton = new Button { Text = "Retirer l'image", AutoSize = true };

        public AppConfig ResultConfig { get; private set; }

        private readonly AppConfig _initialConfig;
        private readonly CaptureSession _captureSession;
        private bool _colorTabAvailable;
        private DeviceColorProfileSet _profileSet;
        // Les profils doivent rester accessibles quand la capture ne démarre pas.
        // La sélection de la carte dans Général est la référence ; ne pas dépendre
        // uniquement de CaptureSession.ActiveVideoSymbolicLink (null après Stop()).
        private string ProfileDeviceLink =>
            (_videoCombo.SelectedItem as VideoDeviceInfo)?.SymbolicLink ??
            _captureSession?.ActiveVideoSymbolicLink ?? _initialConfig.VideoSymbolicLink;
        private string ProfileDeviceName =>
            (_videoCombo.SelectedItem as VideoDeviceInfo)?.FriendlyName ??
            _captureSession?.ActiveVideoFriendlyName ?? "Broadcaster";



        public OptionsForm(AppConfig currentConfig, CaptureSession captureSession)
        {
            _initialConfig = currentConfig;
            _bindings = currentConfig.Bindings.Clone();
            _captureSession = captureSession;

            Text = "Options - Broadcaster";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(700, 710);

            BuildLayout();
            PopulateLanguageCombo();
            PopulateResolutionCombo();
            PopulateCaptureFpsCombo();
            _previewVsyncCheck.Checked = _initialConfig.PreviewVsync;
            PopulateScreenCombo();
            PopulateDevices();
            PopulateColorTab();
            RefreshVideoEditors();
            Localization.TranslateControls(this);
            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            _okButton.Click += OkButton_Click;
            FormClosing += OptionsForm_FormClosing;
            _videoCombo.SelectedIndexChanged += (s, e) =>
            {
                SavePreviousProfileVideoSettings();
                _previousProfileIndex = -1;
                PopulateColorTab();
                RefreshVideoEditors();
            };
            _profileCombo.SelectedIndexChanged += (s, e) =>
            {
                SavePreviousProfileVideoSettings();
                ApplySelectedProfileToUI();
            };
            _newProfileButton.Click += (s, e) => CreateNewProfile();
            _renameProfileButton.Click += (s, e) => RenameSelectedProfile();
            _deleteProfileButton.Click += (s, e) => DeleteSelectedProfile();
            _videoFpsTimer.Tick += (s, e) => UpdateActiveVideoStatus();
            _showCaptureModes.Click += (s, e) => ShowCaptureModes();
            _videoFpsTimer.Start();
            FormClosed += (s, e) => { _videoFpsTimer.Stop(); _videoFpsTimer.Dispose(); };
        }

        private void ShowCaptureModes()
        {
            var modes = _captureSession?.AvailableVideoModes;
            if (modes == null || modes.Count == 0)
            {
                MessageBox.Show(this, Localization.T("Lance une capture avant de consulter les modes annoncés par la carte."),
                    Localization.T("Modes vidéo"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dialog = new CaptureModesDialog(modes.ToList(), _captureSession.ActiveVideoMode))
                dialog.ShowDialog(this);
        }

        private void UpdateActiveVideoStatus()
        {
            string current = _captureSession?.ActiveSourceFormat;
            double measured = _captureSession?.MeasuredGpuFps ?? 0;
            _sourceFormatStatus.Text = Localization.T("Format actif : ") +
                (string.IsNullOrEmpty(current) ? "—" : current) +
                (measured > 0 ? Localization.T("\nRendu DirectX 11 : ") + measured.ToString("0.0") +
                    Localization.T(" FPS mesurés") : Localization.T("\nRendu EVR / FPS non mesurés")) +
                (string.IsNullOrEmpty(_captureSession?.ActiveVideoWarning) ? "" :
                    "\n" + _captureSession.ActiveVideoWarning);
        }

        private void BuildLayout()
        {
            var tabs = new TabControl { Location = new Point(10, 10), Size = new Size(680, 655) };
            var generalTab = new TabPage("Général");
            var videoTab = new TabPage("Vidéo") { AutoScroll = true };
            tabs.TabPages.Add(generalTab);
            tabs.TabPages.Add(videoTab);
            var extrasTab = new TabPage("Extras");
            tabs.TabPages.Add(extrasTab);

            BuildExtrasTab(extrasTab);
            BuildGeneralTab(generalTab);
            BuildVideoTab(videoTab);

            _okButton.Location = new Point(510, 675);
            _cancelButton.Location = new Point(600, 675);

            Controls.Add(tabs);
            Controls.Add(_okButton);
            Controls.Add(_cancelButton);
        }

        private void PopulateLanguageCombo()
        {
            _languageCombo.Items.Clear();
            foreach (string name in Localization.LanguageNames)
                _languageCombo.Items.Add(Localization.T(name));

            int index = Array.IndexOf(Localization.LanguageCodes,
                Localization.NormalizeMode(_initialConfig.Language));
            _languageCombo.SelectedIndex = index >= 0 ? index : 0;
        }

        private void PopulateScreenCombo()
        {
            var all = Screen.AllScreens;
            var primary = Array.Find(all, s => s.Primary);
            var others = Array.FindAll(all, s => !s.Primary);
            Array.Sort(others, (a, b) => a.Bounds.X.CompareTo(b.Bounds.X)); // gauche à droite

            _orderedScreens = new Screen[all.Length];
            _orderedScreens[0] = primary ?? all[0];
            Array.Copy(others, 0, _orderedScreens, 1, others.Length);

            _screenCombo.Items.Clear();
            for (int i = 0; i < _orderedScreens.Length; i++)
            {
                var s = _orderedScreens[i];
                string suffix = s.Primary ? Localization.T(" (Principal)") : "";
                _screenCombo.Items.Add($"{Localization.T("Écran")} {i + 1}{suffix} - {s.Bounds.Width}x{s.Bounds.Height}");
            }

            int match = Array.FindIndex(_orderedScreens, s => s.DeviceName == _initialConfig.DisplayDeviceName);
            _screenCombo.SelectedIndex = match >= 0 ? match : 0;
        }

        private void BuildExtrasTab(TabPage page)
        {
            var label = new Label
            {
                Text = Localization.T("Image affichée quand aucun signal n’est reçu depuis 10 secondes :"),
                AutoSize = true,
                Location = new Point(15, 15)
            };

            //_backgroundPreview.Location = new Point(15, 45);
            //_backgroundPreview.Size = new Size(320, 180);

            //_chooseBackgroundButton.Location = new Point(350, 45);
            //_clearBackgroundButton.Location = new Point(350, 80);

            //_chooseBackgroundButton.Click += (s, e) => ChooseBackgroundImage();
            //_clearBackgroundButton.Click += (s, e) => ClearBackgroundImage();

            //_noSignalEnabledCheck.Location = new Point(15, 240);
            //_noSignalEnabledCheck.Checked = _initialConfig.NoSignalDetectionEnabled;

            _autoReloadCheck.Location = new Point(15, 15);
            _autoReloadCheck.Checked = _initialConfig.AutoReloadOnStart;

            _keyBindingsButton.Location = new Point(15, 45);
            _keyBindingsButton.Click += (s, e) => OpenKeyBindings();

            _checkUpdateButton.Location = new Point(15, 80);
            _checkUpdateButton.Click += (s, e) => CheckForUpdate();

            _updateStatusLabel.Location = new Point(225, 80);
            _updateStatusLabel.MaximumSize = new Size(420, 65);
            _updateStatusLabel.Text = Localization.F("Version actuelle : {0}", UpdateChecker.CurrentVersion);

            _installUpdateButton.Location = new Point(15, 120);
            _installUpdateButton.Click += async (s, e) => await InstallAvailableUpdateAsync();

            _openScreenshotsButton.Location = new Point(15, 165);
            _openScreenshotsButton.Click += (s, e) => OpenScreenshotsFolder();

            _enableClipsCheck.Location = new Point(15, 211);
            _enableClipsCheck.Checked = _initialConfig.ClipsEnabled;
            _enableClipsCheck.CheckedChanged += (s, e) => RefreshClipControls();
            _clipDurationLabel.Location = new Point(35, 246);
            _clipDurationInput.Location = new Point(220, 242);
            _clipDurationInput.Value = Math.Max(5, Math.Min(120, _initialConfig.ClipDurationSeconds));
            _clipFpsLabel.Location = new Point(35, 277);
            _clipFpsCombo.Location = new Point(220, 273);
            foreach (int value in ClipFpsPresets) _clipFpsCombo.Items.Add(value + " FPS");
            int fpsIndex = Array.IndexOf(ClipFpsPresets, _initialConfig.ClipTargetFps);
            _clipFpsCombo.SelectedIndex = fpsIndex >= 0 ? fpsIndex : 4;
            _clipInfoLabel.Location = new Point(35, 314);
            _openClipsButton.Location = new Point(15, 380);
            _openClipsButton.Click += (s, e) => OpenClipsFolder();
            _debugOverlayCheck.Location = new Point(15, 427);
            _debugOverlayCheck.Checked = _initialConfig.ShowDebugOverlay;
            _debugOverlayHint.Location = new Point(35, 455);
            page.Controls.AddRange(new Control[] {
                _enableClipsCheck, _clipDurationLabel, _clipDurationInput,
                _clipFpsLabel, _clipFpsCombo, _clipInfoLabel, _openClipsButton,
                _debugOverlayCheck, _debugOverlayHint
            });
            RefreshClipControls();



            // Desactivation de la feature du background automatique
            //page.Controls.Add(label);
            //page.Controls.Add(_backgroundPreview);
            //page.Controls.Add(_chooseBackgroundButton);
            //page.Controls.Add(_clearBackgroundButton);
            //page.Controls.Add(_noSignalEnabledCheck);
            page.Controls.Add(_autoReloadCheck);
            page.Controls.Add(_keyBindingsButton);
            page.Controls.Add(_checkUpdateButton);
            page.Controls.Add(_installUpdateButton);
            page.Controls.Add(_updateStatusLabel);
            page.Controls.Add(_openScreenshotsButton);

            RefreshBackgroundPreview();
        }

        private void RefreshClipControls()
        {
            bool enabled = _enableClipsCheck.Checked;
            _clipDurationLabel.Visible = enabled;
            _clipDurationInput.Visible = enabled;
            _clipFpsLabel.Visible = enabled;
            _clipFpsCombo.Visible = enabled;
            _clipInfoLabel.Visible = enabled;
        }

        private void OpenClipsFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo {
                    FileName = ReplayClipRecorder.GetClipsFolder(), UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Localization.F("Impossible d'ouvrir le dossier des clips : {0}", ex.Message),
                    "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenScreenshotsFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ScreenshotStore.GetFolder(),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    Localization.F("Impossible d'ouvrir le dossier des captures : {0}", ex.Message),
                    "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenKeyBindings()
        {
            using (var form = new KeyBindingsForm(_bindings))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                    _bindings = form.Result;
            }
        }

        private async void CheckForUpdate()
        {
            _availableUpdate = null;
            _installUpdateButton.Visible = false;
            _checkUpdateButton.Enabled = false;
            _updateStatusLabel.Text = Localization.T("Recherche en cours...");

            try
            {
                var info = await UpdateChecker.CheckAsync();

                if (!info.IsNewer)
                {
                    _updateStatusLabel.Text = Localization.F("Application à jour (version {0}).", UpdateChecker.CurrentVersion);
                }
                else if (string.IsNullOrWhiteSpace(info.DownloadUrl))
                {
                    _updateStatusLabel.Text = Localization.F("Version {0} disponible, mais aucune archive ZIP n'est jointe à la release.", info.LatestVersion);
                }
                else
                {
                    _availableUpdate = info;
                    _updateStatusLabel.Text = Localization.F("Nouvelle version {0} disponible ! (actuelle : {1})", info.LatestVersion, UpdateChecker.CurrentVersion);
                    _installUpdateButton.Text = Localization.F("Mettre à jour vers {0}", info.LatestVersion);
                    _installUpdateButton.Visible = true;
                }
            }
            catch (Exception ex)
            {
                _updateStatusLabel.Text = Localization.F("Impossible de vérifier : {0}", ex.Message);
            }
            finally
            {
                if (!IsDisposed)
                    _checkUpdateButton.Enabled = true;
            }
        }

        private async System.Threading.Tasks.Task InstallAvailableUpdateAsync()
        {
            UpdateInfo info = _availableUpdate;
            if (_isInstallingUpdate || info == null || !info.IsNewer)
                return;

            if (MessageBox.Show(this,
                Localization.F("Installer Broadcaster {0} depuis GitHub ?\n\nFichier : {1}\n\nL'application se fermera, ses fichiers seront remplacés, puis elle redémarrera. Les paramètres utilisateur seront conservés.", info.LatestVersion, info.AssetName),
                Localization.T("Mise à jour de Broadcaster"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _isInstallingUpdate = true;
            _checkUpdateButton.Enabled = false;
            _installUpdateButton.Enabled = false;
            _okButton.Enabled = false;
            _cancelButton.Enabled = false;
            _updateStatusLabel.Text = Localization.T("Téléchargement de la mise à jour...");

            try
            {
                var progress = new Progress<int>(percent =>
                {
                    if (!IsDisposed)
                        _updateStatusLabel.Text = Localization.F("Téléchargement : {0}%", percent);
                });

                await UpdateInstaller.PrepareAndLaunchAsync(info, progress);
                _updateStatusLabel.Text = Localization.T("Téléchargement terminé. Fermeture pour installation...");
                _isInstallingUpdate = false;
                // Le script attendra la fermeture effective (libération de Broadcaster.exe).
                Application.Exit();
            }
            catch (Exception ex)
            {
                _updateStatusLabel.Text = Localization.F("Mise à jour impossible : {0}", ex.Message);
                MessageBox.Show(this, Localization.F("La mise à jour n'a pas été installée :\n{0}", ex.Message),
                    "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _isInstallingUpdate = false;
                if (!IsDisposed)
                {
                    _checkUpdateButton.Enabled = true;
                    _installUpdateButton.Enabled = true;
                    _okButton.Enabled = true;
                    _cancelButton.Enabled = true;
                }
            }
        }

        private void RefreshBackgroundPreview()
        {
            _backgroundPreview.Image?.Dispose();
            _backgroundPreview.Image = BackgroundImageStore.LoadDetachedCopy(); // null si le fichier n'existe pas, pas de souci

            _clearBackgroundButton.Enabled = BackgroundImageStore.Exists();
        }

        private void ChooseBackgroundImage()
        {
            using (var dialog = new OpenFileDialog
            {
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
                Title = Localization.T("Choisir une image de fond")
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        BackgroundImageStore.SetFrom(dialog.FileName);
                        RefreshBackgroundPreview();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, Localization.F("Impossible de charger cette image : {0}", ex.Message),
                            "SwitchViewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        private void ClearBackgroundImage()
        {
            BackgroundImageStore.Clear();
            RefreshBackgroundPreview();
        }

        private void BuildGeneralTab(TabPage page)
        {
            var screenLabel = new Label { Text = "Écran (plein écran) :", AutoSize = true, Location = new Point(15, 15) };
            _screenCombo.Location = new Point(15, 38);
            _screenCombo.Width = 300;

            var resolutionLabel = new Label { Text = "Résolution :", AutoSize = true, Location = new Point(15, 75) };
            _resolutionCombo.Location = new Point(15, 98);
            _resolutionCombo.Width = 250;

            var captureFpsLabel = new Label { Text = "FPS de capture (carte HDMI) :", AutoSize = true, Location = new Point(302, 75) };
            _captureFpsCombo.Location = new Point(302, 98);
            _previewVsyncCheck.Location = new Point(540, 100);
            var vsyncHint = new ToolTip();
            vsyncHint.SetToolTip(_previewVsyncCheck,
                Localization.T("Supprime le tearing mais peut limiter les FPS de l’aperçu à la fréquence de l’écran."));
            var volumeTitle = new Label { Text = "Volume :", AutoSize = true, Location = new Point(15, 135) };
            _volumeTrack.Location = new Point(15, 158);
            _volumeTrack.Width = 320;
            _volumeTrack.ValueChanged += (s, e) => _volumeLabel.Text = $"{_volumeTrack.Value}%";
            _volumeLabel.Location = new Point(345, 165);

            var videoLabel = new Label { Text = "Source de capture vidéo :", AutoSize = true, Location = new Point(15, 200) };
            _videoCombo.Location = new Point(15, 223);
            _videoCombo.Width = 405;

            var audioCaptureLabel = new Label { Text = "Capture audio (si distincte de la vidéo)", AutoSize = true, Location = new Point(15, 260) };
            _audioCaptureCombo.Location = new Point(15, 283);
            _audioCaptureCombo.Width = 405;

            var audioOutputLabel = new Label { Text = "Sortie audio :", AutoSize = true, Location = new Point(15, 320) };
            _audioOutputCombo.Location = new Point(15, 343);
            _audioOutputCombo.Width = 405;

            var languageLabel = new Label { Text = "Langue de l'application :", AutoSize = true, Location = new Point(15, 385) };
            _languageCombo.Location = new Point(15, 408);

            page.Controls.AddRange(new Control[]
            {
        languageLabel, _languageCombo,
        screenLabel, _screenCombo,
        resolutionLabel, _resolutionCombo, captureFpsLabel, _captureFpsCombo, _previewVsyncCheck,
        volumeTitle, _volumeTrack, _volumeLabel,
        videoLabel, _videoCombo,
        audioCaptureLabel, _audioCaptureCombo,
        audioOutputLabel, _audioOutputCombo
            });
        }

        private void BuildVideoTab(TabPage page)
        {
            var profileLabel = new Label { Text = "Profil :", AutoSize = true, Location = new Point(15, 18) };
            _profileCombo.Location = new Point(65, 15);
            _profileCombo.Width = 220;
            _newProfileButton.Location = new Point(295, 14);
            _renameProfileButton.Location = new Point(410, 14);

            _colorUnavailableLabel.Location = new Point(15, 50);

            _renameProfileButton.Location = new Point(410, 14);
            _deleteProfileButton.Location = new Point(520, 14);

            int y = 60;
            BuildColorRow(page, _brightnessRow, "Luminosité :", ref y);
            BuildColorRow(page, _contrastRow, "Contraste :", ref y);
            BuildColorRow(page, _hueRow, "Teinte :", ref y);
            BuildColorRow(page, _saturationRow, "Saturation :", ref y);

            // Les quatre curseurs de couleur finissent à y=240. Réglages vidéo juste dessous.
            var gpuGroup = new GroupBox { Text = "Netteté / traitement DirectX 11", Location = new Point(12, 246), Size = new Size(635, 115) };
            _sharpenEnabled.Location = new Point(14, 25);
            var sharpenLabel = new Label { Text = "Douceur / netteté :", AutoSize = true, Location = new Point(14, 73) };
            _sharpenAmount.Location = new Point(165, 54);
            _sharpenValue.Location = new Point(555, 73);
            _sharpenEnabled.CheckedChanged += (s, e) => RefreshVideoEditors();
            _sharpenAmount.ValueChanged += (s, e) => _sharpenValue.Text = _sharpenAmount.Value + " %";
            gpuGroup.Controls.AddRange(new Control[] { _sharpenEnabled, sharpenLabel, _sharpenAmount, _sharpenValue });
            page.Controls.Add(gpuGroup);

            // Anti-aliasing écran, après conversion YUV -> RGB, avant sharpen.
            // Ce choix n'affecte PAS le format ou la fréquence des samples HDMI.
            var aaGroup = new GroupBox { Text = "Anti-aliasing DirectX 11", Location = new Point(12, 365), Size = new Size(635, 83) };
            aaGroup.Controls.Add(new Label { Text = "Mode :", AutoSize = true, Location = new Point(14, 26) });
            _antiAliasingCombo.Location = new Point(165, 22);
            _antiAliasingCombo.Items.Add(new SourceChoice("Off", Localization.T("Désactivé")));
            _antiAliasingCombo.Items.Add(new SourceChoice("FXAA", Localization.T("FXAA — rapide")));
            _antiAliasingCombo.Items.Add(new SourceChoice("SMAA", Localization.T("SMAA adapté — contours précis")));
            aaGroup.Controls.Add(_antiAliasingCombo);
            aaGroup.Controls.Add(new Label
            {
                Text = "FXAA : 1 passe GPU. SMAA : plusieurs passes ; plus coûteux en 4K/120 FPS.",
                AutoSize = true, Location = new Point(14, 56), ForeColor = Color.DimGray
            });
            page.Controls.Add(aaGroup);

            // Traitements indépendants du format natif : Standard conserve le rendu
            // actuel, Bicubic/Adaptive ne modifient que l'interpolation chromatique.
            var cleanupGroup = new GroupBox { Text = "Nettoyage de l'image (GPU)", Location = new Point(12, 453), Size = new Size(635, 183) };
            cleanupGroup.Controls.Add(new Label { Text = "Reconstruction chromatique :", AutoSize = true, Location = new Point(14, 27) });
            _chromaCombo.Location = new Point(180, 23);
            _chromaCombo.Items.Add(new SourceChoice("Standard", Localization.T("Standard (actuel)")));
            _chromaCombo.Items.Add(new SourceChoice("Bicubic", Localization.T("Bicubique (qualité)")));
            _chromaCombo.Items.Add(new SourceChoice("Adaptive", Localization.T("Adaptatif (contours)")));
            cleanupGroup.Controls.Add(_chromaCombo);
            cleanupGroup.Controls.Add(new Label
            {
                Text = "Interpolation YUY2/NV12 ; le mode avancé peut coûter plus en 4K/120 FPS.",
                AutoSize = true, Location = new Point(14, 55), MaximumSize = new Size(600, 0), ForeColor = Color.DimGray
            });
            _debandEnabled.Location = new Point(14, 94);
            _debandEnabled.CheckedChanged += (s, e) => RefreshVideoEditors();
            cleanupGroup.Controls.Add(_debandEnabled);
            cleanupGroup.Controls.Add(new Label { Text = "Intensité :", AutoSize = true, Location = new Point(14, 146) });
            _debandStrength.Location = new Point(165, 126);
            _debandStrength.ValueChanged += (s, e) => _debandValue.Text = _debandStrength.Value + " %";
            _debandValue.Location = new Point(555, 146);
            cleanupGroup.Controls.AddRange(new Control[] { _debandStrength, _debandValue });
            page.Controls.Add(cleanupGroup);

            var sourceGroup = new GroupBox { Text = "Qualité de la source HDMI", Location = new Point(12, 642), Size = new Size(635, 162) };
            AddChoices(_sourceFormatCombo, "Auto", "YUY2", "UYVY", "NV12", "RGB32", "P010", "MJPEG", "H264", "HEVC");
            AddChoices(_yuvMatrixCombo, "Auto", "BT.709", "BT.601");
            AddChoices(_yuvRangeCombo, "Auto", "Limited", "Full");
            sourceGroup.Controls.Add(new Label { Text = "Format de capture :", Location = new Point(14, 25), AutoSize = true });
            _sourceFormatCombo.Location = new Point(180, 21);
            _showCaptureModes.Location = new Point(370, 20);
            sourceGroup.Controls.Add(_showCaptureModes);
            sourceGroup.Controls.Add(_sourceFormatCombo);
            sourceGroup.Controls.Add(new Label { Text = "Matrice YUV → RGB :", Location = new Point(14, 58), AutoSize = true });
            _yuvMatrixCombo.Location = new Point(180, 54);
            sourceGroup.Controls.Add(_yuvMatrixCombo);
            sourceGroup.Controls.Add(new Label { Text = "Plage YUV :", Location = new Point(14, 91), AutoSize = true });
            _yuvRangeCombo.Location = new Point(180, 87);
            sourceGroup.Controls.Add(_yuvRangeCombo);
            _sourceFormatStatus.Location = new Point(14, 119);
            UpdateActiveVideoStatus();
            sourceGroup.Controls.Add(_sourceFormatStatus);
            page.Controls.Add(sourceGroup);
            page.AutoScrollMinSize = new Size(0, 820);

            page.Controls.Add(profileLabel);
            page.Controls.Add(_profileCombo);
            page.Controls.Add(_newProfileButton);
            page.Controls.Add(_renameProfileButton);
            page.Controls.Add(_colorUnavailableLabel);
            page.Controls.Add(_deleteProfileButton);
        }

        private void PopulateCaptureFpsCombo()
        {
            _captureFpsCombo.Items.Clear();
            foreach (int rate in CaptureFpsPresets)
                _captureFpsCombo.Items.Add(rate == 0 ? Localization.T("Auto (maximum disponible)") : rate + " FPS");
            int index = Array.IndexOf(CaptureFpsPresets, _initialConfig.CaptureTargetFps);
            _captureFpsCombo.SelectedIndex = index >= 0 ? index : 0;
        }

        private void PopulateResolutionCombo()
        {
            _resolutionCombo.Items.Clear();
            foreach (var preset in ResolutionPresets)
                _resolutionCombo.Items.Add(preset.Label);

            int matchIndex = Array.FindIndex(ResolutionPresets,
                p => p.Width == _initialConfig.Width && p.Height == _initialConfig.Height);

            if (matchIndex < 0)
            {
                // Valeur qui ne correspond à aucun preset (config éditée à la main, par ex.) :
                // on l'affiche telle quelle plutôt que de la remplacer silencieusement.
                _resolutionCombo.Items.Add($"{_initialConfig.Width} x {_initialConfig.Height}{Localization.T(" (personnalisé)")}");
                matchIndex = _resolutionCombo.Items.Count - 1;
            }

            _resolutionCombo.SelectedIndex = matchIndex;
        }

        private void BuildColorRow(TabPage page, ColorRow row, string labelText, ref int y)
        {
            var label = new Label { Text = labelText, AutoSize = true, Location = new Point(15, y) };
            row.Track.Location = new Point(120, y - 5);
            row.Track.Width = 400;
            row.ValueLabel.Location = new Point(530, y);

            // Live preview : quand l'utilisateur bouge un curseur (profil éditable),
            // on pousse la valeur directement sur le matériel, immédiatement.
            row.Track.ValueChanged += (s, e) =>
            {
                row.ValueLabel.Text = row.Track.Value.ToString();
                if (row.Supported)
                    _captureSession?.VideoProcAmp?.TrySet(row.Property, row.Track.Value);
            };

            page.Controls.Add(label);
            page.Controls.Add(row.Track);
            page.Controls.Add(row.ValueLabel);

            y += 45;
        }

        private void PopulateColorTab()
        {
            string deviceLink = ProfileDeviceLink;
            bool deviceReady = !string.IsNullOrEmpty(deviceLink);
            bool isRunningOnSelectedDevice = deviceReady &&
                string.Equals(_captureSession?.ActiveVideoSymbolicLink, deviceLink, StringComparison.Ordinal);
            var procAmp = isRunningOnSelectedDevice ? _captureSession?.VideoProcAmp : null;
            _colorTabAvailable = procAmp != null;

            // Les profils et réglages GPU ne dépendent PAS du support matériel
            // de IAMVideoProcAmp (qui manque sur plusieurs cartes HDMI).
            _profileCombo.Enabled = deviceReady;
            _newProfileButton.Enabled = deviceReady;
            _renameProfileButton.Enabled = false;
            _deleteProfileButton.Enabled = false;
            _colorUnavailableLabel.Visible = !deviceReady || !_colorTabAvailable;
            _colorUnavailableLabel.Text = !deviceReady
                ? Localization.T("Sélectionne une source vidéo et clique une première fois sur Appliquer pour créer ses profils.")
                : Localization.T("Curseurs matériels indisponibles sur cette carte. Les réglages de netteté et de source restent accessibles.");

            if (!deviceReady)
            {
                foreach (var row in new[] { _brightnessRow, _contrastRow, _hueRow, _saturationRow })
                {
                    row.Supported = false;
                    row.Track.Enabled = false;
                    row.ValueLabel.Text = "N/A";
                }
                return;
            }

            if (_colorTabAvailable)
                SetupRowsRanges();
            else
            {
                foreach (var row in new[] { _brightnessRow, _contrastRow, _hueRow, _saturationRow })
                {
                    row.Supported = false;
                    row.Track.Enabled = false;
                    row.ValueLabel.Text = "N/A";
                }
            }

            _profileSet = DeviceColorProfileStore.Load(deviceLink)
                ?? (isRunningOnSelectedDevice ? _captureSession.EnsureFactoryColorProfile() : null)
                ?? DeviceColorProfileStore.EnsureFactoryProfileExists(
                    deviceLink, ProfileDeviceName, new ColorProfile());

            _profileCombo.Items.Clear();
            foreach (var profile in _profileSet.Profiles)
                _profileCombo.Items.Add(profile.IsDefault ? Localization.T("Par défaut") : profile.Name);

            if (_profileCombo.Items.Count == 0) return;
            int index = _profileSet.Profiles.FindIndex(p => p.Name == _profileSet.ActiveProfileName);
            _profileCombo.SelectedIndex = index >= 0 ? index : 0;
            ApplySelectedProfileToUI();
        }

        private void SetupRowsRanges()
        {
            var procAmp = _captureSession.VideoProcAmp;
            SetupRowRange(_brightnessRow, procAmp);
            SetupRowRange(_contrastRow, procAmp);
            SetupRowRange(_hueRow, procAmp);
            SetupRowRange(_saturationRow, procAmp);
        }

        private void SetupRowRange(ColorRow row, VideoProcAmpControl procAmp)
        {
            if (procAmp == null || !procAmp.TryGetRange(row.Property, out var range))
            {
                row.Supported = false;
                row.Track.Enabled = false;
                row.ValueLabel.Text = "N/A";
                return;
            }

            row.Supported = true;
            row.Track.Minimum = range.Min;
            row.Track.Maximum = range.Max;
            row.Track.TickFrequency = Math.Max(1, (range.Max - range.Min) / 10);

            // Snapshot de la valeur RÉELLEMENT présente sur le matériel à l'ouverture,
            // pour pouvoir tout restaurer si l'utilisateur annule.
            row.OriginalValue = procAmp.TryGet(row.Property, out var current) ? current : range.Default;
        }

        private void ApplySelectedProfileToUI()
        {
            if (_profileSet == null || _profileCombo.SelectedIndex < 0) return;

            var profile = _profileSet.Profiles[_profileCombo.SelectedIndex];
            bool editable = !profile.IsDefault;
            _renameProfileButton.Enabled = editable;
            _deleteProfileButton.Enabled = editable;

            SetRowValue(_brightnessRow, profile.Brightness, editable);
            SetRowValue(_contrastRow, profile.Contrast, editable);
            SetRowValue(_hueRow, profile.Hue, editable);
            SetRowValue(_saturationRow, profile.Saturation, editable);

            _updatingVideoUi = true;
            _sharpenEnabled.Checked = editable && profile.SharpenEnabled;
            _sharpenAmount.Value = Clamp(profile.SharpenAmount, -100, 100);
            SelectChoice(_antiAliasingCombo, profile.AntiAliasingMode ?? "Off");
            SelectChoice(_chromaCombo, profile.ChromaUpsamplingMode ?? "Standard");
            _debandEnabled.Checked = editable && profile.DebandEnabled;
            _debandStrength.Value = Clamp(profile.DebandStrength, 0, 100);
            SelectChoice(_sourceFormatCombo, profile.SourceFormat);
            SelectChoice(_yuvMatrixCombo, profile.YuvMatrix);
            SelectChoice(_yuvRangeCombo, profile.YuvRange);
            _updatingVideoUi = false;
            _previousProfileIndex = _profileCombo.SelectedIndex;
            RefreshVideoEditors();
        }

        private void SavePreviousProfileVideoSettings()
        {
            if (_updatingVideoUi || _profileSet == null || _previousProfileIndex < 0 ||
                _previousProfileIndex >= _profileSet.Profiles.Count) return;
            var p = _profileSet.Profiles[_previousProfileIndex];
            if (p.IsDefault) return;
            p.SharpenEnabled = _sharpenEnabled.Checked;
            p.SharpenAmount = _sharpenAmount.Value;
            p.AntiAliasingMode = SelectedChoice(_antiAliasingCombo);
            p.ChromaUpsamplingMode = SelectedChoice(_chromaCombo);
            p.DebandEnabled = _debandEnabled.Checked;
            p.DebandStrength = _debandStrength.Value;
            p.SourceFormat = SelectedChoice(_sourceFormatCombo);
            p.YuvMatrix = SelectedChoice(_yuvMatrixCombo);
            p.YuvRange = SelectedChoice(_yuvRangeCombo);
        }

        private void RefreshVideoEditors()
        {
            bool editable = _profileSet != null && _profileCombo.SelectedIndex >= 0 &&
                !_profileSet.Profiles[_profileCombo.SelectedIndex].IsDefault;
            _sharpenEnabled.Enabled = editable;
            _sharpenAmount.Enabled = editable && _sharpenEnabled.Checked;
            _antiAliasingCombo.Enabled = editable;
            _chromaCombo.Enabled = editable;
            _debandEnabled.Enabled = editable;
            _debandStrength.Enabled = editable && _debandEnabled.Checked;
            _debandValue.Text = _debandStrength.Value + " %";
            _sourceFormatCombo.Enabled = editable;
            _yuvMatrixCombo.Enabled = editable;
            _yuvRangeCombo.Enabled = editable;
            _sharpenValue.Text = _sharpenAmount.Value + " %";
        }

        private void DeleteSelectedProfile()
        {
            if (_profileSet == null || _profileCombo.SelectedIndex < 0) return;

            var profile = _profileSet.Profiles[_profileCombo.SelectedIndex];
            if (profile.IsDefault) return;

            var confirm = MessageBox.Show(this, Localization.F("Supprimer le profil \"{0}\" ?", profile.Name),
                "Broadcaster", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            DeviceColorProfileStore.DeleteProfile(ProfileDeviceLink, profile);
            _previousProfileIndex = -1;

            int removedIndex = _profileCombo.SelectedIndex;
            _profileSet.Profiles.RemoveAt(removedIndex);
            _profileCombo.Items.RemoveAt(removedIndex);

            int defaultIndex = _profileSet.Profiles.FindIndex(p => p.IsDefault);
            _profileCombo.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;
            ApplySelectedProfileToUI();
        }

        private void SetRowValue(ColorRow row, int value, bool editable)
        {
            if (!row.Supported) return;

            row.Track.Value = Clamp(value, row.Track.Minimum, row.Track.Maximum);
            row.ValueLabel.Text = row.Track.Value.ToString();
            row.Track.Enabled = editable; // "Par défaut" : lecture seule

            // Toujours poussé explicitement au matériel (ne PAS compter sur ValueChanged,
            // qui ne se déclenche pas si la valeur affichée était déjà la bonne).
            _captureSession.VideoProcAmp?.TrySet(row.Property, row.Track.Value);
        }

        private void CreateNewProfile()
        {
            if (_profileSet == null || _profileSet.Profiles.Count == 0) return;

            string name = PromptForText(Localization.T("Nouveau profil"), Localization.T("Nom du profil :"), Localization.T("Nouveau profil"));
            if (string.IsNullOrWhiteSpace(name)) return;

            if (_profileSet.Profiles.Any(p => p.Name == name))
            {
                MessageBox.Show(this, Localization.T("Un profil porte déjà ce nom."), "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var baseProfile = _profileSet.DefaultProfile ?? _profileSet.Profiles[0];
            var newProfile = baseProfile.Clone(name);
            SavePreviousProfileVideoSettings();
            _profileSet.Profiles.Add(newProfile);
            _profileCombo.Items.Add(newProfile.Name);
            _profileCombo.SelectedIndex = _profileCombo.Items.Count - 1; // déclenche ApplySelectedProfileToUI
        }

        private void RenameSelectedProfile()
        {
            if (_profileSet == null || _profileCombo.SelectedIndex < 0) return;

            var profile = _profileSet.Profiles[_profileCombo.SelectedIndex];
            if (profile.IsDefault) return;

            string newName = PromptForText(Localization.T("Renommer le profil"), Localization.T("Nouveau nom :"), profile.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == profile.Name) return;

            if (_profileSet.Profiles.Any(p => p.Name == newName))
            {
                MessageBox.Show(this, Localization.T("Un profil porte déjà ce nom."), "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string oldName = profile.Name;
            profile.Name = newName;
            _profileCombo.Items[_profileCombo.SelectedIndex] = newName;

            DeviceColorProfileStore.RenameProfile(ProfileDeviceLink, oldName, profile);
            if (_profileSet.ActiveProfileName == oldName)
                _profileSet.ActiveProfileName = newName;
        }

        private string PromptForText(string title, string label, string initialValue)
        {
            using (var dialog = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(320, 110)
            })
            {
                var label1 = new Label { Text = label, AutoSize = true, Location = new Point(10, 10) };
                var textBox = new TextBox { Text = initialValue, Location = new Point(10, 35), Width = 290 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(140, 70) };
                var cancel = new Button { Text = Localization.T("Annuler"), DialogResult = DialogResult.Cancel, Location = new Point(220, 70) };

                dialog.Controls.AddRange(new Control[] { label1, textBox, ok, cancel });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                return dialog.ShowDialog(this) == DialogResult.OK ? textBox.Text.Trim() : null;
            }
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        private static void SelectCurrent(ComboBox combo, Func<object, bool> match)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (match(combo.Items[i]))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

        private void PopulateDevices()
        {
            _videoCombo.Items.Clear();
            foreach (var device in MediaEngine.EnumerateVideoDevices())
                _videoCombo.Items.Add(device);

            _audioCaptureCombo.Items.Clear();
            _audioCaptureCombo.Items.Add(Localization.T("(Aucune - le device vidéo fournit déjà l'audio)"));
            foreach (var device in MediaEngine.EnumerateAudioCaptureDevices())
                _audioCaptureCombo.Items.Add(device);

            _audioOutputCombo.Items.Clear();
            _audioOutputCombo.Items.Add(Localization.T("(Périphérique par défaut du système)"));
            foreach (var device in MediaEngine.EnumerateAudioOutputDevices())
                _audioOutputCombo.Items.Add(device);

            SelectCurrent(_videoCombo, d => (d as VideoDeviceInfo)?.SymbolicLink == _initialConfig.VideoSymbolicLink);
            SelectCurrent(_audioCaptureCombo, d => (d as AudioCaptureDeviceInfo)?.FriendlyName == _initialConfig.AudioCaptureDeviceName);
            SelectCurrent(_audioOutputCombo, d => (d as AudioDeviceInfo)?.EndpointId == _initialConfig.AudioEndpointId);

            if (_videoCombo.Items.Count > 0 && _videoCombo.SelectedIndex < 0) _videoCombo.SelectedIndex = 0;
            if (_audioCaptureCombo.SelectedIndex < 0) _audioCaptureCombo.SelectedIndex = 0;
            if (_audioOutputCombo.SelectedIndex < 0) _audioOutputCombo.SelectedIndex = 0;

            _volumeTrack.Value = (int)Math.Round(_initialConfig.Volume * 100);
            _volumeLabel.Text = $"{_volumeTrack.Value}%";
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            if (!(_videoCombo.SelectedItem is VideoDeviceInfo selectedVideo))
            {
                MessageBox.Show(this, Localization.T("Aucune source vidéo sélectionnée."), "Broadcaster",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            var selectedAudioCapture = _audioCaptureCombo.SelectedItem as AudioCaptureDeviceInfo;
            var selectedAudioOutput = _audioOutputCombo.SelectedItem as AudioDeviceInfo;

            SavePreviousProfileVideoSettings();
            if (_profileSet != null && _profileCombo.SelectedIndex >= 0)
            {
                var profile = _profileSet.Profiles[_profileCombo.SelectedIndex];

                if (!profile.IsDefault)
                {
                    profile.Brightness = _brightnessRow.Track.Value;
                    profile.Contrast = _contrastRow.Track.Value;
                    profile.Hue = _hueRow.Track.Value;
                    profile.Saturation = _saturationRow.Track.Value;
                    DeviceColorProfileStore.SaveProfile(ProfileDeviceLink, profile);
                }

                DeviceColorProfileStore.SetActiveProfile(ProfileDeviceLink, profile.Name);
            }

            int selectedWidth, selectedHeight;
            if (_resolutionCombo.SelectedIndex < ResolutionPresets.Length)
            {
                selectedWidth = ResolutionPresets[_resolutionCombo.SelectedIndex].Width;
                selectedHeight = ResolutionPresets[_resolutionCombo.SelectedIndex].Height;
            }
            else
            {
                // L'entrée "personnalisé" ajoutée dynamiquement est toujours restée sélectionnée
                // telle quelle : on garde la valeur d'origine, inchangée.
                selectedWidth = _initialConfig.Width;
                selectedHeight = _initialConfig.Height;
            }

            ResultConfig = new AppConfig
            {
                VideoSymbolicLink = selectedVideo.SymbolicLink,
                AudioCaptureDeviceName = selectedAudioCapture?.FriendlyName ?? "",
                AudioEndpointId = selectedAudioOutput?.EndpointId ?? "",
                Volume = _volumeTrack.Value / 100f,
                Width = selectedWidth,
                Height = selectedHeight,
                CaptureTargetFps = CaptureFpsPresets[Math.Max(0, _captureFpsCombo.SelectedIndex)],
                PreviewVsync = _previewVsyncCheck.Checked,
                ShowDebugOverlay = _debugOverlayCheck.Checked,
                DisplayDeviceName = _orderedScreens[_screenCombo.SelectedIndex].DeviceName,
                NoSignalDetectionEnabled = _noSignalEnabledCheck.Checked,
                AutoReloadOnStart = _autoReloadCheck.Checked,
                ClipsEnabled = _enableClipsCheck.Checked,
                ClipDurationSeconds = (int)_clipDurationInput.Value,
                ClipTargetFps = ClipFpsPresets[Math.Max(0, _clipFpsCombo.SelectedIndex)],
                Language = Localization.LanguageCodes[Math.Max(0, _languageCombo.SelectedIndex)],
                Bindings = _bindings.Clone(),
            };
        }

        private void OptionsForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_isInstallingUpdate)
            {
                e.Cancel = true;
                return;
            }

            if (DialogResult != DialogResult.OK)
                RestoreOriginalColorValues();
        }

        private void RestoreOriginalColorValues()
        {
            var procAmp = _captureSession?.VideoProcAmp;
            if (procAmp == null) return;

            if (_brightnessRow.Supported) procAmp.TrySet(_brightnessRow.Property, _brightnessRow.OriginalValue);
            if (_contrastRow.Supported) procAmp.TrySet(_contrastRow.Property, _contrastRow.OriginalValue);
            if (_hueRow.Supported) procAmp.TrySet(_hueRow.Property, _hueRow.OriginalValue);
            if (_saturationRow.Supported) procAmp.TrySet(_saturationRow.Property, _saturationRow.OriginalValue);
        }
    }
}