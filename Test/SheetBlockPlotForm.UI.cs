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
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Exception = System.Exception; // tránh nhầm với Autodesk.AutoCAD.Runtime.Exception

namespace CADtools
{
    // File UI: chỉ chứa WinForms + binding. Logic AutoCAD/plot nằm ở SheetBlockPlotLogic.cs
    public class SheetBlockPlotForm : Form
    {
        private readonly Document _doc;
        private readonly Editor _ed;

        private TextBox _txtBlockName;
        private Button _btnPickBlock;
        private Button _btnRefresh;
        private Button _btnFilterWindow;
        private Button _btnBrowseOut;
        private TextBox _txtOutDir;
        private CheckBox _chkCombinePdf;
        private TextBox _txtCombinedPdfName;
        private ComboBox _cbPaper;
        private ComboBox _cbStyle;
        // Fit luôn bật -> bỏ checkbox khỏi UI
        private DataGridView _grid;
        private Button _btnPrint;
        private Button _btnClose;
        private Label _lblSelInfo;
        private Label _printStatus;
        private ProgressBar _printProgress;

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

        private sealed class BlockSortEntry
        {
            public SheetBlockPlotLogic.BlockItem Item;
            public int Index;
            public double Left;
            public double Top;
            public double Height;
        }

        public SheetBlockPlotForm(Document doc)
        {
            _doc = doc;
            _ed = doc.Editor;
            _logic = new SheetBlockPlotLogic(doc);

            Text = "Sheet Block Plotter - Build"
                + UpdateCommands.BuildTimeLocal.ToString("yyyyMMdd-HHmmss") + " ©KhoaND";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1240, 760);
            MinimumSize = new Size(940, 620);
            Font = new System.Drawing.Font("Segoe UI", 9.5f);
            BackColor = Color.FromArgb(246, 248, 250);

            int y = 10;
            Controls.Add(new Label { Left = 10, Top = y + 4, Width = 140, Height = 24, Text = "Block Khung Tên:", TextAlign = ContentAlignment.MiddleLeft });
            _txtBlockName = new TextBox { Left = 150, Top = y, Width = 150, Height = 28, Text = "KHUNG" };
            Controls.Add(_txtBlockName);

            _btnPickBlock = new Button { Left = 302, Top = y, Width = 150, Height = 28, Text = "Chọn khung" };
            _btnPickBlock.Click += (s, e) => PickBlockName();
            Controls.Add(_btnPickBlock);

            _btnFilterWindow = new Button { Left = 460, Top = y, Width = 150, Height = 28, Text = "Chọn vùng in" };
            _btnFilterWindow.Click += (s, e) => FilterByWindow();
            Controls.Add(_btnFilterWindow);

            _btnRefresh = new Button { Left = 558, Top = y, Width = 90, Height = 28, Text = "Refresh" };
            _btnRefresh.Click += (s, e) => RefreshList();
            Controls.Add(_btnRefresh);

            // Dời nhóm Paper/Nét in sang phải để không bị đè với nút "Lọc vùng"
            Controls.Add(new Label { Left = 745, Top = y + 4, Width = 75, Height = 24, Text = "Khổ giấy:", TextAlign = ContentAlignment.MiddleLeft });
            _cbPaper = new ComboBox { Left = 820, Top = y, Width = 100, Height = 28, DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(_cbPaper);

            Controls.Add(new Label { Left = 880, Top = y + 4, Width = 60, Height = 24, Text = "Nét in:", TextAlign = ContentAlignment.MiddleLeft });
            _cbStyle = new ComboBox { Left = 940, Top = y, Width = 290, Height = 28, DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(_cbStyle);

            // Fit luôn bật -> bỏ checkbox (không hiển thị)

            y += 38;
            Controls.Add(new Label { Left = 10, Top = y + 4, Width = 140, Height = 24, Text = "Output folder:", TextAlign = ContentAlignment.MiddleLeft });
            _txtOutDir = new TextBox { Left = 150, Top = y, Width = 670, Height = 28, Text = DefaultOutDir() };
            Controls.Add(_txtOutDir);
            _chkCombinePdf = new CheckBox
            {
                Text = "Gộp thành 1 PDF:",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _txtCombinedPdfName = new TextBox
            {
                Text = "SBP_Combined.pdf",
                Enabled = false
            };
            _chkCombinePdf.CheckedChanged += (s, e) => _txtCombinedPdfName.Enabled = _chkCombinePdf.Checked;
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
                    it.PdfName = BuildDefaultPdfName(it, ParseDisplayedStt(row));
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

            _printStatus = new Label { Text = "Sẵn sàng", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            _printProgress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = 0, Style = ProgressBarStyle.Continuous };

            _btnPrint = new Button { Left = ClientSize.Width - 240, Top = ClientSize.Height - 44, Width = 110, Height = 32, Text = "In PDF", Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            _btnPrint.Click += (s, e) => PrintSelected();
            Controls.Add(_btnPrint);

            _btnClose = new Button { Left = ClientSize.Width - 120, Top = ClientSize.Height - 44, Width = 110, Height = 32, Text = "Đóng", Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            _btnClose.Click += (s, e) => { try { this.Close(); } catch { } }; // modeless: DialogResult khong tu dong dong form
            Controls.Add(_btnClose);
            CancelButton = _btnClose;

            RefreshList();
            UpdateSelectionInfo();
            LoadPlotUiLists();
            ApplyResponsiveLayout();
        }

        private void ApplyResponsiveLayout()
        {
            Controls.Clear();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 4, 14, 0),
                ColumnCount = 1,
                RowCount = 4,
                BackColor = BackColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            var setup = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 3, Padding = new Padding(0, 2, 0, 0) };
            setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
            setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            setup.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            setup.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            setup.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            AddSetupLabel(setup, "Khung tên", 0, 0);
            Put(setup, _txtBlockName, 1, 0);
            PutButton(setup, _btnPickBlock, 2, 0, 108);
            PutButton(setup, _btnFilterWindow, 3, 0, 114);
            AddSetupLabel(setup, "Khổ giấy", 4, 0);
            Put(setup, _cbPaper, 5, 0);
            PutButton(setup, _btnRefresh, 6, 0, 78);
            AddSetupLabel(setup, "Thư mục PDF", 0, 1);
            Put(setup, _txtOutDir, 1, 1);
            var browseCell = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 1, 7, 1) };
            _btnBrowseOut.Dock = DockStyle.None;
            _btnBrowseOut.Width = 40;
            _btnBrowseOut.Height = 28;
            _btnBrowseOut.Left = 0;
            _btnBrowseOut.Top = 1;
            browseCell.Controls.Add(_btnBrowseOut);
            setup.Controls.Add(browseCell, 2, 1);
            AddSetupLabel(setup, "Nét in", 3, 1);
            setup.SetColumnSpan(_cbStyle, 3);
            Put(setup, _cbStyle, 4, 1);
            var combineRow = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 1, 7, 1) };
            _chkCombinePdf.Dock = DockStyle.None;
            _chkCombinePdf.Location = new Point(0, 5);
            combineRow.Controls.Add(_chkCombinePdf);
            _txtCombinedPdfName.Dock = DockStyle.None;
            _txtCombinedPdfName.Left = 190;
            _txtCombinedPdfName.Top = 3;
            _txtCombinedPdfName.Height = 26;
            _txtCombinedPdfName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            combineRow.Controls.Add(_txtCombinedPdfName);
            combineRow.Resize += (s, e) => _txtCombinedPdfName.Width = Math.Max(100, combineRow.ClientSize.Width - _txtCombinedPdfName.Left);
            setup.Controls.Add(combineRow, 0, 2);
            setup.SetColumnSpan(combineRow, 7);
            root.Controls.Add(setup, 0, 0);

            _grid.Dock = DockStyle.Fill;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.Single;
            _grid.GridColor = Color.FromArgb(224, 228, 232);
            _grid.ColumnHeadersHeight = 36;
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(232, 237, 241),
                ForeColor = Color.FromArgb(35, 46, 56),
                Font = new System.Drawing.Font(Font, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft
            };
            root.Controls.Add(_grid, 0, 1);

            var progressRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0, 2, 0, 2)
            };
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            progressRow.Controls.Add(_printStatus, 0, 0);
            progressRow.Controls.Add(_printProgress, 1, 0);
            root.Controls.Add(progressRow, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Padding = new Padding(0) };
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            PutButton(footer, _btnSelAll, 0, 0, 96);
            PutButton(footer, _btnSelNone, 1, 0, 96);
            PutButton(footer, _btnExport, 2, 0, 96);
            _lblSelInfo.Dock = DockStyle.Fill;
            _lblSelInfo.TextAlign = ContentAlignment.MiddleLeft;
            footer.Controls.Add(_lblSelInfo, 3, 0);
            PutButton(footer, _btnPrint, 4, 0, 96);
            PutButton(footer, _btnClose, 5, 0, 96);
            root.Controls.Add(footer, 0, 3);
            Controls.Add(root);
        }

        private static void Put(TableLayoutPanel panel, Control control, int column, int row)
        {
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 1, 7, 1);
            panel.Controls.Add(control, column, row);
        }

        private static void PutButton(TableLayoutPanel panel, Button button, int column, int row, int width)
        {
            button.Dock = DockStyle.None;
            button.Width = width;
            button.Height = 28;
            button.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            button.Margin = new Padding(0, 3, 0, 0);
            panel.Controls.Add(button, column, row);
        }

        private static void AddSetupLabel(TableLayoutPanel panel, string text, int column, int row)
        {
            panel.Controls.Add(new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 1, 7, 1)
            }, column, row);
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
                                    if (!string.IsNullOrWhiteSpace(s) && s.IndexOf("monochrome.ctb", StringComparison.OrdinalIgnoreCase) >= 0)
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

            // Sort theo góc trên bên trái của khung: trên xuống dưới, trái sang phải.
            try
            {
                var positioned = _items
                    .Select((item, index) => new BlockSortEntry
                    {
                        Item = item,
                        Index = index,
                        Left = item == null ? 0 : item.Window.MinPoint.X,
                        Top = item == null ? 0 : item.Window.MaxPoint.Y,
                        Height = item == null ? 0 : item.Window.MaxPoint.Y - item.Window.MinPoint.Y
                    })
                    .OrderByDescending(x => x.Top)
                    .ToList();

                double typicalHeight = positioned
                    .Where(x => x.Height > 1e-6)
                    .Select(x => x.Height)
                    .DefaultIfEmpty(1.0)
                    .OrderBy(x => x)
                    .ElementAt(positioned.Count == 0 ? 0 : positioned.Count / 2);
                double rowTolerance = Math.Max(1e-6, typicalHeight * 0.25);

                var rows = new List<List<BlockSortEntry>>();
                foreach (var entry in positioned)
                {
                    var row = rows.FirstOrDefault(r => Math.Abs(r[0].Top - entry.Top) <= rowTolerance);
                    if (row == null)
                    {
                        row = new List<BlockSortEntry>();
                        rows.Add(row);
                    }
                    row.Add(entry);
                }

                _items.Clear();
                foreach (var row in rows.OrderByDescending(r => r[0].Top))
                {
                    _items.AddRange(row
                    .OrderBy(x => x.Left)
                        .ThenBy(x => x.Index)
                    .Select(x => x.Item));
                }

                ApplyDefaultPdfNames();
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
                    var item = row.Tag as SheetBlockPlotLogic.BlockItem;
                    if (item != null) item.Stt = n;
                    row.Cells["Stt"].Value = n.ToString();
                }
            }
            catch { }

            UpdateSelectionInfo();
        }

        private void ApplyDefaultPdfNames()
        {
            var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item == null) continue;

                item.Stt = i + 1;
                string name = BuildDefaultPdfName(item, item.Stt);
                string baseName = Path.GetFileNameWithoutExtension(name);
                if (usedNames.TryGetValue(baseName, out int count))
                {
                    count++;
                    usedNames[baseName] = count;
                    name = SheetBlockPlotLogic.SanitizeFileName(baseName + "_" + count) + ".pdf";
                }
                else
                {
                    usedNames[baseName] = 1;
                }
                item.PdfName = name;
            }
        }

        private static string BuildDefaultPdfName(SheetBlockPlotLogic.BlockItem item, int stt)
        {
            if (item == null) return "";

            string baseName = (item.KyHieu ?? "").Trim();
            if (string.IsNullOrWhiteSpace(baseName))
            {
                string layoutName = (item.LayoutName ?? "").Trim();
                string location = string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)
                    ? "_Model"
                    : string.IsNullOrWhiteSpace(layoutName) ? "_Layout" : "_" + layoutName;
                baseName = stt.ToString() + location;
            }

            return SheetBlockPlotLogic.SanitizeFileName(baseName) + ".pdf";
        }

        private static int ParseDisplayedStt(DataGridViewRow row)
        {
            try
            {
                int stt;
                if (row != null && int.TryParse(Convert.ToString(row.Cells["Stt"].Value ?? ""), out stt))
                    return stt;
            }
            catch { }
            return 0;
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

            if (_chkCombinePdf.Checked)
            {
                string requestedName = (_txtCombinedPdfName.Text ?? "").Trim();
                string baseName = Path.GetFileNameWithoutExtension(requestedName);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "SBP_Combined";
                string combinedPdf = Path.Combine(outDir,
                    SheetBlockPlotLogic.SanitizeFileName(baseName) + ".pdf");
                string temporaryPdf = Path.Combine(outDir,
                    Path.GetFileNameWithoutExtension(combinedPdf) + "_" + Guid.NewGuid().ToString("N") + ".pdf");
                string temporaryDirectory = Path.Combine(Path.GetTempPath(), "CADtools_SBP_" + Guid.NewGuid().ToString("N"));
                _btnPrint.Enabled = false;
                _btnClose.Enabled = false;
                SetPrintProgress(0, "Chuẩn bị gộp " + selected.Count + " bản vẽ...");

                try
                {
                    Directory.CreateDirectory(temporaryDirectory);
                    var individualPdfs = new List<string>();
                    for (int index = 0; index < selected.Count; index++)
                    {
                        var item = selected[index];
                        string pagePdf = Path.Combine(temporaryDirectory, (index + 1).ToString("D4") + ".pdf");
                        SetPrintProgress(index * 90 / selected.Count,
                            "Đang in trang " + (index + 1) + "/" + selected.Count + ": " + item.PdfName);
                        _logic.PlotWindowToPdf(item.LayoutName, item.Window, item.RectLandscape,
                            pagePdf, paper, styleSheet, fit);
                        individualPdfs.Add(pagePdf);
                        SetPrintProgress((index + 1) * 90 / selected.Count,
                            "Đã in " + (index + 1) + "/" + selected.Count + " trang.");
                    }

                    SetPrintProgress(94, "Đang ghép " + selected.Count + " trang PDF...");
                    using (var mergedDocument = new PdfDocument())
                    {
                        foreach (string pagePdf in individualPdfs)
                        {
                            using (PdfDocument source = PdfReader.Open(pagePdf, PdfDocumentOpenMode.Import))
                            {
                                foreach (PdfPage page in source.Pages)
                                    mergedDocument.AddPage(page);
                            }
                        }
                        if (mergedDocument.PageCount == 0)
                            throw new InvalidDataException("Không có trang PDF hợp lệ để gộp.");
                        mergedDocument.Save(temporaryPdf);
                    }

                    if (File.Exists(combinedPdf)) File.Replace(temporaryPdf, combinedPdf, null);
                    else File.Move(temporaryPdf, combinedPdf);
                    SetPrintProgress(100, "Hoàn tất: " + Path.GetFileName(combinedPdf));
                    try { _ed.WriteMessage("\n[OK] PDF gộp " + Path.GetFileName(combinedPdf) + " - " + selected.Count + " trang"); } catch { }
                    MessageBox.Show(this,
                        "Đã gộp " + selected.Count + " bản vẽ vào một file PDF:\n" + combinedPdf,
                        "Sheet Block Manager and Printer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (System.Exception ex)
                {
                    SetPrintProgress(_printProgress.Value, "Lỗi khi in/gộp PDF: " + ex.Message);
                    try { if (File.Exists(temporaryPdf)) File.Delete(temporaryPdf); } catch { }
                    try { _ed.WriteMessage("\n[LỖI PDF gộp] " + ex.Message); } catch { }
                    MessageBox.Show(this, "Không thể tạo PDF gộp:\n" + ex.Message,
                        "Sheet Block Manager and Printer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    try { if (File.Exists(temporaryPdf)) File.Delete(temporaryPdf); } catch { }
                    try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true); } catch { }
                    _btnPrint.Enabled = true;
                    _btnClose.Enabled = true;
                }
                return;
            }

            int ok = 0, fail = 0;
            _btnPrint.Enabled = false;
            _btnClose.Enabled = false;
            SetPrintProgress(0, "Bắt đầu in " + selected.Count + " PDF...");
            try
            {
                for (int index = 0; index < selected.Count; index++)
                {
                    var item = selected[index];
                    string pdf = Path.Combine(outDir, SheetBlockPlotLogic.SanitizeFileName(item.PdfName));
                    SetPrintProgress(index * 100 / selected.Count,
                        "Đang in " + (index + 1) + "/" + selected.Count + ": " + item.PdfName);
                    try
                    {
                        _logic.PlotWindowToPdf(item.LayoutName, item.Window, item.RectLandscape, pdf, paper, styleSheet, fit);
                        ok++;
                        try { _ed.WriteMessage("\n[OK] " + Path.GetFileName(pdf)); } catch { }
                    }
                    catch (System.Exception ex)
                    {
                        fail++;
                        try { _ed.WriteMessage("\n[LỖI] " + item.Handle + ": " + ex.Message); } catch { }
                    }
                    SetPrintProgress((index + 1) * 100 / selected.Count,
                        "Đã xử lý " + (index + 1) + "/" + selected.Count + " | Thành công: " + ok + " | Lỗi: " + fail);
                }

                SetPrintProgress(100, "Hoàn tất | Thành công: " + ok + " | Lỗi: " + fail);
                MessageBox.Show(this, "Hoàn thành.\nIn thành công: " + ok + "\nIn lỗi: " + fail, "Sheet Block Manager and Printer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                _btnPrint.Enabled = true;
                _btnClose.Enabled = true;
            }
            return;
        }

        private void SetPrintProgress(int percent, string status)
        {
            if (_printProgress == null || _printStatus == null || IsDisposed) return;
            _printProgress.Value = Math.Max(_printProgress.Minimum, Math.Min(_printProgress.Maximum, percent));
            _printStatus.Text = status ?? "";
            _printStatus.Refresh();
            _printProgress.Refresh();
        }

        // Nút "Chọn khung": ẩn form, cho người dùng pick 1 block khung tên HOẶC External Reference (xref),
        // rồi điền tên vào ô "Block Khung Tên" và refresh danh sách.
        // KHÔNG dùng AddAllowedClass (tránh loại nhầm xref) — tự kiểm tra sau khi chọn.
        private void PickBlockName()
        {
            try
            {
                // Ẩn form để quay lại màn hình CAD và chọn đối tượng
                try { this.Hide(); } catch { }

                while (true)
                {
                    var peo = new PromptEntityOptions("\nChọn 1 block khung tên hoặc External Reference (xref): ");
                    peo.AllowNone = false;

                    var per = _ed.GetEntity(peo);
                    if (per.Status != PromptStatus.OK) return; // ESC / hủy

                    string name = null;
                    bool isBlockRef = false;

                    using (_doc.LockDocument())
                    using (var tr = _doc.Database.TransactionManager.StartTransaction())
                    {
                        // Cả block thường lẫn xref đều là BlockReference.
                        var br = tr.GetObject(per.ObjectId, OpenMode.ForRead) as BlockReference;
                        if (br != null)
                        {
                            isBlockRef = true;
                            var def = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;

                            // Lấy tên thật: block động lấy theo DynamicBlockTableRecord,
                            // còn lại lấy theo BlockTableRecord (đúng cho cả block thường & xref).
                            if (br.IsDynamicBlock)
                            {
                                var dbtr = tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                                if (dbtr != null) name = dbtr.Name;
                            }
                            else if (def != null)
                            {
                                name = def.Name;
                            }
                            else
                            {
                                name = br.Name;
                            }
                        }
                        tr.Commit();
                    }

                    if (!isBlockRef)
                    {
                        try { _ed.WriteMessage("\nĐối tượng vừa chọn không phải Block/Xref. Hãy chọn lại (ESC để hủy)."); } catch { }
                        continue; // cho chọn lại
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        // Nếu tên dạng "xref|block" thì lấy phần sau dấu |
                        int bar = name.LastIndexOf('|');
                        if (bar >= 0 && bar < name.Length - 1) name = name.Substring(bar + 1);

                        _txtBlockName.Text = name;

                        // Chọn mới -> bỏ filter vùng cũ để quét lại toàn bộ theo tên
                        _windowFilterHandles = null;
                    }
                    return;
                }
            }
            catch (System.Exception ex)
            {
                try { _ed.WriteMessage("\n[LỖI] Chọn block: " + ex.Message); } catch { }
            }
            finally
            {
                try { this.Show(); this.Activate(); } catch { }
                try { RefreshList(); } catch { }
            }
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