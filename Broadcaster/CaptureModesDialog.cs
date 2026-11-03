using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Broadcaster
{
    /// <summary>
    /// Inspect the native video media types offered by the active Windows capture driver.
    /// Modes are read from Media Foundation, not inferred from marketing specifications.
    /// </summary>
    internal sealed class CaptureModesDialog : Form
    {
        private readonly List<CaptureVideoMode> _modes;
        private readonly CaptureVideoMode _activeMode;
        private readonly ComboBox _groupCombo;
        private readonly ListView _modeList;

        internal CaptureModesDialog(List<CaptureVideoMode> modes, CaptureVideoMode activeMode)
        {
            _modes = modes ?? new List<CaptureVideoMode>();
            _activeMode = activeMode;

            Text = Localization.T("Modes natifs annoncés par la carte HDMI");
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(710, 440);
            ClientSize = new Size(790, 530);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                ColumnCount = 1,
                RowCount = 4
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
            Controls.Add(layout);

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            var groupingLabel = new Label
            {
                Text = Localization.T("Trier / regrouper par :"),
                AutoSize = true,
                Margin = new Padding(1, 7, 9, 0)
            };
            _groupCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 190
            };
            _groupCombo.Items.Add(Localization.T("Résolution"));
            _groupCombo.Items.Add(Localization.T("FPS"));
            _groupCombo.Items.Add(Localization.T("Format"));
            top.Controls.Add(groupingLabel);
            top.Controls.Add(_groupCombo);
            layout.Controls.Add(top, 0, 0);

            var summary = new Label
            {
                Dock = DockStyle.Fill,
                Text = Localization.F("{0} modes annoncés par le pilote Windows (Media Foundation).", _modes.Count) +
                    Environment.NewLine + Localization.T("Le format sélectionné manuellement est strict : aucune substitution silencieuse par NV12."),
                ForeColor = Color.DimGray
            };
            layout.Controls.Add(summary, 0, 1);

            _modeList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                GridLines = true,
                MultiSelect = false,
                ShowGroups = true,
                UseCompatibleStateImageBehavior = false
            };
            _modeList.Columns.Add(Localization.T("Résolution"), 145);
            _modeList.Columns.Add(Localization.T("FPS"), 105);
            _modeList.Columns.Add(Localization.T("Format"), 105);
            _modeList.Columns.Add(Localization.T("Débit brut estimé"), 165);
            _modeList.Columns.Add(Localization.T("État"), 170);
            layout.Controls.Add(_modeList, 0, 2);

            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var info = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Color.DimGray,
                Text = Localization.T("Liste fournie par le pilote : elle n'est pas une fiche technique constructeur.\nLe débit indiqué est théorique pour les formats non compressés.")
            };
            var close = new Button
            {
                Text = Localization.T("Fermer"),
                AutoSize = true,
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            bottom.Controls.Add(info, 0, 0);
            bottom.Controls.Add(close, 1, 0);
            layout.Controls.Add(bottom, 0, 3);
            AcceptButton = close;
            CancelButton = close;

            _groupCombo.SelectedIndexChanged += (sender, args) => RefreshModes();
            _groupCombo.SelectedIndex = 0;
        }

        private void RefreshModes()
        {
            int category = _groupCombo.SelectedIndex;
            IEnumerable<CaptureVideoMode> sorted;
            switch (category)
            {
                case 1: // FPS, then resolution, then format
                    sorted = _modes.OrderByDescending(m => m.Fps)
                        .ThenByDescending(m => (long)m.Width * m.Height)
                        .ThenBy(m => m.Format, StringComparer.Ordinal);
                    break;
                case 2: // Format, then resolution, then FPS
                    sorted = _modes.OrderBy(m => m.Format, StringComparer.Ordinal)
                        .ThenByDescending(m => (long)m.Width * m.Height)
                        .ThenByDescending(m => m.Fps);
                    break;
                default: // Resolution, then FPS, then format
                    sorted = _modes.OrderByDescending(m => (long)m.Width * m.Height)
                        .ThenByDescending(m => m.Fps)
                        .ThenBy(m => m.Format, StringComparer.Ordinal);
                    break;
            }

            _modeList.BeginUpdate();
            try
            {
                _modeList.Items.Clear();
                _modeList.Groups.Clear();
                var groups = new Dictionary<string, ListViewGroup>(StringComparer.Ordinal);
                foreach (CaptureVideoMode mode in sorted)
                {
                    string groupName = category == 1
                        ? mode.Fps.ToString("0.##", CultureInfo.CurrentCulture) + " FPS"
                        : category == 2 ? mode.Format : mode.Width + " × " + mode.Height;
                    if (!groups.TryGetValue(groupName, out var group))
                    {
                        group = new ListViewGroup(groupName, HorizontalAlignment.Left);
                        groups.Add(groupName, group);
                        _modeList.Groups.Add(group);
                    }

                    bool isActive = _activeMode != null && mode.NativeIndex == _activeMode.NativeIndex &&
                        mode.Format == _activeMode.Format && mode.Width == _activeMode.Width &&
                        mode.Height == _activeMode.Height;
                    string status = isActive ? Localization.T("Actif") :
                        mode.Supported ? Localization.T("Format reconnu") : Localization.T("Format non reconnu");
                    string bitrate = mode.UncompressedMiBPerSecond > 0
                        ? mode.UncompressedMiBPerSecond.ToString("0", CultureInfo.CurrentCulture) + " MiB/s"
                        : Localization.T("Variable / non mesuré");
                    var item = new ListViewItem(mode.Width + " × " + mode.Height) { Group = group };
                    item.SubItems.Add(mode.Fps.ToString("0.##", CultureInfo.CurrentCulture));
                    item.SubItems.Add(mode.Format);
                    item.SubItems.Add(bitrate);
                    item.SubItems.Add(status);
                    if (isActive)
                    {
                        item.Font = new Font(_modeList.Font, FontStyle.Bold);
                        item.BackColor = Color.FromArgb(224, 242, 235);
                    }
                    else if (!mode.Supported)
                    {
                        item.ForeColor = Color.Gray;
                    }
                    _modeList.Items.Add(item);
                }
            }
            finally
            {
                _modeList.EndUpdate();
            }
        }
    }
}
