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
            Width = 780; Height = 560; StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            MinimumSize = new Size(680, 480);

            Controls.Add(new Label { Text = "Mẫu tên file:", Left = 12, Top = 14, Width = 90 });
            txtTemplate = new TextBox
            {
                Left = 105,
                Top = 11,
                Width = 640,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = "$(SheetNumber) - $(SheetTitle)"
            };
            txtTemplate.TextChanged += (s, e) => RefreshPreview();
            Controls.Add(txtTemplate);

            Controls.Add(new Label { Text = "Chèn trường:", Left = 12, Top = 42, Width = 90 });
            pnlTokens = new FlowLayoutPanel
            {
                Left = 105,
                Top = 40,
                Width = 640,
                Height = 66,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                WrapContents = true
            };
            Controls.Add(pnlTokens);
            BuildTokenButtons();

            Controls.Add(new Label { Text = "Thư mục lưu:", Left = 12, Top = 116, Width = 90 });
            txtOutDir = new TextBox
            {
                Left = 105,
                Top = 113,
                Width = 560,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = defaultDir ?? ""
            };
            txtOutDir.TextChanged += (s, e) => RefreshPreview();
            Controls.Add(txtOutDir);
            btnBrowse = new Button
            {
                Text = "...",
                Left = 670,
                Top = 112,
                Width = 40,
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
                Left = 105,
                Top = 142,
                Width = 500
            };
            chkMerged.CheckedChanged += (s, e) => RefreshPreview();
            Controls.Add(chkMerged);

            dgv = new DataGridView
            {
                Left = 12,
                Top = 172,
                Width = 745,
                Height = 300,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                ReadOnly = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            var colSel = new DataGridViewCheckBoxColumn
            {
                Name = "Sel",
                HeaderText = "In",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                Width = 44
            };
            dgv.Columns.Add(colSel);
            dgv.Columns.Add("Sheet", "Sheet (Số - Tiêu đề)");
            dgv.Columns.Add("File", "Tên file PDF");
            dgv.Columns["Sheet"].ReadOnly = true;
            dgv.Columns["File"].ReadOnly = true;
            // commit tick ngay khi bam (khong can roi o) test
            dgv.CurrentCellDirtyStateChanged += (s, e) =>
            { if (dgv.IsCurrentCellDirty) dgv.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            dgv.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
                var row = dgv.Rows[e.RowIndex];
                var sheet = row.Tag as SheetInfo;
                if (sheet == null) return;
                bool isChecked = Convert.ToBoolean(row.Cells[0].Value ?? false);
                if (isChecked) _excluded.Remove(sheet); else _excluded.Add(sheet);
            };
            Controls.Add(dgv);

            btnAll = new Button
            {
                Text = "Chọn tất cả",
                Left = 12,
                Top = 482,
                Width = 100,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnAll.Click += (s, e) => { _excluded.Clear(); RefreshPreview(); };
            Controls.Add(btnAll);
            btnNone = new Button
            {
                Text = "Bỏ chọn tất cả",
                Left = 118,
                Top = 482,
                Width = 110,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnNone.Click += (s, e) => { _excluded.Clear(); foreach (var sh in _sheets) _excluded.Add(sh); RefreshPreview(); };
            Controls.Add(btnNone);

            btnOk = new Button
            {
                Text = "In PDF",
                Left = 575,
                Top = 482,
                Width = 90,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.OK
            };
            btnCancel = new Button
            {
                Text = "Hủy",
                Left = 670,
                Top = 482,
                Width = 87,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnOk); Controls.Add(btnCancel);
            AcceptButton = btnOk; CancelButton = btnCancel;

            RefreshPreview();
        }

        private void BuildTokenButtons()
        {
            var tokens = new List<string> {
                "SheetNumber", "SheetTitle", "SheetDesc", "SheetSetName", "LayoutName", "DwgName" };
            var customKeys = _sheets.SelectMany(s => s.Custom.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k);
            foreach (var k in customKeys) tokens.Add(k);

            foreach (var t in tokens)
            {
                var b = new Button { Text = t, AutoSize = true, Margin = new Padding(2) };
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

                int idx = dgv.Rows.Add(!_excluded.Contains(s), s.Number + " - " + s.Title, fileName);
                dgv.Rows[idx].Tag = s;
            }
        }
    }
}