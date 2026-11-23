using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Broadcaster
{
    // Separate dialog keeps Extras compact and fully independent from the capture engine.
    internal sealed class DiscordPresenceOptionsForm : Form
    {
        private readonly DiscordPresenceSettings _settings;
        private readonly CheckBox _enabled = new CheckBox { AutoSize = true, Text = "Activer la Rich Presence Discord" };
        private readonly TextBox _appId = new TextBox { Width = 480 };
        private readonly ComboBox _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 265 };
        private readonly ComboBox _platform = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 265 };
        private readonly TextBox _game = new TextBox { Width = 480 };
        private readonly TextBox _art = new TextBox { Width = 480 };
        private readonly TextBox _nintendoUrl = new TextBox { Width = 480 };
        private readonly CheckBox _showElapsed = new CheckBox { Text = "Afficher le temps écoulé", AutoSize = true };
        private readonly Button _testNintendo = new Button { Text = "Tester le statut Nintendo", AutoSize = true };
        private readonly Button _nxapiDocs = new Button { Text = "Configurer nxapi...", AutoSize = true };
        private readonly Button _discordDocs = new Button { Text = "Créer une application Discord...", AutoSize = true };
        private readonly Label _hint = new Label { AutoSize = true, MaximumSize = new Size(566, 0), ForeColor = Color.DimGray };
        private readonly Label _testResult = new Label { AutoSize = true, MaximumSize = new Size(566, 0), ForeColor = Color.DarkSlateGray };
        private readonly Panel _nintendoControls = new Panel { Height = 155, Width = 570 };
        private readonly Panel _manualControls = new Panel { Height = 118, Width = 570 };
        private readonly Button _ok = new Button { Text = "OK", Width = 105, DialogResult = DialogResult.OK };
        private readonly Button _cancel = new Button { Text = "Annuler", Width = 105, DialogResult = DialogResult.Cancel };
        private static readonly string[] Platforms = { "Switch2", "Switch", "PS5", "Xbox", "Other" };
        private static readonly string[] PlatformLabels = { "Nintendo Switch 2", "Nintendo Switch", "PlayStation 5", "Xbox", "Autre source HDMI" };
        public DiscordPresenceSettings Result { get; private set; }

        public DiscordPresenceOptionsForm(DiscordPresenceSettings original)
        {
            _settings = (original ?? new DiscordPresenceSettings()).Clone();
            _settings.Normalize();
            Text = Localization.T("Discord Rich Presence");
            ClientSize = new Size(642, 654);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;

            var scroll = new Panel { AutoScroll = true, Location = new Point(12, 12), Size = new Size(618, 588) };
            Controls.Add(scroll);
            int y = 9;
            _enabled.Location = new Point(9, y); scroll.Controls.Add(_enabled); y += 34;
            AddLabel(scroll, "ID de l'application Discord :", y); y += 23;
            _appId.Location = new Point(9, y); _appId.Text = _settings.ApplicationId;
            scroll.Controls.Add(_appId); y += 33;
            _discordDocs.Location = new Point(9, y); scroll.Controls.Add(_discordDocs); y += 42;
            AddLabel(scroll, "Mode de détection :", y); y += 22;
            _mode.Location = new Point(9, y);
            _mode.Items.Add(Localization.T("Manuel — toutes les consoles"));
            _mode.Items.Add(Localization.T("Nintendo — automatique via nxapi"));
            _mode.SelectedIndex = _settings.Mode == "Nintendo" ? 1 : 0;
            scroll.Controls.Add(_mode); y += 34;
            AddLabel(scroll, "Console / plateforme :", y); y += 22;
            _platform.Location = new Point(9, y);
            foreach (var label in PlatformLabels) _platform.Items.Add(Localization.T(label));
            int pi = Array.IndexOf(Platforms, _settings.Platform);
            _platform.SelectedIndex = pi < 0 ? 4 : pi;
            scroll.Controls.Add(_platform); y += 34;

            _manualControls.Location = new Point(9, y);
            AddLabel(_manualControls, "Nom du jeu affiché sur Discord :", 0, 0);
            _game.Location = new Point(0, 23); _game.Text = _settings.ManualGame;
            _manualControls.Controls.Add(_game);
            AddLabel(_manualControls, "Clé d'image Discord (facultatif) :", 57, 0);
            _art.Location = new Point(0, 80); _art.Text = _settings.ImageKey;
            _manualControls.Controls.Add(_art);
            scroll.Controls.Add(_manualControls); y += _manualControls.Height + 5;

            _nintendoControls.Location = new Point(9, y);
            AddLabel(_nintendoControls, "URL de présence nxapi :", 0, 0);
            _nintendoUrl.Location = new Point(0, 24); _nintendoUrl.Text = _settings.NintendoPresenceUrl;
            _nintendoControls.Controls.Add(_nintendoUrl);
            _nxapiDocs.Location = new Point(0, 58);
            _testNintendo.Location = new Point(202, 58);
            _nintendoControls.Controls.Add(_nxapiDocs);
            _nintendoControls.Controls.Add(_testNintendo);
            _testResult.Location = new Point(0, 100);
            _nintendoControls.Controls.Add(_testResult);
            scroll.Controls.Add(_nintendoControls); y += _nintendoControls.Height + 8;

            _showElapsed.Location = new Point(9, y); _showElapsed.Checked = _settings.ShowElapsed;
            scroll.Controls.Add(_showElapsed); y += 34;
            _hint.Location = new Point(9, y);
            _hint.Text = Localization.T("Discord doit être ouvert sur ce PC. Nintendo nécessite nxapi, configuré séparément avec un compte autorisé. Aucun mot de passe Nintendo ou Discord n'est demandé par Broadcaster. L'URL de présence est enregistrée localement : ne la partage pas.");
            scroll.Controls.Add(_hint);

            _cancel.Location = new Point(406, 613); _ok.Location = new Point(522, 613);
            Controls.Add(_cancel); Controls.Add(_ok);
            AcceptButton = _ok; CancelButton = _cancel;
            Localization.TranslateControls(this);
            _mode.SelectedIndexChanged += (s, e) => UpdateMode();
            _enabled.CheckedChanged += (s, e) => UpdateMode();
            _enabled.Checked = _settings.Enabled;
            _discordDocs.Click += (s, e) => OpenPage("https://discord.com/developers/applications");
            _nxapiDocs.Click += (s, e) => OpenPage("https://github.com/samuelthomas2774/nxapi/blob/main/docs/cli.md");
            _testNintendo.Click += async (s, e) =>
            {
                if (!DiscordPresenceSettings.ValidPresenceUrl(_nintendoUrl.Text))
                {
                    _testResult.Text = Localization.T("URL nxapi invalide : utilise HTTPS ou une adresse locale HTTP.");
                    return;
                }
                _testNintendo.Enabled = false;
                _testResult.Text = Localization.T("Vérification en cours...");
                try
                {
                    var value = await NintendoPresenceReader.ReadAsync(_nintendoUrl.Text.Trim(), System.Threading.CancellationToken.None);
                    if (!IsDisposed) _testResult.Text = value == null ? Localization.T("Aucun jeu Nintendo actif ou visible.") : Localization.F("Jeu détecté : {0}", value.Title);
                }
                catch (Exception ex) { if (!IsDisposed) _testResult.Text = ex.Message; }
                finally { if (!IsDisposed) _testNintendo.Enabled = true; }
            };
            _ok.Click += ValidateAndClose;
            UpdateMode();
        }

        private static void OpenPage(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { }
        }

        private static void AddLabel(Control parent, string text, int y, int x = 9)
        {
            parent.Controls.Add(new Label { Location = new Point(x, y), AutoSize = true, Text = Localization.T(text) });
        }

        private void UpdateMode()
        {
            bool nintendo = _mode.SelectedIndex == 1;
            _manualControls.Visible = !nintendo;
            _nintendoControls.Visible = nintendo;
            // Show the selected editor at the same location, without an empty gap.
            const int editorTop = 253;
            _manualControls.Top = editorTop;
            _nintendoControls.Top = editorTop;
            _showElapsed.Top = editorTop + (nintendo ? _nintendoControls.Height : _manualControls.Height) + 13;
            _hint.Top = _showElapsed.Bottom + 12;
            // Configuration remains editable while sharing is disabled; only publication
            // to Discord depends on the master checkbox.
        }

        private void ValidateAndClose(object sender, EventArgs e)
        {
            var config = new DiscordPresenceSettings
            {
                Enabled = _enabled.Checked,
                ApplicationId = _appId.Text.Trim(),
                Mode = _mode.SelectedIndex == 1 ? "Nintendo" : "Manual",
                Platform = Platforms[Math.Max(0, _platform.SelectedIndex)],
                ManualGame = _game.Text,
                ImageKey = _art.Text,
                NintendoPresenceUrl = _nintendoUrl.Text.Trim(),
                ShowElapsed = _showElapsed.Checked
            };
            config.Normalize();
            if (config.Enabled && !DiscordPresenceSettings.ValidApplicationId(config.ApplicationId))
            {
                MessageBox.Show(this, Localization.T("Renseigne un ID d'application Discord valide."), "Discord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None; return;
            }
            if (config.Enabled && config.Mode == "Nintendo" && !DiscordPresenceSettings.ValidPresenceUrl(config.NintendoPresenceUrl))
            {
                MessageBox.Show(this, Localization.T("URL nxapi invalide : utilise HTTPS ou une adresse locale HTTP."), "Discord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None; return;
            }
            if (config.Enabled && config.Mode == "Manual" && string.IsNullOrWhiteSpace(config.ManualGame))
            {
                MessageBox.Show(this, Localization.T("Indique le nom du jeu à afficher."), "Discord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None; return;
            }
            Result = config;
        }
    }

}
