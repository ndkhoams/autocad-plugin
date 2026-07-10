using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace BatchPlotPdf
{
    // Bang luoi cho phep sua truc tiep cac truong Sheet Set roi ghi nguoc vao .dst.
    public class SheetSetEditorForm : Form
    {
        private readonly List<SheetInfo> _sheets;
        private readonly List<string> _customKeys;
        // Chi giu lai cac cot custom can thiet (bo Client, Project, Total sheet...).
        private static readonly string[] _whitelist = { "CONT", "SHT" };
        private DataGridView dgv;

        public bool DoSave { get; private set; }

        public SheetSetEditorForm(List<SheetInfo> sheets)
        {
            _sheets = sheets ?? new List<SheetInfo>();
            _customKeys = _sheets.SelectMany(s => s.Custom.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(k => _whitelist.Any(w => string.Equals(w, k, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(k => Array.FindIndex(_whitelist, w => string.Equals(w, k, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Text = "Quản lý Sheet Set";
            ClientSize = new Size(1240, 640);
            MinimumSize = new Size(820, 460);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.75f);

            dgv = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                BorderStyle = BorderStyle.None
            };
            dgv.RowTemplate.Height = 28;
            dgv.ColumnHeadersHeight = 34;
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            dgv.DefaultCellStyle.Padding = new Padding(3, 2, 3, 2);

            AddCol("STT", "STT", 46);
            dgv.Columns["STT"].ReadOnly = true;
            dgv.Columns["STT"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            dgv.Columns["STT"].DefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
            AddCol("Number", "Số sheet", 200);
            AddCol("Title", "Tiêu đề", 380);
            AddCol("Rev", "Revision", 90);
            AddCol("RevDate", "Ngày rev", 110);
            AddCol("Purpose", "Issue purpose", 170);
            foreach (var k in _customKeys) AddCol("cust::" + k, k, 110);

            for (int idx = 0; idx < _sheets.Count; idx++)
            {
                var s = _sheets[idx];
                int i = dgv.Rows.Add();
                var row = dgv.Rows[i];
                row.Tag = s;
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

            var lblHint = new Label
            {
                Dock = DockStyle.Top,
                Height = 54,
                Padding = new Padding(12, 10, 12, 6),
                ForeColor = Color.DimGray,
                Text = "Sửa trực tiếp trong bảng rồi bấm nút Lưu vào Sheet Set. "
                     + "Tất cả cột — kể cả Revision / Ngày rev / Issue purpose — đều ghi thẳng vào Sheet Set."
            };

            var pnl = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(12, 12, 12, 12) };
            var btnSave = new Button { Text = "Lưu vào Sheet Set", Dock = DockStyle.Right, Width = 170, Height = 34 };
            var spacer = new Panel { Dock = DockStyle.Right, Width = 12 };
            var btnCancel = new Button { Text = "Đóng", Dock = DockStyle.Right, Width = 100, Height = 34, DialogResult = DialogResult.Cancel };
            var btnExport = new Button { Text = "Xuất Excel", Dock = DockStyle.Left, Width = 130, Height = 34 };
            btnSave.Click += (s, e) => { CommitToModel(); DoSave = true; DialogResult = DialogResult.OK; };
            btnExport.Click += (s, e) => ExportToExcel();
            pnl.Controls.Add(btnSave);
            pnl.Controls.Add(spacer);
            pnl.Controls.Add(btnCancel);
            pnl.Controls.Add(btnExport);

            // Them theo thu tu: Fill truoc (duoi cung z-order), roi Top/Bottom -> layout dung.
            Controls.Add(dgv);
            Controls.Add(lblHint);
            Controls.Add(pnl);

            AcceptButton = btnSave;
            CancelButton = btnCancel;
        }

        // Xuat bang hien tai (ke ca chinh sua chua luu) ra file CSV UTF-8 co BOM -> Excel mo truc tiep.
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

                    // Dau phan cach theo Windows (Excel VN thuong la ';') de khong bi dinh cot.
                    string sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
                    if (string.IsNullOrEmpty(sep)) sep = ",";

                    var sb = new StringBuilder();
                    var headers = new List<string>();
                    foreach (DataGridViewColumn c in dgv.Columns)
                        if (c.Visible) headers.Add(Csv(c.HeaderText, sep));
                    sb.AppendLine(string.Join(sep, headers.ToArray()));

                    foreach (DataGridViewRow row in dgv.Rows)
                    {
                        if (row.IsNewRow) continue;
                        var cells = new List<string>();
                        foreach (DataGridViewColumn c in dgv.Columns)
                        {
                            if (!c.Visible) continue;
                            var v = row.Cells[c.Index].Value;
                            cells.Add(Csv(v == null ? "" : v.ToString(), sep));
                        }
                        sb.AppendLine(string.Join(sep, cells.ToArray()));
                    }

                    // UTF-8 CO BOM de Excel mo dung tieng Viet.
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));

                    if (MessageBox.Show("Đã xuất: " + dlg.FileName + Environment.NewLine + "Mở file ngay?",
                        "Xuất Excel", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không xuất được: " + ex.Message, "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Boc 1 gia tri CSV: co dau phan cach / xuong dong / dau ngoac kep thi boc trong dau ngoac kep.
        private static string Csv(string s, string sep)
        {
            if (s == null) s = "";
            const string q = "\"";
            bool needQuote = s.IndexOf(sep, StringComparison.Ordinal) >= 0
                || s.Contains(q) || s.Contains("\n") || s.Contains("\r");
            s = s.Replace(q, q + q);
            return needQuote ? q + s + q : s;
        }

        private void AddCol(string name, string header, int width)
        {
            dgv.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        private void CommitToModel()
        {
            dgv.EndEdit();
            foreach (DataGridViewRow row in dgv.Rows)
            {
                var s = row.Tag as SheetInfo;
                if (s == null) continue;
                s.Number = Str(row, "Number");
                s.Title = Str(row, "Title");
                s.Revision = Str(row, "Rev");
                s.RevisionDate = Str(row, "RevDate");
                s.IssuePurpose = Str(row, "Purpose");
                foreach (var k in _customKeys)
                    s.Custom[k] = Str(row, "cust::" + k);
            }
        }

        private static string Str(DataGridViewRow row, string col)
        {
            var v = row.Cells[col].Value;
            return v == null ? "" : v.ToString();
        }
    }
}