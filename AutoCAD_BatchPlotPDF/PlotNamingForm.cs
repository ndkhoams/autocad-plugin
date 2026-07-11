using CADtools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace CADtools
{
    // Form GOP duy nhat: vua dat ten & in PDF theo Sheet Set, vua sua & luu nguoc Sheet Set (.dst),
    // vua xuat Excel — tat ca trong 1 cua so (khong con tach thanh 2 form rieng).
    public class PlotNamingForm : Form
    {
        public enum SsmAction { None, Print, Save }

        private readonly System.Collections.Generic.List<SheetInfo> _sheets;
        private readonly System.Collections.Generic.List<string> _customKeys;
        private static readonly string[] _whitelist = { "SHT", "CONT" };

        // DST picker (NEW)
        private TextBox txtDstPath;
        private Button btnDstBrowse;
        public bool DstChanged { get; private set; }
        public string DstPath { get { return txtDstPath == null ? "" : txtDstPath.Text.Trim(); } }

        private TextBox txtTemplate, txtOutDir, txtProjNum;
        private FlowLayoutPanel pnlTokens;
        private CheckBox chkMerged;
        private DataGridView dgv;
        private Button btnBrowse, btnPrint, btnSave, btnExport, btnCancel, btnAll, btnNone;
        private readonly HashSet<SheetInfo> _excluded = new HashSet<SheetInfo>();
        // Subset collapse state (true = collapsed)
        private readonly Dictionary<string, bool> _subsetCollapsed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private int _lastCheckRow = -1;
        private bool _shiftDown = false;
        private bool _bulk = false;

        public SsmAction Action { get; private set; }
        public string Template { get { return txtTemplate.Text; } }
        public string OutputDir { get { return txtOutDir.Text; } }
        public string ProjectNumber { get { return txtProjNum.Text.Trim(); } }
        public bool Merged { get { return chkMerged.Checked; } }
        public System.Collections.Generic.List<SheetInfo> SelectedSheets
        {
            get
            {
                var list = new System.Collections.Generic.List<SheetInfo>();
                foreach (var s in _sheets) if (!_excluded.Contains(s)) list.Add(s);
                return list;
            }
        }

        public PlotNamingForm(System.Collections.Generic.List<SheetInfo> sheets, string defaultDir, string currentDstPath)
        {
            _sheets = sheets ?? new System.Collections.Generic.List<SheetInfo>();
            _customKeys = _sheets.SelectMany(s => s.Custom.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => _whitelist.Any(w => string.Equals(w, k, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(k => Array.FindIndex(_whitelist, w => string.Equals(w, k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

            Text = "Sheet Set Properties";
            ClientSize = new Size(1200, 800); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.75f);
            MinimumSize = new Size(1000, 640);
            Padding = new Padding(6);

            // Dịch cụm trên đầu sang phải để không che text bên trái
            const int labelW = 170;
            const int fieldL = 190;
            const int rightEdge = 1180;

            // DST picker row
            var lblDst = new Label { Text = "Sheet set (.dst):", Left = 20, Top = 6, Width = labelW, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(lblDst);

            txtDstPath = new TextBox
            {
                Left = fieldL,
                Top = 4,
                Width = rightEdge - fieldL - 50,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ReadOnly = true,
                Text = currentDstPath ?? ""
            };
            Controls.Add(txtDstPath);

            btnDstBrowse = new Button { Text = "...", Left = rightEdge - 44, Top = 3, Width = 44, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnDstBrowse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Filter = "Sheet Set (*.dst)|*.dst";
                    dlg.Title = "Chọn file Sheet Set (.dst)";
                    dlg.Multiselect = false;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    txtDstPath.Text = dlg.FileName;
                    DstChanged = true;
                    Action = SsmAction.None;
                    DialogResult = DialogResult.OK;
                }
            };
            Controls.Add(btnDstBrowse);

            // shift the rest of controls down by 30px
            const int dy = 30;

            var lblTpl = new Label { Text = "Mẫu tên file PDF:", Left = 20, Top = 26 + dy, Width = labelW, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(lblTpl);
            txtTemplate = new TextBox
            {
                Left = fieldL,
                Top = 24 + dy,
                Width = rightEdge - fieldL,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = "$(Project Number)-$(SheetNumber)-Sht$(SHT)-($(Revision))"
            };
            txtTemplate.TextChanged += (s, e) => UpdateAllPreviews();
            Controls.Add(txtTemplate);

            var lblTok = new Label { Text = "Add Field:", Left = 20, Top = 66 + dy, Width = labelW, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(lblTok);
            pnlTokens = new FlowLayoutPanel
            {
                Left = fieldL,
                Top = 64 + dy,
                Width = rightEdge - fieldL,
                Height = 88,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                WrapContents = true,
                Padding = new Padding(2)
            };
            Controls.Add(pnlTokens);
            BuildTokenButtons();

            var lblProj = new Label { Text = "Project number:", Left = 20, Top = 162 + dy, Width = labelW, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(lblProj);
            txtProjNum = new TextBox
            {
                Left = fieldL,
                Top = 160 + dy,
                Width = rightEdge - fieldL,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = LoadSavedProjectNumber()
            };
            txtProjNum.TextChanged += (s, e) => UpdateAllPreviews();
            Controls.Add(txtProjNum);

            var lblDir = new Label { Text = "Thư mục lưu PDF:", Left = 20, Top = 210 + dy, Width = labelW, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(lblDir);
            txtOutDir = new TextBox
            {
                Left = fieldL,
                Top = 208 + dy,
                Width = rightEdge - fieldL - 50,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = defaultDir ?? ""
            };
            txtOutDir.TextChanged += (s, e) => UpdateAllPreviews();
            Controls.Add(txtOutDir);
            btnBrowse = new Button { Text = "...", Left = rightEdge - 44, Top = 207 + dy, Width = 44, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnBrowse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) if (d.ShowDialog() == DialogResult.OK) txtOutDir.Text = d.SelectedPath; };
            Controls.Add(btnBrowse);

            chkMerged = new CheckBox { Text = "Gộp tất cả vào 1 file PDF", Left = fieldL, Top = 246 + dy, Width = 600, Height = 24 };
            chkMerged.CheckedChanged += (s, e) => UpdateAllPreviews();
            Controls.Add(chkMerged);

            var lblHint = new Label
            {
                Text = "Sửa trực tiếp trong bảng (Số sheet, Tiêu đề, Revision, Ngày rev, Issue purpose, CONT, SHT). "
            + "Giữ Shift rồi tích để chọn/bỏ cả dải. Nút \"In PDF\" chỉ in sheet đang tích; nút \"Lưu Sheet Set\" ghi thay đổi ngược vào .dst.",
                Left = 20,
                Top = 276 + dy,
                Width = rightEdge - 20,
                Height = 24,
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(lblHint);

            dgv = new DataGridView
            {
                Left = 20,
                Top = 306 + dy,
                Width = rightEdge - 20,
                Height = 404,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                ReadOnly = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                BorderStyle = BorderStyle.FixedSingle
            };
            dgv.RowTemplate.Height = 28;
            dgv.ColumnHeadersHeight = 34;
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            dgv.DefaultCellStyle.Padding = new Padding(4, 2, 4, 2);

            var colSel = new DataGridViewCheckBoxColumn { Name = "Sel", HeaderText = "In", Width = 40, FillWeight = 40, SortMode = DataGridViewColumnSortMode.NotSortable };
            dgv.Columns.Add(colSel);
            AddCol("STT", "STT", 44, true);
            AddCol("Number", "Sheet Number", 200, false);
            AddCol("Title", "Sheet Title", 300, false);
            AddCol("Rev", "Revision", 60, false);
            AddCol("RevDate", "Revision Date", 80, false);
            AddCol("Purpose", "Issue Purpose", 200, false);
            // SUBSET: không dùng cột riêng. Thay vào đó chèn 1 dòng tiêu đề trước sheet đầu tiên của mỗi subset.
            // (dòng tiêu đề sẽ hiển thị ở cột "Sheet Title")
            // 2 cột SHT/CONT đứng trước Layout Name
            AddCol("cust::SHT", "SHT", 60, false);
            AddCol("cust::CONT", "CONT", 60, false);
            AddCol("LayoutName", "Layout name", 200, false);
            AddCol("DwgPath", "DWG path", 200, false);
            // Nút duyệt DWG theo từng sheet
            dgv.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "DwgBrowse",
                HeaderText = "",
                Text = "...",
                UseColumnTextForButtonValue = true,
                Width = 36,
                FillWeight = 36,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            // Tránh trùng cột SHT/CONT (đã add phía trên)
            foreach (var k in _customKeys)
            {
                if (string.Equals(k, "SHT", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(k, "CONT", StringComparison.OrdinalIgnoreCase)) continue;
                AddCol("cust::" + k, k, 80, false);
            }
            AddCol("File", "Tên file PDF", 300, true);

            dgv.Columns["STT"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            dgv.Columns["STT"].DefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
            dgv.Columns["Rev"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            dgv.CurrentCellDirtyStateChanged += (s, e) => { if (dgv.IsCurrentCellDirty) dgv.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            dgv.CellMouseDown += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) _shiftDown = (Control.ModifierKeys & Keys.Shift) == Keys.Shift; };

            // Tô đỏ DWG path nếu SSM không tìm thấy file DWG
            dgv.CellFormatting += (s, e) =>
            {
                try
                {
                    if (e.RowIndex < 0) return;
                    if (dgv.Columns[e.ColumnIndex].Name != "DwgPath") return;

                    string p = e.Value == null ? "" : e.Value.ToString();
                    if (string.IsNullOrWhiteSpace(p)) return;

                    bool ok = File.Exists(p);
                    e.CellStyle.ForeColor = ok ? dgv.DefaultCellStyle.ForeColor : Color.Red;
                }
                catch { }
            };

            // Header row (subset): 1) Ẩn checkbox "In"  2) Ẩn nút "..." chọn DWG
            // 3) Hiển thị nút +/- ở cột STT để thu gọn/bung sheet của subset (như SSM)
            // 4) Hiển thị tên subset ở cột "Sheet Number"
            dgv.CellPainting += (s, e) =>
            {
                try
                {
                    if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                    var row = dgv.Rows[e.RowIndex];
                    bool isHeader = (row != null && row.Tag is string && ((string)row.Tag).StartsWith("__SUBSET__", StringComparison.Ordinal));
                    if (!isHeader) return;

                    string subsetKey = "";
                    try { subsetKey = ((string)row.Tag).Substring("__SUBSET__".Length); } catch { subsetKey = ""; }

                    string col = dgv.Columns[e.ColumnIndex].Name;

                    // (1) Ẩn checkbox cột In ở header
                    if (col == "Sel")
                    {
                        e.PaintBackground(e.ClipBounds, true);
                        e.Handled = true;
                        return;
                    }

                    // (2) Ẩn nút "..." cột duyệt DWG ở header
                    if (col == "DwgBrowse")
                    {
                        e.PaintBackground(e.ClipBounds, true);
                        e.Handled = true;
                        return;
                    }

                    // (3) Vẽ lại cell "Sheet Number" để đảm bảo luôn hiện chữ (kể cả khi selected)
                    if (col == "Number")
                    {
                        Rectangle r = e.CellBounds;

                        // Nếu dòng header đang được chọn, dùng màu selection để không bị "mất chữ"
                        bool sel = false;
                        try { sel = row.Selected; } catch { }
                        Color back = sel ? dgv.DefaultCellStyle.SelectionBackColor : row.DefaultCellStyle.BackColor;
                        Color fore = sel ? dgv.DefaultCellStyle.SelectionForeColor : row.DefaultCellStyle.ForeColor;

                        using (var b = new SolidBrush(back))
                        using (var p = new Pen(dgv.GridColor))
                        {
                            e.Graphics.FillRectangle(b, r);
                            e.Graphics.DrawRectangle(p, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1));
                        }

                        string subsetName = "";
                        try { subsetName = Convert.ToString(row.Cells["Number"].Value ?? ""); } catch { }
                        subsetName = (subsetName ?? "").Trim();

                        TextRenderer.DrawText(
                        e.Graphics,
                        subsetName,
                        row.DefaultCellStyle.Font ?? dgv.Font,
                        new Rectangle(r.X + 6, r.Y, r.Width - 10, r.Height),
                        fore,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                        e.Handled = true;
                        return;
                    }

                    // Nút +/- ở cột STT để thu gọn/bung subset
                    if (col == "STT")
                    {
                        Rectangle r = e.CellBounds;

                        bool sel = false;
                        try { sel = row.Selected; } catch { }
                        Color back = sel ? dgv.DefaultCellStyle.SelectionBackColor : row.DefaultCellStyle.BackColor;
                        Color fore = sel ? dgv.DefaultCellStyle.SelectionForeColor : row.DefaultCellStyle.ForeColor;

                        using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, r);

                        bool collapsed = false;
                        try { collapsed = _subsetCollapsed.ContainsKey(subsetKey) && _subsetCollapsed[subsetKey]; } catch { collapsed = false; }
                        string sign = collapsed ? "+" : "-";

                        TextRenderer.DrawText(
                        e.Graphics,
                        sign,
                        row.DefaultCellStyle.Font ?? dgv.Font,
                        new Rectangle(r.X, r.Y, r.Width, r.Height),
                        fore,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                        e.Handled = true;
                        return;
                    }

                    // (header) cell Number đã được tự vẽ ở block phía trên, không xử lý ở đây nữa

                    // Chặn cell Title của header tự vẽ chữ (nếu không sẽ bị trùng text subset)
                    if (col == "Title")
                    {
                        // Bỏ đường kẻ dọc giữa Number|Title
                        try { e.AdvancedBorderStyle.Left = DataGridViewAdvancedCellBorderStyle.None; } catch { }
                        e.PaintBackground(e.ClipBounds, true);
                        e.Handled = true;
                        return;
                    }
                }
                catch { }
            };

            // Nút "..." để duyệt lại DWG cho từng sheet
            dgv.CellContentClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;

                // Click vào +/- (cột STT) để thu gọn/bung subset
                if (dgv.Columns[e.ColumnIndex].Name == "STT")
                {
                    var r = dgv.Rows[e.RowIndex];
                    if (r != null && r.Tag is string && ((string)r.Tag).StartsWith("__SUBSET__", StringComparison.Ordinal))
                    {
                        string sk = "";
                        try { sk = ((string)r.Tag).Substring("__SUBSET__".Length); } catch { sk = ""; }
                        bool cur = false;
                        try { cur = _subsetCollapsed.ContainsKey(sk) && _subsetCollapsed[sk]; } catch { cur = false; }
                        _subsetCollapsed[sk] = !cur;
                        BuildRows();
                        UpdateAllPreviews();
                        return;
                    }
                }

                if (dgv.Columns[e.ColumnIndex].Name != "DwgBrowse") return;

                var row = dgv.Rows[e.RowIndex];
                var sheet = row.Tag as SheetInfo;
                if (sheet == null) return;

                using (var dlg = new OpenFileDialog())
                {
                    dlg.Filter = "DWG (*.dwg)|*.dwg";
                    dlg.Title = "Chọn file DWG";
                    dlg.Multiselect = false;

                    try
                    {
                        string current = sheet.DwgPath ?? "";
                        if (!string.IsNullOrWhiteSpace(current))
                        {
                            dlg.InitialDirectory = Path.GetDirectoryName(current);
                            dlg.FileName = Path.GetFileName(current);
                        }
                    }
                    catch { }

                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    // Set cell -> sẽ kích hoạt CellValueChanged + propagate DWG path theo rule hiện tại
                    row.Cells["DwgPath"].Value = dlg.FileName;
                    dgv.EndEdit();
                }
            };
            dgv.CellValueChanged += (s, e) =>
            {
                if (_bulk || e.RowIndex < 0) return;
                var row = dgv.Rows[e.RowIndex];
                var sheet = row.Tag as SheetInfo;
                if (sheet == null) return;
                string col = dgv.Columns[e.ColumnIndex].Name;

                if (col == "Sel")
                {
                    bool isChecked = Convert.ToBoolean(row.Cells[0].Value ?? false);
                    ApplyCheck(sheet, isChecked);
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
                    return;
                }

                if (col == "DwgPath")
                {
                    // Nếu sửa DWG path ở 1 sheet thì tất cả sheet dùng cùng DWG cũ cũng phải đổi theo
                    string oldPath = sheet.DwgPath ?? "";
                    string newPath = Str(row, "DwgPath");
                    if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var sh in _sheets)
                        {
                            if (string.Equals((sh.DwgPath ?? ""), oldPath, StringComparison.OrdinalIgnoreCase))
                                sh.DwgPath = newPath;
                        }

                        // cập nhật UI (các row đang hiển thị)
                        _bulk = true;
                        foreach (DataGridViewRow r in dgv.Rows)
                        {
                            var sh2 = r.Tag as SheetInfo;
                            if (sh2 != null && string.Equals((sh2.DwgPath ?? ""), newPath, StringComparison.OrdinalIgnoreCase))
                                r.Cells["DwgPath"].Value = newPath;
                        }
                        _bulk = false;
                    }
                }

                CommitRow(row, sheet);
                UpdateAllPreviews();
            };
            Controls.Add(dgv);

            BuildRows();

            const int btnTop = 754;
            btnAll = new Button { Text = "Chọn tất cả", Left = 20, Top = btnTop, Width = 120, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnAll.Click += (s, e) => { _excluded.Clear(); SyncChecks(); UpdateAllPreviews(); };
            Controls.Add(btnAll);

            btnNone = new Button { Text = "Bỏ chọn tất cả", Left = 148, Top = btnTop, Width = 130, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnNone.Click += (s, e) => { _excluded.Clear(); foreach (var sh in _sheets) _excluded.Add(sh); SyncChecks(); UpdateAllPreviews(); };
            Controls.Add(btnNone);

            btnExport = new Button { Text = "Xuất Excel", Left = 292, Top = btnTop, Width = 120, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnExport.Click += (s, e) => ExportToExcel();
            Controls.Add(btnExport);

            btnSave = new Button { Text = "Lưu Sheet Set", Left = rightEdge - 372, Top = btnTop, Width = 150, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnSave.Click += (s, e) => { CommitAll(); Action = SsmAction.Save; DialogResult = DialogResult.OK; };

            btnPrint = new Button { Text = "In PDF", Left = rightEdge - 214, Top = btnTop, Width = 110, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnPrint.Click += (s, e) => { CommitAll(); SaveProjectNumber(); Action = SsmAction.Print; DialogResult = DialogResult.OK; };

            btnCancel = new Button { Text = "Đóng", Left = rightEdge - 96, Top = btnTop, Width = 96, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };

            Controls.Add(btnSave); Controls.Add(btnPrint); Controls.Add(btnCancel);
            AcceptButton = btnPrint; CancelButton = btnCancel;

            UpdateAllPreviews();
        }

        // (rest of methods are unchanged from repo except Top offsets already handled above)
        private const string RegPath = @"Software\CADtools";
        private static string LoadSavedProjectNumber()
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath)) return k == null ? "" : (k.GetValue("ProjectNumber") as string ?? ""); }
            catch { return ""; }
        }
        public void SaveProjectNumber()
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath)) if (k != null) k.SetValue("ProjectNumber", ProjectNumber ?? ""); }
            catch { }
        }

        private void ApplyCheck(SheetInfo sheet, bool isChecked)
        {
            if (isChecked) _excluded.Remove(sheet); else _excluded.Add(sheet);
        }

        private void AddCol(string name, string header, int width, bool readOnly)
        {
            dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, FillWeight = width, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        private void BuildTokenButtons()
        {
            var tokens = new System.Collections.Generic.List<string> { "SheetNumber", "SheetTitle", "SheetDesc", "SheetSetName", "LayoutName", "DwgName", "Revision", "RevisionDate", "IssuePurpose" };
            var customShow = new[] { "SHT", "CONT", "Project Number" };
            var customKeys = _sheets.SelectMany(s => s.Custom.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => customShow.Any(w => string.Equals(w, k, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(k => Array.FindIndex(customShow, w => string.Equals(w, k, StringComparison.OrdinalIgnoreCase)));
            foreach (var k in customKeys) tokens.Add(k);

            foreach (var t in tokens)
            {
                var b = new Button { Text = t, AutoSize = true, Margin = new Padding(3), Padding = new Padding(4, 2, 4, 2) };
                string token = "$(" + t + ")";
                b.Click += (s, e) => { int i = txtTemplate.SelectionStart; txtTemplate.Text = txtTemplate.Text.Insert(i, token); txtTemplate.SelectionStart = i + token.Length; txtTemplate.Focus(); };
                pnlTokens.Controls.Add(b);
            }
        }

        private void BuildRows()
        {
            _bulk = true;
            try
            {
                dgv.Rows.Clear();

                string lastSubset = null;
                int stt = 0;

                for (int idx = 0; idx < _sheets.Count; idx++)
                {
                    var s = _sheets[idx];
                    string subset = (s == null ? "" : (s.SubsetPath ?? "")).Trim();

                    // Khi chuyển subset, chèn 1 dòng tiêu đề (group header) trước sheet đầu tiên của subset
                    if (!string.Equals(lastSubset, subset, StringComparison.OrdinalIgnoreCase))
                    {
                        int hi = dgv.Rows.Add();
                        var hr = dgv.Rows[hi];
                        hr.Tag = "__SUBSET__" + (subset ?? ""); // header row
                        hr.ReadOnly = true;

                        // Style header row
                        hr.DefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
                        hr.DefaultCellStyle.ForeColor = Color.FromArgb(60, 60, 60);
                        hr.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);

                        // Header: ghi tên subset vào cột "Sheet Number" (đỡ phải gộp cột)
                        hr.Cells["Sel"].Value = false;
                        hr.Cells["STT"].Value = "";
                        hr.Cells["Number"].Value = string.IsNullOrWhiteSpace(subset) ? "[ROOT]" : subset;
                        hr.Cells["Title"].Value = "";

                        lastSubset = subset;
                    }

                    // Nếu subset đang collapsed thì bỏ qua không add sheet rows
                    bool collapsed = false;
                    try { collapsed = _subsetCollapsed.ContainsKey(subset) && _subsetCollapsed[subset]; } catch { collapsed = false; }
                    if (collapsed) continue;

                    stt++;
                    int i = dgv.Rows.Add();
                    var row = dgv.Rows[i];
                    row.Tag = s;
                    row.Cells["Sel"].Value = !_excluded.Contains(s);
                    row.Cells["STT"].Value = stt.ToString();
                    row.Cells["Number"].Value = s.Number;
                    row.Cells["Title"].Value = s.Title;
                    row.Cells["Rev"].Value = s.Revision;
                    row.Cells["RevDate"].Value = s.RevisionDate;
                    row.Cells["Purpose"].Value = s.IssuePurpose;
                    row.Cells["LayoutName"].Value = s.LayoutName;
                    row.Cells["DwgPath"].Value = s.DwgPath;
                    foreach (var k in _customKeys)
                    {
                        string v; s.Custom.TryGetValue(k, out v);
                        row.Cells["cust::" + k].Value = v ?? "";
                    }
                }
            }
            finally { _bulk = false; }
        }

        private void SyncChecks()
        {
            _bulk = true;
            foreach (DataGridViewRow row in dgv.Rows)
            {
                var s = row.Tag as SheetInfo;
                if (s != null) row.Cells["Sel"].Value = !_excluded.Contains(s);
            }
            _bulk = false;
        }

        private SheetInfo FirstSelected()
        {
            foreach (var s in _sheets) if (!_excluded.Contains(s)) return s;
            return _sheets.Count > 0 ? _sheets[0] : null;
        }

        private void UpdateAllPreviews()
        {
            if (dgv == null) return;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string mergedName = null;
            if (chkMerged.Checked)
            {
                mergedName = SsmNaming.EnsurePdf(SsmNaming.SanitizeFile(SsmNaming.Resolve(Template, FirstSelected(), true, ProjectNumber)));
                if (string.IsNullOrWhiteSpace(mergedName)) mergedName = "MergedSheets.pdf";
            }
            foreach (DataGridViewRow row in dgv.Rows)
            {
                var s = row.Tag as SheetInfo;
                if (s == null) continue;
                string fileName;
                if (chkMerged.Checked) fileName = mergedName;
                else
                {
                    string name = SsmNaming.SanitizeFile(SsmNaming.Resolve(Template, s, false, ProjectNumber));
                    if (string.IsNullOrWhiteSpace(name)) name = s.LayoutName;
                    string baseName = name; int n = 2;
                    while (!used.Add(name)) name = baseName + " (" + (n++) + ")";
                    fileName = SsmNaming.EnsurePdf(name);
                }
                row.Cells["File"].Value = fileName;
            }
        }

        private void CommitRow(DataGridViewRow row, SheetInfo s)
        {
            s.Number = Str(row, "Number");
            s.Title = Str(row, "Title");
            s.Revision = Str(row, "Rev");
            s.RevisionDate = Str(row, "RevDate");
            s.IssuePurpose = Str(row, "Purpose");
            s.LayoutName = Str(row, "LayoutName");
            s.DwgPath = Str(row, "DwgPath");
            s.EditableCustomKeys = _customKeys;
            foreach (var k in _customKeys) s.Custom[k] = Str(row, "cust::" + k);
        }

        private void CommitAll()
        {
            dgv.EndEdit();
            foreach (DataGridViewRow row in dgv.Rows)
            {
                var s = row.Tag as SheetInfo;
                if (s != null) CommitRow(row, s);
            }
        }

        private void ExportToExcel()
        {
            try
            {
                dgv.EndEdit();
                using (var dlg = new SaveFileDialog { Title = "Xuất bảng Sheet Set ra Excel", Filter = "CSV (mở bằng Excel)|*.csv", FileName = "SheetSet_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    string sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
                    if (string.IsNullOrEmpty(sep)) sep = ",";

                    var sb = new StringBuilder();
                    var headers = new List<string>();
                    foreach (DataGridViewColumn c in dgv.Columns)
                        if (c.Visible && c.Name != "Sel") headers.Add(Csv(c.HeaderText, sep));
                    sb.AppendLine(string.Join(sep, headers.ToArray()));

                    foreach (DataGridViewRow row in dgv.Rows)
                    {
                        if (row.IsNewRow) continue;
                        var cells = new List<string>();
                        foreach (DataGridViewColumn c in dgv.Columns)
                        {
                            if (!c.Visible || c.Name == "Sel") continue;
                            var v = row.Cells[c.Index].Value;
                            cells.Add(Csv(v == null ? "" : v.ToString(), sep));
                        }
                        sb.AppendLine(string.Join(sep, cells.ToArray()));
                    }

                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));

                    if (MessageBox.Show("Đã xuất: " + dlg.FileName + Environment.NewLine + "Mở file ngay?", "Xuất Excel", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không xuất được: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string Csv(string s, string sep)
        {
            if (s == null) s = "";
            const string q = "\"";
            bool needQuote = s.IndexOf(sep, StringComparison.Ordinal) >= 0 || s.Contains(q) || s.Contains("\n") || s.Contains("\r");
            s = s.Replace(q, q + q);
            return needQuote ? q + s + q : s;
        }

        private static string Str(DataGridViewRow row, string col)
        {
            var v = row.Cells[col].Value;
            return v == null ? "" : v.ToString();
        }
    }
}