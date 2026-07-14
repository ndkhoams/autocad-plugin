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
    // File UI: chỉ chứa WinForms + binding. Logic AutoCAD/plot nằm ở SheetBlockPlotLogic.cs
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

        // Trạng thái thu gọn theo Hạng mục (UI state)
        private readonly Dictionary<string, bool> _hmCollapsed =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Filter theo vùng quét: lưu handle các block nằm trong selection (null = không lọc)
        private HashSet<string> _windowFilterHandles = null;

        // Map hiển thị khổ giấy (A0/A1/A2/A3) -> canonical media name (ISO_full_bleed_...)
        private readonly Dictionary<string, string> _paperMediaMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "A0", "ISO_full_bleed_A0_(1189.00_x_841.00_MM)" },
                { "A1", "ISO_full_bleed_A1_(841.00_x_594.00_MM)" },
                { "A2", "ISO_full_bleed_A2_(594.00_x_420.00_MM)" },
                { "A3", "ISO_full_bleed_A3_(420.00_x_297.00_MM)" },
            };

        // Logic instance
        private readonly SheetBlockPlotLogic _logic;

        // UI giữ list items để bind
        private readonly List<SheetBlockPlotLogic.BlockItem> _items =
            new List<SheetBlockPlotLogic.BlockItem>();

        public SheetBlockPlotForm(Document doc)
        {
            _doc = doc;
            _ed = doc.Editor;
            _logic = new SheetBlockPlotLogic(doc);

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

                    var it = row.Tag as SheetBlockPlotLogic.BlockItem;
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
                    it.PdfName = SheetBlockPlotLogic.SanitizeFileName(pdfBase) + ".pdf";
                    try { row.Cells["PdfName"].Value = it.PdfName; } catch { }

                    // Ghi ngược vào block (logic)
                    _logic.WriteBackAttributes(it);
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
                    var it = row.Tag as SheetBlockPlotLogic.BlockItem;
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

                        var it = row.Tag as SheetBlockPlotLogic.BlockItem;
                        if (it == null) continue;

                        // STT theo đúng thứ tự đang hiển thị trên grid
                        string sttView = "";
                        try { sttView = Convert.ToString(row.Cells["Stt"].Value ?? ""); } catch { sttView = ""; }

                        sb.AppendLine(string.Join(",", new string[]
                        {
                            SheetBlockPlotLogic.Csv(sttView),
                            SheetBlockPlotLogic.Csv(it.HangMuc),
                            SheetBlockPlotLogic.Csv(it.KyHieu),
                            SheetBlockPlotLogic.Csv(it.TenBanVe),
                            SheetBlockPlotLogic.Csv(it.PdfName)
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

                        // Paper size list: chỉ hiện A0/A1/A2/A3
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
                _items.AddRange(_logic.CollectBlocks(target, _windowFilterHandles));
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
            var selected = new List<SheetBlockPlotLogic.BlockItem>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                // Bỏ dòng header hạng mục
                string tag = Convert.ToString(row.Tag ?? "");
                if (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith("HM:", StringComparison.OrdinalIgnoreCase)) continue;

                bool sel = false;
                try { sel = Convert.ToBoolean(row.Cells["Sel"].Value ?? false); } catch { sel = false; }
                if (!sel) continue;

                var it = row.Tag as SheetBlockPlotLogic.BlockItem;
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
                string pdf = Path.Combine(outDir, SheetBlockPlotLogic.SanitizeFileName(it.PdfName));
                try
                {
                    _logic.PlotWindowToPdf(it.LayoutName, it.Window, it.RectLandscape, pdf, paper, styleSheet, fit);
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

        private void FilterByWindow()
        {
            try
            {
                // Ẩn form để quay lại màn hình CAD và quét vùng
                try { this.Hide(); } catch { }

                _windowFilterHandles = _logic.PromptSelectBlockHandles(_ed);
            }
            finally
            {
                try { this.Show(); this.Activate(); } catch { }
                try { RefreshList(); } catch { }
            }
        }
    }
}