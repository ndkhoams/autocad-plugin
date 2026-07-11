using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace BatchPlotPdf
{
    // Form GOP duy nhat: vua dat ten & in PDF theo Sheet Set, vua sua & luu nguoc Sheet Set (.dst),
    // vua xuat Excel — tat ca trong 1 cua so (khong con tach MTECH/SSMEDIT thanh 2 form rieng).
    public class PlotNamingForm : Form
    {
        public enum SsmAction { None, Print, Save }

        private readonly List<SheetInfo> _sheets;
        private readonly List<string> _customKeys;
        // Chi cac custom key nay hien thanh cot sua & duoc ghi nguoc (bo Client/Project/Total sheet...).
        private static readonly string[] _whitelist = { "CONT", "SHT" };

        private TextBox txtTemplate, txtOutDir, txtProjNum;
        private FlowLayoutPanel pnlTokens;
        private CheckBox chkMerged;
        private DataGridView dgv;
        private Button btnBrowse, btnPrint, btnSave, btnExport, btnCancel, btnAll, btnNone;
        private readonly HashSet<SheetInfo> _excluded = new HashSet<SheetInfo>();
        private int _lastCheckRow = -1;   // ho tro Shift-chon ca dai hang
        private bool _shiftDown = false;
        private bool _bulk = false;       // chan de quy khi set tick hang loat

        public SsmAction Action { get; private set; }
        public string Template { get { return txtTemplate.Text; } }
        public string OutputDir { get { return txtOutDir.Text; } }
        public string ProjectNumber { get { return txtProjNum.Text.Trim(); } }
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
            _customKeys = _sheets.SelectMany(s => s.Custom.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(k => _whitelist.Any(w => string.Equals(w, k, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(k => Array.FindIndex(_whitelist, w => string.Equals(w, k, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Text = "Sheet Set → In PDF & Quản lý";
            ClientSize = new Size(1200, 800); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.75f);
            MinimumSize = new Size(1000, 640);
            Padding = new Padding(6);

            const int labelW = 110;
            const int fieldL = 130;
            const int rightEdge = 1180;

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
                Width = rightEdge - fieldL,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = "$(Project Number)-$(SheetNumber)-Sht$(SHT)-($(Revision))"
            };
            txtTemplate.TextChanged += (s, e) => UpdateAllPreviews();
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
                Width = rightEdge - fieldL,
                Height = 88,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                WrapContents = true,
                Padding = new Padding(2)
            };
            Controls.Add(pnlTokens);
            BuildTokenButtons();

            // Project number: AutoCAD khong cho doc gia tri that qua COM (bag chi tra ve default),
            // nen nhap tay o day; token $(Project Number) trong mau ten se dung gia tri nay.
            var lblProj = new Label
            {
                Text = "Project number:",
                Left = 20,
                Top = 162,
                Width = labelW,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(lblProj);
            txtProjNum = new TextBox
            {
                Left = fieldL,
                Top = 160,
                Width = rightEdge - fieldL,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = LoadSavedProjectNumber()
            };
            txtProjNum.TextChanged += (s, e) => UpdateAllPreviews();
            Controls.Add(txtProjNum);

            var lblDir = new Label
            {
                Text = "Thư mục lưu:",
                Left = 20,
                Top = 210,
                Width = labelW,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(lblDir);
            txtOutDir = new TextBox
            {
                Left = fieldL,
                Top = 208,
                Width = rightEdge - fieldL - 50,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = defaultDir ?? ""
            };
            txtOutDir.TextChanged += (s, e) => UpdateAllPreviews();
            Controls.Add(txtOutDir);
            btnBrowse = new Button
            {
                Text = "...",
                Left = rightEdge - 44,
                Top = 207,
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
                Top = 246,
                Width = 600,
                Height = 24
            };
            chkMerged.CheckedChanged += (s, e) => UpdateAllPreviews();
            Controls.Add(chkMerged);

            var lblHint = new Label
            {
                Text = "Sửa trực tiếp trong bảng (Số sheet, Tiêu đề, Revision, Ngày rev, Issue purpose, CONT, SHT). "
                     + "Giữ Shift rồi tích để chọn/bỏ cả dải. Nút \"In PDF\" chỉ in sheet đang tích; nút \"Lưu Sheet Set\" ghi thay đổi ngược vào .dst.",
                Left = 20,
                Top = 276,
                Width = rightEdge - 20,
                Height = 24,
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(lblHint);

            dgv = new DataGridView
            {
                Left = 20,
                Top = 306,
                Width = rightEdge - 20,
                Height = 434,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                ReadOnly = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                BorderStyle = BorderStyle.FixedSingle
            };
            dgv.RowTemplate.Height = 28;
            dgv.ColumnHeadersHeight = 34;
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            dgv.DefaultCellStyle.Padding = new Padding(4, 2, 4, 2);

            var colSel = new DataGridViewCheckBoxColumn
            {
                Name = "Sel",
                HeaderText = "In",
                Width = 40,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            dgv.Columns.Add(colSel);
            AddCol("STT", "STT", 44, true);
            AddCol("Number", "Số sheet", 150, false);
            AddCol("Title", "Tiêu đề", 300, false);
            AddCol("Rev", "Revision", 80, false);
            AddCol("RevDate", "Ngày rev", 100, false);
            AddCol("Purpose", "Issue purpose", 150, false);
            foreach (var k in _customKeys) AddCol("cust::" + k, k, 80, false);
            AddCol("File", "Tên file PDF", 300, true);

            dgv.Columns["STT"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            dgv.Columns["STT"].DefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
            dgv.Columns["Rev"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            // commit tick ngay khi bam (khong can roi o) cho checkbox
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
                if (_bulk || e.RowIndex < 0) return;
                var row = dgv.Rows[e.RowIndex];
                var sheet = row.Tag as SheetInfo;
                if (sheet == null) return;
                string col = dgv.Columns[e.ColumnIndex].Name;

                if (col == "Sel")
                {
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
                    return;
                }

                // Sua cot du lieu -> cap nhat model roi tinh lai ten file (anh huong dedup nen tinh lai het).
                CommitRow(row, sheet);
                UpdateAllPreviews();
            };
            Controls.Add(dgv);

            BuildRows();

            const int btnTop = 754;
            btnAll = new Button
            {
                Text = "Chọn tất cả",
                Left = 20,
                Top = btnTop,
                Width = 120,
                Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnAll.Click += (s, e) => { _excluded.Clear(); SyncChecks(); UpdateAllPreviews(); };
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
            btnNone.Click += (s, e) => { _excluded.Clear(); foreach (var sh in _sheets) _excluded.Add(sh); SyncChecks(); UpdateAllPreviews(); };
            Controls.Add(btnNone);
            btnExport = new Button
            {
                Text = "Xuất Excel",
                Left = 292,
                Top = btnTop,
                Width = 120,
                Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnExport.Click += (s, e) => ExportToExcel();
            Controls.Add(btnExport);

            btnSave = new Button
            {
                Text = "Lưu Sheet Set",
                Left = rightEdge - 372,
                Top = btnTop,
                Width = 150,
                Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            btnSave.Click += (s, e) => { CommitAll(); Action = SsmAction.Save; DialogResult = DialogResult.OK; };
            btnPrint = new Button
            {
                Text = "In PDF",
                Left = rightEdge - 214,
                Top = btnTop,
                Width = 110,
                Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            btnPrint.Click += (s, e) => { CommitAll(); SaveProjectNumber(); Action = SsmAction.Print; DialogResult = DialogResult.OK; };
            btnCancel = new Button
            {
                Text = "Đóng",
                Left = rightEdge - 96,
                Top = btnTop,
                Width = 96,
                Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnSave); Controls.Add(btnPrint); Controls.Add(btnCancel);
            AcceptButton = btnPrint; CancelButton = btnCancel;

            UpdateAllPreviews();
        }

        // Luu/doc Project number trong registry HKCU de nho cho lan sau.
        private const string RegPath = @"Software\BatchPlotPdf";
        private static string LoadSavedProjectNumber()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath))
                    return k == null ? "" : (k.GetValue("ProjectNumber") as string ?? "");
            }
            catch { return ""; }
        }
        public void SaveProjectNumber()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath))
                    if (k != null) k.SetValue("ProjectNumber", ProjectNumber ?? "");
            }
            catch { }
        }

        private void ApplyCheck(SheetInfo sheet, bool isChecked)
        {
            if (isChecked) _excluded.Remove(sheet); else _excluded.Add(sheet);
        }

        private void AddCol(string name, string header, int width, bool readOnly)
        {
            dgv.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                ReadOnly = readOnly,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        private void BuildTokenButtons()
        {
            var tokens = new List<string> {
                "SheetNumber", "SheetTitle", "SheetDesc", "SheetSetName", "LayoutName", "DwgName",
                "Revision", "RevisionDate", "IssuePurpose" };
            // Chi hien cac custom token thuc su dung (CONT, SHT, Project Number); bo cac property
            // cap Sheet Set khong dung: Client, Project Address Line 1/2/3, Project Name, Total sheet.
            var customShow = new[] { "CONT", "SHT", "Project Number" };
            var customKeys = _sheets.SelectMany(s => s.Custom.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(k => customShow.Any(w => string.Equals(w, k, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(k => Array.FindIndex(customShow, w => string.Equals(w, k, StringComparison.OrdinalIgnoreCase)));
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

        // Dung 1 lan: tao du hang tu _sheets (giu Tag = SheetInfo de sua truc tiep).
        // QUAN TRONG: phai dat _bulk=true trong luc do hang. Neu khong, moi lan gan .Value cho
        // 1 o se ban su kien CellValueChanged -> CommitRow doc lai CA hang (luc do cac o khac
        // CHUA duoc gan, dang null) roi ghi "" nguoc vao SheetInfo -> xoa sach Number/Title/
        // Rev... TRUOC khi chung kip gan -> bang hien ra rong. _bulk chan dung viec do.
        private void BuildRows()
        {
            _bulk = true;
            try
            {
                dgv.Rows.Clear();
                for (int idx = 0; idx < _sheets.Count; idx++)
                {
                    var s = _sheets[idx];
                    int i = dgv.Rows.Add();
                    var row = dgv.Rows[i];
                    row.Tag = s;
                    row.Cells["Sel"].Value = !_excluded.Contains(s);
                    row.Cells["STT"].Value = (idx + 1).ToString();
                    row.Cells["Number"].Value = s.Number;
                    row.Cells["Title"].Value = s.Title;
                    row.Cells["Rev"].Value = s.Revision;
                    row.Cells["RevDate"].Value = s.RevisionDate;
                    row.Cells["Purpose"].Value = s.IssuePurpose;
                    foreach (var k in _customKeys)
                    {
                        string v; s.Custom.TryGetValue(k, out v);
                        row.Cells["cust::" + k].Value = v ?? "";
                    }
                }
            }
            finally { _bulk = false; }
        }

        // Dong bo cot tick voi _excluded (sau khi bam Chon/Bo chon tat ca).
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

        // Tinh lai cot "Tên file PDF" cho tat ca hang (khong dung vao du lieu dang sua).
        private void UpdateAllPreviews()
        {
            if (dgv == null) return;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string mergedName = null;
            if (chkMerged.Checked)
            {
                mergedName = SsmNaming.EnsurePdf(SsmNaming.SanitizeFile(
                    SsmNaming.Resolve(Template, FirstSelected(), true, ProjectNumber)));
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

        // Day gia tri 1 hang vao SheetInfo (de In/Luu deu dung so lieu moi nhat).
        private void CommitRow(DataGridViewRow row, SheetInfo s)
        {
            s.Number = Str(row, "Number");
            s.Title = Str(row, "Title");
            s.Revision = Str(row, "Rev");
            s.RevisionDate = Str(row, "RevDate");
            s.IssuePurpose = Str(row, "Purpose");
            s.EditableCustomKeys = _customKeys;   // chi ghi nguoc CONT/SHT
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

        // Xuat bang hien tai (ke ca chinh sua chua luu) ra CSV UTF-8 co BOM -> Excel mo truc tiep.
        private void ExportToExcel()
        {
            try
            {
                dgv.EndEdit();
                using (var dlg = new SaveFileDialog
                {
                    Title = "Xuất bảng Sheet Set ra Excel",
                    Filter = "CSV (mở bằng Excel)|*.csv",
                    FileName = "SheetSet_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv"
                })
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

                    if (MessageBox.Show("Đã xuất: " + dlg.FileName + Environment.NewLine + "Mở file ngay?",
                        "Xuất Excel", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không xuất được: " + ex.Message, "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string Csv(string s, string sep)
        {
            if (s == null) s = "";
            const string q = "\"";
            bool needQuote = s.IndexOf(sep, StringComparison.Ordinal) >= 0
                || s.Contains(q) || s.Contains("\n") || s.Contains("\r");
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