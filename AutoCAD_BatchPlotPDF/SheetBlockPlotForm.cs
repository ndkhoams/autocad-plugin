using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Exception = System.Exception; // tránh nhầm với Autodesk.AutoCAD.Runtime.Exception

namespace CADtools
{
    // SBP: liệt kê & in từng block khung tên trong Model + PaperSpace
    public class SheetBlockPlotForm : Form
    {
        private readonly Document _doc;
        private readonly Editor _ed;

        private TextBox _txtBlockName;
        private Button _btnRefresh;
        private Button _btnFilterWindow;
        private Button _btnBrowseOut;
        private TextBox _txtOutDir;
        private ComboBox _cbPaper;
        private ComboBox _cbStyle;
        // Fit luôn bật -> bỏ checkbox khỏi UI
        private DataGridView _grid;
        private Button _btnPrint;
        private Button _btnClose;
        private Label _lblSelInfo;

        private Button _btnSelAll;
        private Button _btnSelNone;
        private Button _btnExport;

        // Trạng thái thu gọn theo Hạng mục
        private readonly Dictionary<string, bool> _hmCollapsed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Filter theo vùng quét: lưu handle các block nằm trong selection (null = không lọc)
        private HashSet<string> _windowFilterHandles = null;
        // Map hiển thị khổ giấy (A0/A1/A2/A3) -> canonical media name (ISO_full_bleed_...)
        private readonly Dictionary<string, string> _paperMediaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
             {
             { "A0", "ISO_full_bleed_A0_(1189.00_x_841.00_MM)" },
             { "A1", "ISO_full_bleed_A1_(841.00_x_594.00_MM)" },
             { "A2", "ISO_full_bleed_A2_(594.00_x_420.00_MM)" },
             { "A3", "ISO_full_bleed_A3_(420.00_x_297.00_MM)" },
             };

        // NOTE (UI): combobox _cbPaper sẽ hiển thị A0/A1/A2/A3.
        // Khi in: map A0/A1/A2/A3 -> ISO_full_bleed_* bằng _paperMediaMap.

        private readonly List<BlockItem> _items = new List<BlockItem>();

        private class BlockItem
        {
            public int Stt = 0;
            public string LayoutName = ""; // "Model" hoặc tên layout
            public string KyHieu = "";     // MT_KH
            public string HangMuc = "";    // MT_TENHANGMUC
            public string TenBanVe = "";   // MT_TENBANVE
            public string PdfName = "";    // tên file PDF sẽ xuất
            public string Handle = "";

            // Vị trí block (WCS) để sort theo thứ tự trái -> phải, trên -> dưới
            // Gốc so sánh: góc trên - bên trái (Y lớn hơn = ở trên)
            public double PosX = 0;
            public double PosY = 0;

            public Extents2d Window; // WCS 2D window
            public bool RectLandscape = true; // hướng khung (theo RECT polyline), để xoay in cho đúng
        }

        public SheetBlockPlotForm(Document doc)
        {
            _doc = doc;
            _ed = doc.Editor;

            Text = "Sheet Block Manager and Printer - V1.0 ©KhoaND";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1250, 720);
            Font = new System.Drawing.Font("Segoe UI", 9.75f);
            MinimumSize = new Size(900, 600);

            int y = 10;
            Controls.Add(new Label { Left = 10, Top = y + 4, Width = 140, Height = 24, Text = "Block Khung Tên:", TextAlign = ContentAlignment.MiddleLeft });
            _txtBlockName = new TextBox { Left = 150, Top = y, Width = 210, Height = 28, Text = "KHUNG" };
            Controls.Add(_txtBlockName);

            _btnRefresh = new Button { Left = 370, Top = y, Width = 90, Height = 28, Text = "Refresh" };
            _btnRefresh.Click += (s, e) => RefreshList();
            Controls.Add(_btnRefresh);

            _btnFilterWindow = new Button { Left = 465, Top = y, Width = 150, Height = 28, Text = "Chọn vùng in" };
            _btnFilterWindow.Click += (s, e) => FilterByWindow();
            Controls.Add(_btnFilterWindow);

            // Dời nhóm Paper/Nét in sang phải để không bị đè với nút "Lọc vùng"
            Controls.Add(new Label { Left = 645, Top = y + 4, Width = 75, Height = 24, Text = "Khổ giấy:", TextAlign = ContentAlignment.MiddleLeft });
            _cbPaper = new ComboBox { Left = 720, Top = y, Width = 100, Height = 28, DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(_cbPaper);

            Controls.Add(new Label { Left = 840, Top = y + 4, Width = 60, Height = 24, Text = "Nét in:", TextAlign = ContentAlignment.MiddleLeft });
            _cbStyle = new ComboBox { Left = 900, Top = y, Width = 300, Height = 28, DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(_cbStyle);

            // Fit luôn bật -> bỏ checkbox (không hiển thị)

            y += 38;
            Controls.Add(new Label { Left = 10, Top = y + 4, Width = 140, Height = 24, Text = "Output folder:", TextAlign = ContentAlignment.MiddleLeft });
            _txtOutDir = new TextBox { Left = 150, Top = y, Width = 670, Height = 28, Text = DefaultOutDir() };
            Controls.Add(_txtOutDir);
            _btnBrowseOut = new Button { Left = 830, Top = y, Width = 40, Height = 28, Text = "..." };
            _btnBrowseOut.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    if (d.ShowDialog(this) == DialogResult.OK) _txtOutDir.Text = d.SelectedPath;
                }
            };
            Controls.Add(_btnBrowseOut);

            y += 40;
            _grid = new DataGridView
            {
                Left = 10,
                Top = y,
                Width = ClientSize.Width - 20,
                Height = ClientSize.Height - y - 70,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                // Chỉ highlight ô đang chọn, không highlight cả hàng
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = false,
                ReadOnly = false
            };

            // Fix hiển thị: header bị che + dòng data thấp
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
            _grid.ColumnHeadersHeight = 34;
            _grid.RowTemplate.Height = 26;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _grid.AllowUserToResizeRows = false;
            _grid.EnableHeadersVisualStyles = false;

            // Cột In: gọn và canh giữa (giống SSP)
            var colSel = new DataGridViewCheckBoxColumn { Name = "Sel", HeaderText = "In", Width = 50, FillWeight = 50 };
            colSel.ReadOnly = false;
            _grid.Columns.Add(colSel);

            // Cột STT: dùng TextBox để giống SSP (không có border kiểu button)
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Stt",
                HeaderText = "STT",
                Width = 50,
                FillWeight = 50,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            // ATT editable
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "HangMuc", HeaderText = "HẠNG MỤC", FillWeight = 180, ReadOnly = false, SortMode = DataGridViewColumnSortMode.NotSortable });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "KyHieu", HeaderText = "KÝ HIỆU BẢN VẼ", FillWeight = 120, ReadOnly = false, SortMode = DataGridViewColumnSortMode.NotSortable });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TenBanVe", HeaderText = "TÊN BẢN VẼ", FillWeight = 260, ReadOnly = false, SortMode = DataGridViewColumnSortMode.NotSortable });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PdfName", HeaderText = "TÊN FILE PDF", FillWeight = 220, ReadOnly = true });

            // Style 2 cột đầu giống SSP
            try
            {
                _grid.Columns["Sel"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                _grid.Columns["Stt"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                _grid.Columns["Stt"].DefaultCellStyle.Padding = new Padding(0);

                // Giống SSP: cột STT không hiện khung/ô selection dạng "box"
                _grid.Columns["Stt"].DefaultCellStyle.SelectionBackColor = _grid.Columns["Stt"].DefaultCellStyle.BackColor;
                _grid.Columns["Stt"].DefaultCellStyle.SelectionForeColor = _grid.Columns["Stt"].DefaultCellStyle.ForeColor;

                // Giữ đường kẻ ngăn cột (vertical grid lines)
                _grid.CellBorderStyle = DataGridViewCellBorderStyle.Single;
                _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
                _grid.GridColor = Color.Silver;
            }
            catch { }

            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellValueChanged += (s, e) => { if (e != null && e.ColumnIndex >= 0) UpdateSelectionInfo(); };
            _grid.CellEndEdit += (s, e) =>
            {
                try
                {
                    if (e == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                    var row = _grid.Rows[e.RowIndex];
                    if (row == null) return;

                    // Bỏ dòng header hạng mục
                    string tag = Convert.ToString(row.Tag ?? "");
                    if (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith("HM:", StringComparison.OrdinalIgnoreCase)) return;

                    var it = row.Tag as BlockItem;
                    if (it == null) return;

                    string col = _grid.Columns[e.ColumnIndex].Name;
                    if (col != "KyHieu" && col != "HangMuc" && col != "TenBanVe") return;

                    string newVal = Convert.ToString(row.Cells[col].Value ?? "");
                    newVal = (newVal ?? "").Trim();

                    if (col == "KyHieu") it.KyHieu = newVal;
                    if (col == "HangMuc") it.HangMuc = newVal;
                    if (col == "TenBanVe") it.TenBanVe = newVal;

                    // Update lại tên PDF theo logic hiện tại
                    string pdfBase = ((it.KyHieu ?? "") + "_" + (it.TenBanVe ?? "")).Trim('_').Trim();
                    if (string.IsNullOrWhiteSpace(pdfBase)) pdfBase = "KHUNG_MT_" + (it.Handle ?? "");
                    it.PdfName = SanitizeFileName(pdfBase) + ".pdf";
                    try { row.Cells["PdfName"].Value = it.PdfName; } catch { }

                    // Ghi ngược vào block
                    WriteBackAttributes(it);
                }
                catch { }
            };
            _grid.CellClick += (s, e) =>
            {
                try
                {
                    if (e == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                    var row = _grid.Rows[e.RowIndex];
                    if (row == null) return;
                    string tag = Convert.ToString(row.Tag ?? "");
                    if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith("HM:", StringComparison.OrdinalIgnoreCase)) return;
                    string hm = tag.Substring(3);
                    if (string.IsNullOrWhiteSpace(hm)) return;

                    string col = _grid.Columns[e.ColumnIndex].Name;

                    // Bấm +/- để bung/thu (nằm trong ô STT)
                    if (col == "Stt")
                    {
                        ToggleHangMucCollapse(hm);
                        return;
                    }

                    // Tick ở dòng header để chọn/bỏ chọn cả hạng mục (giống SSP)
                    if (col == "Sel")
                    {
                        bool want = false;
                        try { want = Convert.ToBoolean(row.Cells["Sel"].EditedFormattedValue ?? false); } catch { want = false; }
                        SelectHangMuc(hm, want);
                        return;
                    }
                }
                catch { }
            };

            Controls.Add(_grid);

            // Nút chọn/bỏ chọn tất cả + xuất excel (CSV)
            _btnSelAll = new Button { Left = 10, Top = ClientSize.Height - 44, Width = 110, Height = 32, Text = "Chọn tất cả", Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            _btnSelAll.Click += (s, e) => SetAllSelection(true);
            Controls.Add(_btnSelAll);

            _btnSelNone = new Button { Left = 125, Top = ClientSize.Height - 44, Width = 110, Height = 32, Text = "Bỏ chọn", Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            _btnSelNone.Click += (s, e) => SetAllSelection(false);
            Controls.Add(_btnSelNone);

            _btnExport = new Button { Left = 240, Top = ClientSize.Height - 44, Width = 110, Height = 32, Text = "Xuất Excel", Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            _btnExport.Click += (s, e) => ExportCsv();
            Controls.Add(_btnExport);

            _lblSelInfo = new Label { Left = 360, Top = ClientSize.Height - 44, Width = 260, Height = 32, Text = "", Anchor = AnchorStyles.Left | AnchorStyles.Bottom, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(_lblSelInfo);

            _btnPrint = new Button { Left = ClientSize.Width - 240, Top = ClientSize.Height - 44, Width = 110, Height = 32, Text = "In PDF", Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            _btnPrint.Click += (s, e) => PrintSelected();
            Controls.Add(_btnPrint);

            _btnClose = new Button { Left = ClientSize.Width - 120, Top = ClientSize.Height - 44, Width = 110, Height = 32, Text = "Đóng", Anchor = AnchorStyles.Right | AnchorStyles.Bottom, DialogResult = DialogResult.Cancel };
            Controls.Add(_btnClose);
            CancelButton = _btnClose;

            RefreshList();
            UpdateSelectionInfo();
            LoadPlotUiLists();
        }

        private void SetAllSelection(bool value)
        {
            try
            {
                if (_grid == null) return;
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    // bỏ dòng header HM
                    string tag = Convert.ToString(row.Tag ?? "");
                    if (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith("HM:", StringComparison.OrdinalIgnoreCase)) continue;
                    try { row.Cells["Sel"].Value = value; } catch { }
                }
                UpdateSelectionInfo();
            }
            catch { }
        }

        private void SelectHangMuc(string hangMuc, bool value)
        {
            try
            {
                if (_grid == null) return;
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    // bỏ dòng header HM
                    string tag = Convert.ToString(row.Tag ?? "");
                    if (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith("HM:", StringComparison.OrdinalIgnoreCase)) continue;
                    var it = row.Tag as BlockItem;
                    if (it == null) continue;
                    if (!string.Equals(it.HangMuc ?? "", hangMuc ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                    try { row.Cells["Sel"].Value = value; } catch { }
                }
                UpdateSelectionInfo();
            }
            catch { }
        }

        private void ToggleHangMucCollapse(string hangMuc)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hangMuc)) return;
                bool cur = false;
                _hmCollapsed.TryGetValue(hangMuc, out cur);
                _hmCollapsed[hangMuc] = !cur;
                ApplyCollapseState();
            }
            catch { }
        }

        private void ApplyCollapseState()
        {
            try
            {
                if (_grid == null) return;

                string currentHm = null;
                bool collapsed = false;

                foreach (DataGridViewRow row in _grid.Rows)
                {
                    string tag = Convert.ToString(row.Tag ?? "");
                    bool isHeader = (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith("HM:", StringComparison.OrdinalIgnoreCase));
                    if (isHeader)
                    {
                        currentHm = tag.Substring(3);
                        collapsed = false;
                        if (!string.IsNullOrWhiteSpace(currentHm))
                            _hmCollapsed.TryGetValue(currentHm, out collapsed);
                        try { row.Cells["Stt"].Value = collapsed ? "+" : "-"; } catch { }
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(currentHm) && collapsed)
                        row.Visible = false;
                    else
                        row.Visible = true;
                }

                UpdateSelectionInfo();
            }
            catch { }
        }

        private void ExportCsv()
        {
            try
            {
                using (var sfd = new SaveFileDialog())
                {
                    sfd.Filter = "CSV (*.csv)|*.csv";
                    sfd.Title = "Xuất danh sách";
                    sfd.FileName = "SBP.csv";
                    if (sfd.ShowDialog(this) != DialogResult.OK) return;

                    var sb = new StringBuilder();
                    sb.AppendLine("STT,HẠNG MỤC,KÝ HIỆU BẢN VẼ,TÊN BẢN VẼ,TÊN FILE PDF");

                    foreach (DataGridViewRow row in _grid.Rows)
                    {
                        // Bỏ dòng header hạng mục
                        string tag = Convert.ToString(row.Tag ?? "");
                        if (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith("HM:", StringComparison.OrdinalIgnoreCase)) continue;

                        var it = row.Tag as BlockItem;
                        if (it == null) continue;

                        // STT theo đúng thứ tự đang hiển thị trên grid
                        string sttView = "";
                        try { sttView = Convert.ToString(row.Cells["Stt"].Value ?? ""); } catch { sttView = ""; }

                        sb.AppendLine(string.Join(",", new string[]
                        {
                            Csv(sttView),
                            Csv(it.HangMuc),
                            Csv(it.KyHieu),
                            Csv(it.TenBanVe),
                            Csv(it.PdfName)
                        }));
                    }

                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show(this, "Đã xuất: " + sfd.FileName, "SBP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (System.Exception ex)
            {
                try { MessageBox.Show(this, "Lỗi xuất CSV: " + ex.Message, "SBP", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            }
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            s = s.Replace("\"", "\"\"");
            return "\"" + s + "\"";
        }

        private void LoadPlotUiLists()
        {
            try
            {
                using (_doc.LockDocument())
                {
                    var psv = PlotSettingsValidator.Current;

                    // Build a temp PlotSettings to query available plotters/media/styles (giống dialog Plot)
                    using (var ps = new PlotSettings(false))
                    {
                        // Fix plotter như ban đầu: DWG To PDF.pc3
                        try { psv.SetPlotConfigurationName(ps, "DWG To PDF.pc3", null); } catch { }
                        try { psv.RefreshLists(ps); } catch { }

                        // Paper size list
                        // Yêu cầu: khóa cứng đúng 3 khổ "ISO_full_bleed" dạng 594x841 / 420x594 / 297x420.
                        // Không lấy từ GetCanonicalMediaNameList để tránh driver trả về thêm bản đảo chiều.
                        try
                        {
                            _cbPaper.Items.Clear();
                            _cbPaper.Items.Add("A0");
                            _cbPaper.Items.Add("A1");
                            _cbPaper.Items.Add("A2");
                            _cbPaper.Items.Add("A3");
                            _cbPaper.SelectedIndex = 3;
                        }
                        catch { }

                        // Plot style sheet list (CTB/STB)
                        try
                        {
                            var styles = psv.GetPlotStyleSheetList();
                            if (styles != null && styles.Count > 0)
                            {
                                _cbStyle.Items.Clear();
                                _cbStyle.Items.Add("None");
                                foreach (var s in styles) if (!string.IsNullOrWhiteSpace(s)) _cbStyle.Items.Add(s);

                                int idxMono = -1;
                                for (int i = 0; i < _cbStyle.Items.Count; i++)
                                {
                                    var s = Convert.ToString(_cbStyle.Items[i]);
                                    if (!string.IsNullOrWhiteSpace(s) && s.IndexOf("monochrome", StringComparison.OrdinalIgnoreCase) >= 0)
                                    { idxMono = i; break; }
                                }
                                _cbStyle.SelectedIndex = idxMono >= 0 ? idxMono : 0;
                            }
                        }
                        catch { }
                    }

                    // (Đã bỏ chọn plotter)
                }
            }
            catch { }
        }

        private string DefaultOutDir()
        {
            try
            {
                string dwg = _doc.Database.Filename;
                if (!string.IsNullOrWhiteSpace(dwg))
                    return Path.Combine(Path.GetDirectoryName(dwg), "PDF");
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PDF");
        }

        private void RefreshList()
        {
            _items.Clear();
            _grid.Rows.Clear();

            string target = (_txtBlockName.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(target)) target = "KHUNG_MT";

            try
            {
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);

                    // Model space
                    TryCollectFromBtr(tr, (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead), "Model", "Model", target);

                    // All paper layouts
                    DBDictionary layoutDict = (DBDictionary)tr.GetObject(_doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry de in layoutDict)
                    {
                        Layout lo = (Layout)tr.GetObject(de.Value, OpenMode.ForRead);
                        if (lo.ModelType) continue;
                        var btr = (BlockTableRecord)tr.GetObject(lo.BlockTableRecordId, OpenMode.ForRead);
                        TryCollectFromBtr(tr, btr, "Layout:" + lo.LayoutName, lo.LayoutName, target);
                    }

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                try { _ed.WriteMessage("\n[LỖI Refresh] " + ex.Message); } catch { }
            }

            // Sort _items trước khi đổ vào grid:
            // 1) Hạng mục, 2) Ký hiệu, 3) Vị trí block (trái -> phải, trên -> dưới; gốc trên - trái)
            try
            {
                _items.Sort((a, b) =>
                {
                    string ah = a == null ? "" : (a.HangMuc ?? "");
                    string bh = b == null ? "" : (b.HangMuc ?? "");
                    int c = string.Compare(ah, bh, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;

                    string ak = a == null ? "" : (a.KyHieu ?? "");
                    string bk = b == null ? "" : (b.KyHieu ?? "");
                    c = string.Compare(ak, bk, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;

                    // 3) Vị trí: ưu tiên hàng trên trước (Y lớn hơn), rồi cột trái trước (X nhỏ hơn)
                    double ay = a == null ? 0 : a.PosY;
                    double by = b == null ? 0 : b.PosY;
                    if (Math.Abs(ay - by) > 1e-6) return (ay > by) ? -1 : 1;

                    double ax = a == null ? 0 : a.PosX;
                    double bx = b == null ? 0 : b.PosX;
                    if (Math.Abs(ax - bx) > 1e-6) return (ax < bx) ? -1 : 1;

                    // fallback ổn định
                    string at = a == null ? "" : (a.TenBanVe ?? "");
                    string bt = b == null ? "" : (b.TenBanVe ?? "");
                    return string.Compare(at, bt, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch { }

            // Hiển thị giống SSP: mỗi Hạng mục có 1 dòng header trước sheet đầu tiên của hạng mục đó.
            string lastHm = null;
            foreach (var it in _items)
            {
                string hm = it == null ? "" : (it.HangMuc ?? "");
                if (lastHm == null || !string.Equals(lastHm, hm, StringComparison.OrdinalIgnoreCase))
                {
                    // Header row: Sel + STT(+/-) + Hạng mục + Ký hiệu + Tên BV + Tên PDF
                    // (Tên hạng mục hiển thị ở cột HẠNG MỤC)
                    int hr = _grid.Rows.Add(false, "-", hm, "", "", "");
                    var hrow = _grid.Rows[hr];
                    hrow.Tag = "HM:" + hm;
                    try
                    {
                        hrow.DefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
                        hrow.DefaultCellStyle.Font = new System.Drawing.Font(_grid.Font, FontStyle.Bold);
                        hrow.Cells["Stt"].Value = "-";
                        hrow.Cells["Sel"].Value = false;
                    }
                    catch { }
                    lastHm = hm;
                }

                // Dòng sheet: cho phép edit ATT
                int r = _grid.Rows.Add(true, "", it.HangMuc, it.KyHieu, it.TenBanVe, it.PdfName);
                var row = _grid.Rows[r];
                row.Tag = it;
            }

            ApplyCollapseState();

            // Đánh lại STT theo thứ tự đang hiển thị sau sort (bỏ qua header HM)
            try
            {
                int n = 0;
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    string tag = Convert.ToString(row.Tag ?? "");
                    if (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith("HM:", StringComparison.OrdinalIgnoreCase)) continue;
                    n++;
                    row.Cells["Stt"].Value = n.ToString();
                }
            }
            catch { }

            UpdateSelectionInfo();
        }

        private void UpdateSelectionInfo()
        {
            try
            {
                if (_lblSelInfo == null) return;
                int total = 0;
                int selected = 0;
                if (_grid != null)
                {
                    foreach (DataGridViewRow row in _grid.Rows)
                    {
                        // bỏ dòng header HM
                        string tag = Convert.ToString(row.Tag ?? "");
                        if (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith("HM:", StringComparison.OrdinalIgnoreCase)) continue;

                        total++;
                        bool s = false;
                        try { s = Convert.ToBoolean(row.Cells["Sel"].Value ?? false); } catch { s = false; }
                        if (s) selected++;
                    }
                }
                _lblSelInfo.Text = "Đã chọn " + selected + "/" + total + " block khung tên.";
            }
            catch { }
        }

        private void TryCollectFromBtr(Transaction tr, BlockTableRecord btr, string spaceLabel, string layoutName, string targetName)
        {
            foreach (ObjectId id in btr)
            {
                BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null) continue;

                string name = ResolveBlockName(tr, br);
                // Lọc theo prefix: "KHUNG_MT" sẽ bắt cả "KHUNG_MT", "KHUNG_MT_A1", ...
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(targetName)
                    || !name.StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Nếu đang bật filter theo vùng quét: chỉ lấy các block nằm trong selection
                if (_windowFilterHandles != null && _windowFilterHandles.Count > 0)
                {
                    string hh = "";
                    try { hh = br.Handle.ToString(); } catch { hh = ""; }
                    if (string.IsNullOrWhiteSpace(hh) || !_windowFilterHandles.Contains(hh))
                        continue;
                }

                // window = polyline RECT khung in trong block (kể cả nested block).
                // KHÔNG fallback sang GeometricExtents để tránh in sai vùng.
                Extents2d win;
                bool rectLandscape;
                if (!TryGetOuterFrameWindow(tr, br, out win, out rectLandscape))
                {
                    try { _ed.WriteMessage("\n[SBP] Không tìm thấy RECT polyline khung in (kể cả nested). Bỏ qua. Handle=" + br.Handle); } catch { }
                    continue;
                }

                var map = ReadAttributesMap(tr, br);
                string kyHieu = GetAttr(map, "MT_KH");
                string hangMuc = GetAttr(map, "MT_TENHANGMUC");
                string tenBanVe = GetAttr(map, "MT_TENBANVE");

                string pdfBase = (kyHieu + "_" + tenBanVe).Trim('_').Trim();
                if (string.IsNullOrWhiteSpace(pdfBase)) pdfBase = "KHUNG_MT_" + br.Handle.ToString();

                _items.Add(new BlockItem
                {
                    Stt = _items.Count + 1,
                    LayoutName = layoutName ?? "",
                    KyHieu = kyHieu,
                    HangMuc = hangMuc,
                    TenBanVe = tenBanVe,
                    PdfName = SanitizeFileName(pdfBase) + ".pdf",
                    Handle = br.Handle.ToString(),

                    // Lấy vị trí block theo WCS (đủ để sort trong cùng layout)
                    PosX = br.Position.X,
                    PosY = br.Position.Y,

                    Window = win,
                    RectLandscape = rectLandscape
                });
            }
        }

        // Lấy extents của polyline RECT khung in trong block (có thể nằm trong nested block),
        // rồi transform theo BlockReference.
        private static bool TryGetOuterFrameWindow(Transaction tr, BlockReference br, out Extents2d win, out bool rectLandscape)
        {
            win = new Extents2d(new Point2d(0, 0), new Point2d(0, 0));
            rectLandscape = true;
            if (tr == null || br == null) return false;

            try
            {
                var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (def == null) return false;

                bool has = false;
                Extents3d best = new Extents3d(); // extents trong hệ def gốc (đã áp nested-transform)
                double bestArea = -1;
                Point3d[] bestPts = null; // 4 điểm RECT trong hệ def gốc (đã áp nested-transform)

                ScanRectRecursive(tr, def, Matrix3d.Identity, ref has, ref best, ref bestArea, ref bestPts);
                if (!has || bestPts == null || bestPts.Length != 4) return false;

                var m = br.BlockTransform;
                // Transform 4 điểm RECT thật ra world/paperspace theo BlockTransform
                Point3d[] wpts = new Point3d[4];
                for (int i = 0; i < 4; i++) wpts[i] = bestPts[i].TransformBy(m);

                // Detect landscape/portrait theo cạnh dài nhất (tránh sai khi RECT bị rotate trong nested block)
                double maxLen = -1;
                Vector3d longEdge = Vector3d.XAxis;
                for (int i = 0; i < 4; i++)
                {
                    var a = wpts[i];
                    var b = wpts[(i + 1) % 4];
                    var v = b - a;
                    double len = v.Length;
                    if (len > maxLen)
                    {
                        maxLen = len;
                        longEdge = v;
                    }
                }
                double ax = Math.Abs(longEdge.X);
                double ay = Math.Abs(longEdge.Y);
                rectLandscape = (ax >= ay);

                double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                foreach (var p in wpts)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }

                win = new Extents2d(new Point2d(minX, minY), new Point2d(maxX, maxY));
                return true;
            }
            catch { return false; }
        }

        // Duyệt đệ quy: tìm RECT polyline trong block definition và nested block.
        // acc: transform tích luỹ từ nested block về hệ của def gốc.
        private static void ScanRectRecursive(
            Transaction tr,
            BlockTableRecord def,
            Matrix3d acc,
            ref bool has,
            ref Extents3d best,
            ref double bestArea,
            ref Point3d[] bestPts)
        {
            foreach (ObjectId eid in def)
            {
                Entity ent = null;
                try { ent = tr.GetObject(eid, OpenMode.ForRead) as Entity; } catch { ent = null; }
                if (ent == null) continue;

                // 1) RECT polyline
                var pl = ent as Polyline;
                if (pl != null)
                {
                    if (!pl.Closed) continue;
                    if (pl.NumberOfVertices != 4) continue;

                    // Cạnh thẳng (bulge=0)
                    bool ok = true;
                    try
                    {
                        for (int i = 0; i < 4; i++)
                            if (Math.Abs(pl.GetBulgeAt(i)) > 1e-9) { ok = false; break; }
                    }
                    catch { ok = false; }
                    if (!ok) continue;

                    // vuông góc + song song
                    try
                    {
                        var p0 = pl.GetPoint2dAt(0);
                        var p1 = pl.GetPoint2dAt(1);
                        var p2 = pl.GetPoint2dAt(2);
                        var p3 = pl.GetPoint2dAt(3);

                        Vector2d v01 = p1 - p0;
                        Vector2d v12 = p2 - p1;
                        Vector2d v23 = p3 - p2;
                        Vector2d v30 = p0 - p3;

                        if (v01.Length < 1e-6 || v12.Length < 1e-6 || v23.Length < 1e-6 || v30.Length < 1e-6) continue;

                        double ortho1 = Math.Abs(v01.GetNormal().DotProduct(v12.GetNormal()));
                        double ortho2 = Math.Abs(v12.GetNormal().DotProduct(v23.GetNormal()));
                        if (ortho1 > 1e-2) continue;
                        if (ortho2 > 1e-2) continue;

                        double para1 = Math.Abs(v01.GetNormal().DotProduct(v23.GetNormal()));
                        double para2 = Math.Abs(v12.GetNormal().DotProduct(v30.GetNormal()));
                        if (para1 < 0.98) continue;
                        if (para2 < 0.98) continue;
                    }
                    catch { continue; }

                    // Tính extents đúng theo 4 đỉnh RECT (không dùng GeometricExtents bbox) để tránh bị "ăn" sang khung bên cạnh
                    // khi RECT nằm trong nested block và có rotation.
                    Extents3d ex;
                    Point3d[] vpts = null;
                    try
                    {
                        vpts = new Point3d[]
                        {
                            new Point3d(pl.GetPoint2dAt(0).X, pl.GetPoint2dAt(0).Y, 0),
                            new Point3d(pl.GetPoint2dAt(1).X, pl.GetPoint2dAt(1).Y, 0),
                            new Point3d(pl.GetPoint2dAt(2).X, pl.GetPoint2dAt(2).Y, 0),
                            new Point3d(pl.GetPoint2dAt(3).X, pl.GetPoint2dAt(3).Y, 0)
                        };

                        // apply nested transform
                        for (int i = 0; i < vpts.Length; i++) vpts[i] = vpts[i].TransformBy(acc);

                        double minX = vpts.Min(p => p.X);
                        double minY = vpts.Min(p => p.Y);
                        double maxX = vpts.Max(p => p.X);
                        double maxY = vpts.Max(p => p.Y);
                        ex = new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
                    }
                    catch { continue; }

                    double w = Math.Abs(ex.MaxPoint.X - ex.MinPoint.X);
                    double h = Math.Abs(ex.MaxPoint.Y - ex.MinPoint.Y);
                    if (w <= 1e-6 || h <= 1e-6) continue;
                    double area = w * h;

                    // Nếu chỉ có 1 rectang khung in thì lấy rectang có diện tích lớn nhất
                    if (!has || area > bestArea)
                    {
                        bestArea = area;
                        best = ex;
                        bestPts = vpts;
                        has = true;
                    }

                    continue;
                }

                // 2) nested block
                var br2 = ent as BlockReference;
                if (br2 != null)
                {
                    try
                    {
                        var def2 = (BlockTableRecord)tr.GetObject(br2.BlockTableRecord, OpenMode.ForRead);
                        if (def2 == null) continue;
                        // Thứ tự nhân matrix: tích luỹ transform theo chuỗi parent -> nested.
                        // Dùng acc * br2.BlockTransform để áp transform nested đúng theo TransformBy.
                        var acc2 = acc * br2.BlockTransform;
                        ScanRectRecursive(tr, def2, acc2, ref has, ref best, ref bestArea, ref bestPts);
                    }
                    catch { }
                }
            }
        }

        private static string ResolveBlockName(Transaction tr, BlockReference br)
        {
            try
            {
                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                return btr == null ? "" : (btr.Name ?? "");
            }
            catch { return ""; }
        }

        private static Dictionary<string, string> ReadAttributesMap(Transaction tr, BlockReference br)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    var ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                    if (ar == null) continue;
                    string tag = (ar.Tag ?? "").Trim();
                    string val = (ar.TextString ?? "").Trim();
                    if (tag.Length == 0) continue;
                    dict[tag] = val;
                }
            }
            catch { }
            return dict;
        }

        private static string GetAttr(Dictionary<string, string> dict, string key)
        {
            if (dict == null || string.IsNullOrWhiteSpace(key)) return "";
            string v;
            return dict.TryGetValue(key, out v) ? (v ?? "") : "";
        }

        private void WriteBackAttributes(BlockItem it)
        {
            if (it == null) return;
            try
            {
                using (_doc.LockDocument())
                {
                    var db = _doc.Database;
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        ObjectId id;
                        // Handle string (hex)
                        var h = new Handle(Convert.ToInt64(it.Handle, 16));
                        id = db.GetObjectId(false, h, 0);

                        var br = tr.GetObject(id, OpenMode.ForWrite) as BlockReference;
                        if (br == null) { tr.Commit(); return; }

                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            var ar = tr.GetObject(aid, OpenMode.ForWrite) as AttributeReference;
                            if (ar == null) continue;
                            string t = (ar.Tag ?? "").Trim();
                            if (t.Length == 0) continue;

                            if (string.Equals(t, "MT_KH", StringComparison.OrdinalIgnoreCase))
                                ar.TextString = it.KyHieu ?? "";
                            else if (string.Equals(t, "MT_TENBANVE", StringComparison.OrdinalIgnoreCase))
                                ar.TextString = it.TenBanVe ?? "";
                            else if (string.Equals(t, "MT_TENHANGMUC", StringComparison.OrdinalIgnoreCase))
                                ar.TextString = it.HangMuc ?? "";
                        }

                        tr.Commit();
                    }
                }
            }
            catch (System.Exception ex)
            {
                try { _ed.WriteMessage("\n[SBP] Không ghi được ATT: " + ex.Message); } catch { }
            }
        }

        private void PrintSelected()
        {
            // UI hiển thị A0/A1/A2/A3, nhưng khi plot phải dùng canonical media name ISO_full_bleed_...
            string paperKey = Convert.ToString(_cbPaper.SelectedItem ?? "A3");
            string paper = paperKey;
            try
            {
                if (_paperMediaMap != null && !string.IsNullOrWhiteSpace(paperKey))
                {
                    string canon;
                    if (_paperMediaMap.TryGetValue(paperKey, out canon) && !string.IsNullOrWhiteSpace(canon))
                        paper = canon;
                }
            }
            catch { }

            string styleSheet = Convert.ToString(_cbStyle == null ? "" : (_cbStyle.SelectedItem ?? ""));
            bool fit = true; // luôn fit

            string outDir = (_txtOutDir.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(outDir)) outDir = DefaultOutDir();
            Directory.CreateDirectory(outDir);

            // Lấy danh sách chọn
            var selected = new List<BlockItem>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                bool sel = false;
                try { sel = Convert.ToBoolean(row.Cells["Sel"].Value ?? false); } catch { sel = false; }
                if (!sel) continue;
                var it = row.Tag as BlockItem;
                if (it != null) selected.Add(it);
            }

            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Bạn chưa chọn dòng nào.", "Sheet Block Manager and Printer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int ok = 0, fail = 0;

            foreach (var it in selected)
            {
                string pdf = Path.Combine(outDir, SanitizeFileName(it.PdfName));
                try
                {
                    PlotWindowToPdf(_doc, it.LayoutName, it.Window, it.RectLandscape, pdf, paper, styleSheet, fit);
                    ok++;
                    try { _ed.WriteMessage("\n[OK] " + Path.GetFileName(pdf)); } catch { }
                }
                catch (System.Exception ex)
                {
                    fail++;
                    try { _ed.WriteMessage("\n[LỖI] " + it.Handle + ": " + ex.Message); } catch { }
                }
            }

            MessageBox.Show(this, "Hoàn thành.\nIn thành công: " + ok + "\nIn lỗi: " + fail, "Sheet Block Manager and Printer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Plot current layout with a Window area (WCS XY)
        private static void PlotWindowToPdf(Document doc, string layoutName, Extents2d win, bool rectLandscape, string pdfFile, string paperMedia, string styleSheet, bool fit)
        {
            // IMPORTANT: This is a best-effort plotting method. Media names can differ by PC3/printer config.
            // Default uses "DWG To PDF.pc3".

            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new InvalidOperationException("Plot is busy");

            // NOTE: thao tác Plot cần lock Document để tránh eInvalidInput do context thay đổi
            using (doc.LockDocument())
            {
                LayoutManager lm = LayoutManager.Current;
                // Chuyển đúng layout trước khi start Transaction (ổn định context)
                // Nếu không switch đúng layout thì PlotInfoValidator có thể báo eLayoutNotCurrent.
                try
                {
                    if (!string.IsNullOrWhiteSpace(layoutName))
                        lm.CurrentLayout = layoutName; // gồm cả "Model"
                }
                catch { }

                Database db = doc.Database;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId layoutId;
                    try
                    {
                        layoutId = (!string.IsNullOrWhiteSpace(layoutName) && string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase))
                            ? lm.GetLayoutId("Model")
                            : lm.GetLayoutId(lm.CurrentLayout);
                    }
                    catch
                    {
                        layoutId = lm.GetLayoutId(lm.CurrentLayout);
                    }
                    Layout lo = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);

                    using (PlotSettings ps = new PlotSettings(lo.ModelType))
                    {
                        ps.CopyFrom(lo);
                        PlotSettingsValidator psv = PlotSettingsValidator.Current;

                        // Plot style table (CTB/STB) giống dialog Plot
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(styleSheet) && !string.Equals(styleSheet, "None", StringComparison.OrdinalIgnoreCase))
                                psv.SetCurrentStyleSheet(ps, styleSheet);
                        }
                        catch { }

                        // Fix plotter như ban đầu: DWG To PDF.pc3
                        try { psv.SetPlotConfigurationName(ps, "DWG To PDF.pc3", null); } catch { }
                        psv.RefreshLists(ps);

                        // Paper size: dùng canonical media name từ combobox (giống CAD dialog)
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(paperMedia))
                            {
                                psv.SetCanonicalMediaName(ps, paperMedia);
                                psv.RefreshLists(ps);
                            }
                        }
                        catch { /* nếu không set được media thì giữ nguyên media của layout */ }

                        // Không ép PaperUnits/Rotation: để giống đúng hành vi Ctrl+P (Window theo layout hiện tại)

                        psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                        // Window = đúng extents lớn nhất của block.
                        // Chỉ nới cực nhỏ khi extents quá mỏng (tránh eInvalidInput), để vùng in không bị rộng hơn.
                        var minPt = win.MinPoint;
                        var maxPt = win.MaxPoint;
                        bool isModel = (!string.IsNullOrWhiteSpace(layoutName) && string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase));

                        // Model cần TileMode=true để plot đúng context; Layout cần TileMode=false
                        try { db.TileMode = isModel; } catch { }

                        double w = Math.Abs(maxPt.X - minPt.X);
                        double h = Math.Abs(maxPt.Y - minPt.Y);

                        // Fix chiều bản vẽ: tự xoay theo hướng giấy (paperMedia) và hướng RECT khung in
                        try
                        {
                            bool paperLandscape = true;

                            if (!string.IsNullOrWhiteSpace(paperMedia))
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(
                                    paperMedia,
                                    @"\((\s*\d+(?:\.\d+)?)\s*x\s*(\d+(?:\.\d+)?)\s*MM\)",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                                );

                                if (m.Success)
                                {
                                    double pw = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                                    double ph = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                                    paperLandscape = (pw >= ph);
                                }
                            }

                            // Nếu khung và giấy khác hướng (ví dụ chọn media dạng HxW) -> xoay 90 độ để fit
                            var rot = PlotRotation.Degrees000;
                            if (rectLandscape != paperLandscape)
                            {
                                rot = PlotRotation.Degrees090;
                            }
                            psv.SetPlotRotation(ps, rot);

                        }
                        catch { }
                        double eps = isModel ? 1e-4 : 1e-3; // paper(mm) dùng eps lớn hơn chút
                        double dx = (w < eps) ? (isModel ? 1.0 : 0.1) : 0.0;
                        double dy = (h < eps) ? (isModel ? 1.0 : 0.1) : 0.0;
                        var win2 = new Extents2d(new Point2d(minPt.X - dx, minPt.Y - dy), new Point2d(maxPt.X + dx, maxPt.Y + dy));

                        // Các khung đặt sát nhau: entity nằm đúng trên biên (vd x=420/840/...) có thể bị Window "ăn" sang khung bên cạnh.
                        // Shrink nhẹ window để loại bỏ entity nằm đúng biên.
                        try
                        {
                            double shrink = 0.05; // mm (Layout) / đơn vị bản vẽ (Model). Nếu vẫn dính, tăng lên 0.1
                            if ((win2.MaxPoint.X - win2.MinPoint.X) > shrink * 2 && (win2.MaxPoint.Y - win2.MinPoint.Y) > shrink * 2)
                            {
                                win2 = new Extents2d(
                                    new Point2d(win2.MinPoint.X + shrink, win2.MinPoint.Y + shrink),
                                    new Point2d(win2.MaxPoint.X - shrink, win2.MaxPoint.Y - shrink)
                                );
                            }
                        }
                        catch { }

                        // (Đã bỏ ghi log SBP_WIN_LOG.txt)

                        psv.SetPlotWindowArea(ps, win2);

                        // Bạn in tay có tick "Center the plot" -> center
                        try { psv.SetPlotCentered(ps, true); } catch { }
                        psv.SetUseStandardScale(ps, true);
                        psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);

                        PlotInfo pi = new PlotInfo();
                        pi.Layout = layoutId;
                        pi.OverrideSettings = ps;

                        PlotInfoValidator piv = new PlotInfoValidator();
                        piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                        try
                        {
                            piv.Validate(pi);
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception vex)
                        {
                            // Log thêm thông tin để debug eInvalidInput
                            try
                            {
                                doc.Editor.WriteMessage(
                                    "\n[SBP-VALIDATE-ERR] layout=" + (lm.CurrentLayout ?? "")
                                    + " status=" + vex.ErrorStatus
                                    + " win=[" + win2.MinPoint.X + "," + win2.MinPoint.Y + "]-[" + win2.MaxPoint.X + "," + win2.MaxPoint.Y + "]"
                                );
                            }
                            catch { }

                            throw;
                        }

                        using (PlotEngine pe = PlotFactory.CreatePublishEngine())
                        {
                            using (PlotProgressDialog ppd = new PlotProgressDialog(false, 1, true))
                            {
                                ppd.OnBeginPlot();
                                ppd.IsVisible = false;
                                try
                                {
                                    pe.BeginPlot(ppd, null);

                                    pe.BeginDocument(pi, doc.Name, null, 1, true, pdfFile);
                                }
                                catch (Autodesk.AutoCAD.Runtime.Exception pex)
                                {
                                    try { doc.Editor.WriteMessage("\n[SBP-PLOT-ERR] status=" + pex.ErrorStatus + " file=" + pdfFile); } catch { }
                                    throw;
                                }
                                PlotPageInfo ppi = new PlotPageInfo();
                                pe.BeginPage(ppi, pi, true, null);
                                pe.BeginGenerateGraphics(null);
                                pe.EndGenerateGraphics(null);
                                pe.EndPage(null);
                                pe.EndDocument(null);
                                pe.EndPlot(null);
                                ppd.OnEndPlot();
                            }
                        }
                    }
                    tr.Commit();
                }
            }
        }

        private void FilterByWindow()
        {
            try
            {
                // Ẩn form để quay lại màn hình CAD và quét vùng
                try { this.Hide(); } catch { }

                var opts = new PromptSelectionOptions();
                opts.MessageForAdding = "\nQuét vùng để lọc khung tên (Enter để xong): ";
                opts.AllowDuplicates = false;

                PromptSelectionResult res = null;
                try { res = _ed.GetSelection(opts); } catch { res = null; }

                if (res == null || res.Status != PromptStatus.OK || res.Value == null)
                {
                    // Cancel/ESC -> bỏ filter
                    _windowFilterHandles = null;
                }
                else
                {
                    var hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    using (_doc.LockDocument())
                    using (var tr = _doc.Database.TransactionManager.StartTransaction())
                    {
                        foreach (SelectedObject so in res.Value)
                        {
                            if (so == null) continue;
                            var br = tr.GetObject(so.ObjectId, OpenMode.ForRead) as BlockReference;
                            if (br == null) continue;
                            try { hs.Add(br.Handle.ToString()); } catch { }
                        }
                        tr.Commit();
                    }

                    _windowFilterHandles = hs.Count > 0 ? hs : null;
                }
            }
            finally
            {
                try { this.Show(); this.Activate(); } catch { }
                try { RefreshList(); } catch { }
            }
        }

        private static string IsoMediaName(string paper)
        {
            // Canonical names for DWG To PDF on many installs (not guaranteed).
            switch ((paper ?? "").Trim().ToUpperInvariant())
            {
                case "A0": return "ISO_A0_(1189.00_x_841.00_MM)";
                case "A1": return "ISO_A1_(841.00_x_594.00_MM)";
                case "A2": return "ISO_A2_(594.00_x_420.00_MM)";
                case "A3": return "ISO_A3_(420.00_x_297.00_MM)";
                default: return "";
            }
        }

        private static string SanitizeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "plot";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }
    }
}