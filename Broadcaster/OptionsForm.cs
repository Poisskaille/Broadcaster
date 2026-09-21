using System;
using System.Drawing;
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

        private readonly ComboBox _screenCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private Screen[] _orderedScreens;

        private static readonly (string Label, int Width, int Height)[] ResolutionPresets = new[]
        {
        ("1280 x 720 (HD)", 1280, 720),
        ("1920 x 1080 (Full HD)", 1920, 1080),
        ("2560 x 1440 (2K / QHD)", 2560, 1440),
        ("3840 x 2160 (4K UHD)", 3840, 2160),
};

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



        public OptionsForm(AppConfig currentConfig, CaptureSession captureSession)
        {
            _initialConfig = currentConfig;
            _captureSession = captureSession;

            Text = "Options - Broadcaster";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(700, 500);

            BuildLayout();
            PopulateResolutionCombo();
            PopulateScreenCombo();
            PopulateDevices();
            PopulateColorTab();
            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            _okButton.Click += OkButton_Click;
            FormClosing += OptionsForm_FormClosing;
            _profileCombo.SelectedIndexChanged += (s, e) => ApplySelectedProfileToUI();
            _newProfileButton.Click += (s, e) => CreateNewProfile();
            _renameProfileButton.Click += (s, e) => RenameSelectedProfile();
            _deleteProfileButton.Click += (s, e) => DeleteSelectedProfile();
        }

        private void BuildLayout()
        {
            var tabs = new TabControl { Location = new Point(10, 10), Size = new Size(680, 460) };
            var generalTab = new TabPage("Général");
            var videoTab = new TabPage("Vidéo");
            tabs.TabPages.Add(generalTab);
            tabs.TabPages.Add(videoTab);
            var extrasTab = new TabPage("Extras");
            tabs.TabPages.Add(extrasTab);

            BuildExtrasTab(extrasTab);


            BuildGeneralTab(generalTab);
            BuildVideoTab(videoTab);

            _okButton.Location = new Point(510, 470);
            _cancelButton.Location = new Point(600, 470);

            Controls.Add(tabs);
            Controls.Add(_okButton);
            Controls.Add(_cancelButton);
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
                string suffix = s.Primary ? " (Principal)" : "";
                _screenCombo.Items.Add($"Écran {i + 1}{suffix} - {s.Bounds.Width}x{s.Bounds.Height}");
            }

            int match = Array.FindIndex(_orderedScreens, s => s.DeviceName == _initialConfig.DisplayDeviceName);
            _screenCombo.SelectedIndex = match >= 0 ? match : 0;
        }

        private void BuildExtrasTab(TabPage page)
        {
            var label = new Label
            {
                Text = "Image affichée quand aucun signal n'est reçu depuis 10 secondes :",
                AutoSize = true,
                Location = new Point(15, 15)
            };

            _backgroundPreview.Location = new Point(15, 45);
            _backgroundPreview.Size = new Size(320, 180);

            _chooseBackgroundButton.Location = new Point(350, 45);
            _clearBackgroundButton.Location = new Point(350, 80);

            _chooseBackgroundButton.Click += (s, e) => ChooseBackgroundImage();
            _clearBackgroundButton.Click += (s, e) => ClearBackgroundImage();

            _noSignalEnabledCheck.Location = new Point(15, 240);
            _noSignalEnabledCheck.Checked = _initialConfig.NoSignalDetectionEnabled;

            page.Controls.Add(label);
            page.Controls.Add(_backgroundPreview);
            page.Controls.Add(_chooseBackgroundButton);
            page.Controls.Add(_clearBackgroundButton);
            page.Controls.Add(_noSignalEnabledCheck);

            RefreshBackgroundPreview();
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
                Title = "Choisir une image de fond"
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
                        MessageBox.Show(this, "Impossible de charger cette image : " + ex.Message,
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

            var volumeTitle = new Label { Text = "Volume :", AutoSize = true, Location = new Point(15, 195) };
            _volumeTrack.Location = new Point(15, 218);
            _volumeTrack.Width = 320;
            _volumeTrack.ValueChanged += (s, e) => _volumeLabel.Text = $"{_volumeTrack.Value}%";
            _volumeLabel.Location = new Point(345, 225);

            var videoLabel = new Label { Text = "Capture Source :", AutoSize = true, Location = new Point(15, 260) };
            _videoCombo.Location = new Point(15, 283);
            _videoCombo.Width = 405;

            var audioCaptureLabel = new Label { Text = "Audio Capture (if need to separate audio and video)", AutoSize = true, Location = new Point(15, 320) };
            _audioCaptureCombo.Location = new Point(15, 343);
            _audioCaptureCombo.Width = 405;

            var audioOutputLabel = new Label { Text = "Audio Output :", AutoSize = true, Location = new Point(15, 385) };
            _audioOutputCombo.Location = new Point(15, 408);
            _audioOutputCombo.Width = 405;

            page.Controls.AddRange(new Control[]
            {
                                screenLabel, _screenCombo,
                videoLabel, _videoCombo,
                audioCaptureLabel, _audioCaptureCombo,
                audioOutputLabel, _audioOutputCombo,
                volumeTitle, _volumeTrack, _volumeLabel,
                resolutionLabel, _resolutionCombo,


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

            page.Controls.Add(profileLabel);
            page.Controls.Add(_profileCombo);
            page.Controls.Add(_newProfileButton);
            page.Controls.Add(_renameProfileButton);
            page.Controls.Add(_colorUnavailableLabel);
            page.Controls.Add(_deleteProfileButton);
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
                _resolutionCombo.Items.Add($"{_initialConfig.Width} x {_initialConfig.Height} (personnalisé)");
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
            var procAmp = _captureSession?.VideoProcAmp;
            _colorTabAvailable = procAmp != null;

            _profileCombo.Enabled = _colorTabAvailable;
            _newProfileButton.Enabled = _colorTabAvailable;
            _renameProfileButton.Enabled = false;
            _deleteProfileButton.Enabled = false;
            _colorUnavailableLabel.Visible = !_colorTabAvailable;

            if (!_colorTabAvailable) return;

            SetupRowsRanges();

            // Devrait toujours exister à ce stade (créé au tout premier démarrage de la
            // capture pour ce device), mais on se protège si jamais.
            _profileSet = DeviceColorProfileStore.Load(_captureSession.ActiveVideoSymbolicLink)
              ?? _captureSession.EnsureFactoryColorProfile()
              ?? new DeviceColorProfileSet();

            _profileCombo.Items.Clear();
            foreach (var p in _profileSet.Profiles)
                _profileCombo.Items.Add(p.Name);

            if (_profileCombo.Items.Count == 0) return;

            int activeIndex = _profileSet.Profiles.FindIndex(p => p.Name == _profileSet.ActiveProfileName);
            _profileCombo.SelectedIndex = activeIndex >= 0 ? activeIndex : 0;
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
        }

        private void DeleteSelectedProfile()
        {
            if (_profileSet == null || _profileCombo.SelectedIndex < 0) return;

            var profile = _profileSet.Profiles[_profileCombo.SelectedIndex];
            if (profile.IsDefault) return;

            var confirm = MessageBox.Show(this, $"Supprimer le profil \"{profile.Name}\" ?",
                "Broadcaster", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            DeviceColorProfileStore.DeleteProfile(_captureSession.ActiveVideoSymbolicLink, profile);

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

            string name = PromptForText("Nouveau profil", "Nom du profil :", "Nouveau profil");
            if (string.IsNullOrWhiteSpace(name)) return;

            if (_profileSet.Profiles.Any(p => p.Name == name))
            {
                MessageBox.Show(this, "Un profil porte déjà ce nom.", "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var baseProfile = _profileSet.DefaultProfile ?? _profileSet.Profiles[0];
            var newProfile = baseProfile.Clone(name);

            _profileSet.Profiles.Add(newProfile);
            _profileCombo.Items.Add(newProfile.Name);
            _profileCombo.SelectedIndex = _profileCombo.Items.Count - 1; // déclenche ApplySelectedProfileToUI
        }

        private void RenameSelectedProfile()
        {
            if (_profileSet == null || _profileCombo.SelectedIndex < 0) return;

            var profile = _profileSet.Profiles[_profileCombo.SelectedIndex];
            if (profile.IsDefault) return;

            string newName = PromptForText("Renommer le profil", "Nouveau nom :", profile.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == profile.Name) return;

            if (_profileSet.Profiles.Any(p => p.Name == newName))
            {
                MessageBox.Show(this, "Un profil porte déjà ce nom.", "Broadcaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string oldName = profile.Name;
            profile.Name = newName;
            _profileCombo.Items[_profileCombo.SelectedIndex] = newName;

            DeviceColorProfileStore.RenameProfile(_captureSession.ActiveVideoSymbolicLink, oldName, profile);
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
                var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(220, 70) };

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
            _audioCaptureCombo.Items.Add("(Aucune - le device vidéo fournit déjà l'audio)");
            foreach (var device in MediaEngine.EnumerateAudioCaptureDevices())
                _audioCaptureCombo.Items.Add(device);

            _audioOutputCombo.Items.Clear();
            _audioOutputCombo.Items.Add("(Périphérique par défaut du système)");
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
                MessageBox.Show(this, "Aucune source vidéo sélectionnée.", "Broadcaster",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            var selectedAudioCapture = _audioCaptureCombo.SelectedItem as AudioCaptureDeviceInfo;
            var selectedAudioOutput = _audioOutputCombo.SelectedItem as AudioDeviceInfo;

            if (_colorTabAvailable && _profileSet != null && _profileCombo.SelectedIndex >= 0)
            {
                var profile = _profileSet.Profiles[_profileCombo.SelectedIndex];

                if (!profile.IsDefault)
                {
                    profile.Brightness = _brightnessRow.Track.Value;
                    profile.Contrast = _contrastRow.Track.Value;
                    profile.Hue = _hueRow.Track.Value;
                    profile.Saturation = _saturationRow.Track.Value;
                    DeviceColorProfileStore.SaveProfile(_captureSession.ActiveVideoSymbolicLink, profile);
                }

                DeviceColorProfileStore.SetActiveProfile(_captureSession.ActiveVideoSymbolicLink, profile.Name);
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
                DisplayDeviceName = _orderedScreens[_screenCombo.SelectedIndex].DeviceName,
                NoSignalDetectionEnabled = _noSignalEnabledCheck.Checked,
            };
        }

        private void OptionsForm_FormClosing(object sender, FormClosingEventArgs e)
        {
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