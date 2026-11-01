using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Broadcaster
{
    public class KeyBindings
    {
        // F11 reste fixe (plein écran), non reconfigurable.
        public static readonly Keys ReservedKey = Keys.F11;

        public Keys OpenMenu { get; set; } = Keys.Escape;
        public Keys Reload { get; set; } = Keys.R;
        public Keys ToggleCursor { get; set; } = Keys.T;
        public Keys Screenshot { get; set; } = Keys.F12;
        public Keys Clip { get; set; } = Keys.F10;

        public KeyBindings Clone() => new KeyBindings
        {
            OpenMenu = OpenMenu,
            Reload = Reload,
            ToggleCursor = ToggleCursor,
            Screenshot = Screenshot,
            Clip = Clip
        };

        public bool IsValid()
        {
            var all = new[] { OpenMenu, Reload, ToggleCursor, Screenshot, Clip };
            return all.All(k => k != Keys.None && k != ReservedKey)
                && all.Distinct().Count() == all.Length;
        }

        // Migration des anciens config.ini : si F12 est déjà occupée, on n'écrase
        // pas les raccourcis existants.
        public static Keys ChooseScreenshotKey(KeyBindings bindings)
        {
            var used = new[] { bindings.OpenMenu, bindings.Reload, bindings.ToggleCursor, bindings.Clip };
            var candidates = new[] { Keys.F12, Keys.F9, Keys.F8, Keys.F7, Keys.P, Keys.F6 };
            return candidates.First(k => k != ReservedKey && !used.Contains(k));
        }
        public static Keys ChooseClipKey(KeyBindings bindings)
        {
            var used = new[] { bindings.OpenMenu, bindings.Reload, bindings.ToggleCursor, bindings.Screenshot };
            var candidates = new[] { Keys.F10, Keys.F9, Keys.F8, Keys.F7, Keys.F6, Keys.C, Keys.P, Keys.F5 };
            return candidates.First(k => k != ReservedKey && !used.Contains(k));
        }
    }

    public class KeyBindingsForm : Form
    {
        private enum BindingAction { OpenMenu, Reload, ToggleCursor, Screenshot, Clip }

        private static readonly Keys[] ModifierOnlyKeys =
        {
            Keys.ShiftKey, Keys.ControlKey, Keys.Menu,
            Keys.LShiftKey, Keys.RShiftKey, Keys.LControlKey, Keys.RControlKey,
            Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin
        };

        private static readonly Dictionary<BindingAction, string> Titles = new Dictionary<BindingAction, string>
        {
            { BindingAction.OpenMenu, "Ouvrir le menu" },
            { BindingAction.Reload, "Relancer la capture" },
            { BindingAction.ToggleCursor, "Afficher / masquer le curseur" },
            { BindingAction.Screenshot, "Capture d'écran" },
            { BindingAction.Clip, "Enregistrer un clip" }
        };

        private readonly KeyBindings _working;
        private readonly Dictionary<BindingAction, Label> _keyLabels = new Dictionary<BindingAction, Label>();
        private readonly Dictionary<BindingAction, Button> _changeButtons = new Dictionary<BindingAction, Button>();
        private readonly Label _statusLabel = new Label { ForeColor = Color.DarkRed };
        private BindingAction? _capturing;

        public KeyBindings Result { get; private set; }

        public KeyBindingsForm(KeyBindings current)
        {
            _working = current.Clone();

            Text = Localization.T("Raccourcis clavier");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(480, 316);

            int y = 20;
            foreach (BindingAction action in Enum.GetValues(typeof(BindingAction)))
                AddRow(action, ref y);

            _statusLabel.Location = new Point(15, y);
            _statusLabel.Size = new Size(450, 20);

            var note = new Label
            {
                Text = "F11 (plein écran) reste fixe.",
                AutoSize = true,
                ForeColor = Color.Gray,
                Location = new Point(15, y + 25)
            };

            var resetButton = new Button { Text = "Réinitialiser", Size = new Size(100, 28), Location = new Point(15, 275) };
            var saveButton = new Button { Text = "Enregistrer", Size = new Size(90, 28), DialogResult = DialogResult.OK, Location = new Point(285, 275) };
            var cancelButton = new Button { Text = "Annuler", Size = new Size(90, 28), DialogResult = DialogResult.Cancel, Location = new Point(380, 275) };

            resetButton.Click += (s, e) => ResetToDefaults();
            saveButton.Click += (s, e) => Result = _working.Clone();

            Controls.AddRange(new Control[] { _statusLabel, note, resetButton, saveButton, cancelButton });
            Localization.TranslateControls(this);
            RefreshLabels();
        }

        private void AddRow(BindingAction action, ref int y)
        {
            var title = new Label { Text = Localization.T(Titles[action]) + " :", AutoSize = true, Location = new Point(15, y + 5) };
            var keyLabel = new Label
            {
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(150, 26),
                Location = new Point(235, y)
            };
            var change = new Button { Size = new Size(72, 26), Location = new Point(393, y) };
            change.Click += (s, e) => ToggleCapture(action);

            _keyLabels[action] = keyLabel;
            _changeButtons[action] = change;
            Controls.AddRange(new Control[] { title, keyLabel, change });
            y += 40;
        }

        // ProcessCmdKey est appelé AVANT le traitement des touches de dialogue (Échap, Entrée, Tab...)
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_capturing.HasValue)
                return base.ProcessCmdKey(ref msg, keyData);

            Keys key = keyData & Keys.KeyCode;
            if (key != Keys.None && !ModifierOnlyKeys.Contains(key))
                AssignKey(_capturing.Value, key);

            return true; // on avale TOUTES les touches pendant la capture
        }

        private void ToggleCapture(BindingAction action)
        {
            _capturing = _capturing == action ? (BindingAction?)null : action;
            _statusLabel.Text = "";
            RefreshLabels();
        }

        private void AssignKey(BindingAction action, Keys key)
        {
            if (key == KeyBindings.ReservedKey)
            {
                _statusLabel.Text = Localization.T("F11 est réservée au plein écran.");
                return;
            }

            foreach (BindingAction other in Enum.GetValues(typeof(BindingAction)))
            {
                if (other != action && GetKey(other) == key)
                {
                    _statusLabel.Text = Localization.F("{0} est déjà utilisée pour « {1} ».", FormatKey(key), Localization.T(Titles[other]));
                    return;
                }
            }

            SetKey(action, key);
            _capturing = null;
            _statusLabel.Text = "";
            RefreshLabels();
        }

        private void ResetToDefaults()
        {
            var defaults = new KeyBindings();
            _working.OpenMenu = defaults.OpenMenu;
            _working.Reload = defaults.Reload;
            _working.ToggleCursor = defaults.ToggleCursor;
            _working.Screenshot = defaults.Screenshot;
            _working.Clip = defaults.Clip;
            _capturing = null;
            _statusLabel.Text = "";
            RefreshLabels();
        }

        private Keys GetKey(BindingAction action)
        {
            switch (action)
            {
                case BindingAction.OpenMenu: return _working.OpenMenu;
                case BindingAction.Reload: return _working.Reload;
                case BindingAction.ToggleCursor: return _working.ToggleCursor;
                case BindingAction.Screenshot: return _working.Screenshot;
                case BindingAction.Clip: return _working.Clip;
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        private void SetKey(BindingAction action, Keys key)
        {
            switch (action)
            {
                case BindingAction.OpenMenu: _working.OpenMenu = key; break;
                case BindingAction.Reload: _working.Reload = key; break;
                case BindingAction.ToggleCursor: _working.ToggleCursor = key; break;
                case BindingAction.Screenshot: _working.Screenshot = key; break;
                case BindingAction.Clip: _working.Clip = key; break;
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        private void RefreshLabels()
        {
            foreach (BindingAction action in Enum.GetValues(typeof(BindingAction)))
            {
                bool isCapturing = _capturing == action;
                _keyLabels[action].Text = isCapturing ? Localization.T("Appuie sur une touche") : FormatKey(GetKey(action));
                _keyLabels[action].BackColor = isCapturing ? Color.LightYellow : SystemColors.Control;
                _changeButtons[action].Text = isCapturing ? Localization.T("Annuler") : Localization.T("Modifier");
            }
        }

        private static string FormatKey(Keys key)
        {
            switch (key)
            {
                case Keys.Escape: return Localization.T("Échap");
                case Keys.Return: return Localization.T("Entrée");
                case Keys.Space: return Localization.T("Espace");
                case Keys.Back: return Localization.T("Retour arrière");
                default:
                    string s = key.ToString();
                    if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s.Substring(1); // D1 -> 1
                    return s;
            }
        }
    }
}