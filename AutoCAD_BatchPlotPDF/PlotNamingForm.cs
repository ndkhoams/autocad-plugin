using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BatchPlotPdf
{
    public class PlotNamingForm : Form
    {
        private readonly List<SheetInfo> _sheets;
        private TextBox txtTemplate, txtOutDir;
        private FlowLayoutPanel pnlTokens;
        private CheckBox chkMerged;
        private DataGridView dgv;
        private Button btnBrowse, btnOk, btnCancel, btnAll, btnNone;
        private readonly HashSet<SheetInfo> _excluded = new HashSet<SheetInfo>();
        private int _lastCheckRow = -1;   // ho tro Shift-chon ca dai hang
        private bool _shiftDown = false;
        private bool _bulk = false;       // chan de quy khi set tick hang loat

        public string Template { get { return txtTemplate.Text; } }
        public string OutputDir { get { return txtOutDir.Text; } }
        public bool Merged { get { return chkMerged.Checked; } }
        public List<SheetInfo> SelectedSheets
        {
            get
            {
                var list = new List<SheetInfo>();
                foreach (var s in _sheets) if (!_excluded.Contains(s)) list.Add(s);
                return list;
            }
        }

        public PlotNamingForm(List<SheetInfo> sheets, string defaultDir)
        {
            _sheets = sheets ?? new List<SheetInfo>();
            Text = "Đặt tên PDF theo Sheet Set";
            Width = 940; Height = 680; StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.75f);
            MinimumSize = new Size(840, 580);
            Padding = new Padding(6);

            const int labelW = 110;
            const int fieldL = 130;

            var lblTpl = new Label
            {
                Text = "Mẫu tên file:",
                Left = 20,
                Top = 26,
                Width = labelW,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(lblTpl);
            txtTemplate = new TextBox
            {
                Left = fieldL,
                Top = 24,
                Width = 760,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = "$(SheetNumber) - $(SheetTitle)"
            };
            txtTemplate.TextChanged += (s, e) => RefreshPreview();
            Controls.Add(txtTemplate);

            var lblTok = new Label
            {
                Text = "Chèn trường:",
                Left = 20,
                Top = 66,
                Width = labelW,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(lblTok);
            pnlTokens = new FlowLayoutPanel
            {
                Left = fieldL,
                Top = 64,
                Width = 760,
                Height = 88,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                WrapContents = true,
                Padding = new Padding(2)
            };
            Controls.Add(pnlTokens);
            BuildTokenButtons();

            var lblDir = new Label
            {
                Text = "Thư mục lưu:",
                Left = 20,
                Top = 172,
                Width = labelW,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(lblDir);
            txtOutDir = new TextBox
            {
                Left = fieldL,
                Top = 170,
                Width = 712,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = defaultDir ?? ""
            };
            txtOutDir.TextChanged += (s, e) => RefreshPreview();
            Controls.Add(txtOutDir);
            btnBrowse = new Button
            {
                Text = "...",
                Left = 850,
                Top = 169,
                Width = 44,
                Height = 28,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnBrowse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                    if (d.ShowDialog() == DialogResult.OK) txtOutDir.Text = d.SelectedPath;
            };
            Controls.Add(btnBrowse);

            chkMerged = new CheckBox
            {
                Text = "Gộp tất cả vào 1 file PDF (dùng mẫu tên cho tên file gộp)",
                Left = fieldL,
                Top = 208,
                Width = 600,
                Height = 24
            };
            chkMerged.CheckedChanged += (s, e) => RefreshPreview();
            Controls.Add(chkMerged);

            var lblHint = new Label
            {
                Text = "Mẹo: giữ Shift rồi tích để chọn/bỏ chọn cả một dải hàng.",
                Left = 20,
                Top = 240,
                Width = 720,
                Height = 22,
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            Controls.Add(lblHint);

            dgv = new DataGridView
            {
                Left = 20,
                Top = 270,
                Width = 874,
                Height = 316,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                ReadOnly = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                BorderStyle = BorderStyle.FixedSingle
            };
            dgv.RowTemplate.Height = 30;
            dgv.ColumnHeadersHeight = 34;
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            dgv.DefaultCellStyle.Padding = new Padding(4, 2, 4, 2);

            var colSel = new DataGridViewCheckBoxColumn
            {
                Name = "Sel",
                HeaderText = "In",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                Width = 48,
                FillWeight = 8
            };
            dgv.Columns.Add(colSel);
            dgv.Columns.Add("Sheet", "Sheet (Số - Tiêu đề)");
            dgv.Columns.Add("Rev", "Rev");
            dgv.Columns.Add("File", "Tên file PDF");
            dgv.Columns["Sheet"].ReadOnly = true;
            dgv.Columns["Rev"].ReadOnly = true;
            dgv.Columns["File"].ReadOnly = true;
            dgv.Columns["Sheet"].FillWeight = 42;
            dgv.Columns["Rev"].FillWeight = 12;
            dgv.Columns["File"].FillWeight = 46;
            dgv.Columns["Rev"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            // commit tick ngay khi bam (khong can roi o)
            dgv.CurrentCellDirtyStateChanged += (s, e) =>
            { if (dgv.IsCurrentCellDirty) dgv.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            // bat trang thai Shift TRUOC khi gia tri commit
            dgv.CellMouseDown += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 0)
                    _shiftDown = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            };
            dgv.CellValueChanged += (s, e) =>
            {
                if (_bulk || e.RowIndex < 0 || e.ColumnIndex != 0) return;
                var row = dgv.Rows[e.RowIndex];
                var sheet = row.Tag as SheetInfo;
                if (sheet == null) return;
                bool isChecked = Convert.ToBoolean(row.Cells[0].Value ?? false);
                ApplyCheck(sheet, isChecked);

                // Shift + tich -> ap dung cung trang thai cho ca dai tu hang tich truoc do
                if (_shiftDown && _lastCheckRow >= 0 && _lastCheckRow != e.RowIndex)
                {
                    int a = Math.Min(_lastCheckRow, e.RowIndex);
                    int b = Math.Max(_lastCheckRow, e.RowIndex);
                    _bulk = true;
                    for (int i = a; i <= b; i++)
                    {
                        dgv.Rows[i].Cells[0].Value = isChecked;
                        var sh = dgv.Rows[i].Tag as SheetInfo;
                        if (sh != null) ApplyCheck(sh, isChecked);
                    }
                    _bulk = false;
                }
                _lastCheckRow = e.RowIndex;
                _shiftDown = false;
            };
            Controls.Add(dgv);

            const int btnTop = 604;
            btnAll = new Button
            {
                Text = "Chọn tất cả",
                Left = 20,
                Top = btnTop,
                Width = 120,
                Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnAll.Click += (s, e) => { _excluded.Clear(); RefreshPreview(); };
            Controls.Add(btnAll);
            btnNone = new Button
            {
                Text = "Bỏ chọn tất cả",
                Left = 148,
                Top = btnTop,
                Width = 130,
                Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnNone.Click += (s, e) => { _excluded.Clear(); foreach (var sh in _sheets) _excluded.Add(sh); RefreshPreview(); };
            Controls.Add(btnNone);

            btnOk = new Button
            {
                Text = "In PDF",
                Left = 686,
                Top = btnTop,
                Width = 100,
                Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.OK
            };
            btnCancel = new Button
            {
                Text = "Hủy",
                Left = 794,
                Top = btnTop,
                Width = 100,
                Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnOk); Controls.Add(btnCancel);
            AcceptButton = btnOk; CancelButton = btnCancel;

            RefreshPreview();
        }

        private void ApplyCheck(SheetInfo sheet, bool isChecked)
        {
            if (isChecked) _excluded.Remove(sheet); else _excluded.Add(sheet);
        }

        private void BuildTokenButtons()
        {
            var tokens = new List<string> {
                "SheetNumber", "SheetTitle", "SheetDesc", "SheetSetName", "LayoutName", "DwgName",
                "Revision", "RevisionDate", "IssuePurpose" };
            var customKeys = _sheets.SelectMany(s => s.Custom.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k);
            foreach (var k in customKeys) tokens.Add(k);

            foreach (var t in tokens)
            {
                var b = new Button
                {
                    Text = t,
                    AutoSize = true,
                    Margin = new Padding(3),
                    Padding = new Padding(4, 2, 4, 2)
                };
                string token = "$(" + t + ")";
                b.Click += (s, e) =>
                {
                    int i = txtTemplate.SelectionStart;
                    txtTemplate.Text = txtTemplate.Text.Insert(i, token);
                    txtTemplate.SelectionStart = i + token.Length;
                    txtTemplate.Focus();
                };
                pnlTokens.Controls.Add(b);
            }
        }

        private SheetInfo FirstSelected()
        {
            foreach (var s in _sheets) if (!_excluded.Contains(s)) return s;
            return _sheets.Count > 0 ? _sheets[0] : null;
        }

        private void RefreshPreview()
        {
            dgv.Rows.Clear();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string mergedName = null;
            if (chkMerged.Checked)
            {
                mergedName = SsmNaming.EnsurePdf(SsmNaming.SanitizeFile(
                    SsmNaming.Resolve(Template, FirstSelected(), true)));
                if (string.IsNullOrWhiteSpace(mergedName)) mergedName = "MergedSheets.pdf";
            }

            foreach (var s in _sheets)
            {
                string fileName;
                if (chkMerged.Checked)
                {
                    fileName = mergedName;
                }
                else
                {
                    string name = SsmNaming.SanitizeFile(SsmNaming.Resolve(Template, s, false));
                    if (string.IsNullOrWhiteSpace(name)) name = s.LayoutName;
                    string baseName = name; int n = 2;
                    while (!used.Add(name)) name = baseName + " (" + (n++) + ")";
                    fileName = SsmNaming.EnsurePdf(name);
                }

                int idx = dgv.Rows.Add(!_excluded.Contains(s), s.Number + " - " + s.Title, s.Revision, fileName);
                dgv.Rows[idx].Tag = s;
            }
        }
    }
}