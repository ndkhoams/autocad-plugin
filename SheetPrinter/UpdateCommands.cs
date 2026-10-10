using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(CADtools.UpdateCommands))]

namespace CADtools
{
    public class UpdateCommands
    {
        private const string BuildTimestamp = "20261010-083512";
        private const string LatestSourceUrl = "https://raw.githubusercontent.com/ndkhoams/autocad-plugin/main/SheetPrinter/UpdateCommands.cs";
        private const string DownloadUrl = "https://raw.githubusercontent.com/ndkhoams/autocad-plugin/main/AutoCad_SSP.iso";
        private static readonly HttpClient Http = CreateHttpClient();

        [CommandMethod("SPUPDATE", CommandFlags.Session)]
        public void CheckForUpdates()
        {
            var form = new UpdateForm();
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(form);
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sheet Printer-SPUPDATE");
            return client;
        }

        internal static DateTime BuildTimeLocal
        {
            get { return DateTime.ParseExact(BuildTimestamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture); }
        }

        private static async Task<DateTime> GetLatestBuildTimeAsync()
        {
            string source = await Http.GetStringAsync(LatestSourceUrl).ConfigureAwait(false);
            Match match = Regex.Match(source, @"private const string BuildTimestamp = ""(?<timestamp>\d{8}-\d{6})"";");
            if (!match.Success)
                throw new InvalidDataException("Không tìm thấy timestamp build hợp lệ trong UpdateCommands.cs trên GitHub.");

            return DateTime.ParseExact(match.Groups["timestamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        private sealed class UpdateForm : Form
        {
            private readonly Label _status;
            private readonly Label _currentVersion;
            private readonly Label _latestVersion;
            private readonly ProgressBar _progress;
            private readonly Button _check;
            private readonly Button _download;
            private bool _updateAvailable;

            public UpdateForm()
            {
                DateTime buildTime = BuildTimeLocal;
                Text = "Sheet Printer - " + buildTime.ToString("yyyyMMdd-HHmmss");
                ClientSize = new System.Drawing.Size(1120, 670);
                MinimumSize = new System.Drawing.Size(900, 560);
                Font = new System.Drawing.Font("Segoe UI", 10F);
                StartPosition = FormStartPosition.CenterScreen;

                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(24, 18, 24, 18),
                    ColumnCount = 1,
                    RowCount = 6
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                var heading = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "SHEET PRINTER - UPDATES",
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                    Font = new System.Drawing.Font(Font.FontFamily, 16F, System.Drawing.FontStyle.Bold)
                };

                var versionGroup = new GroupBox
                {
                    Dock = DockStyle.Fill,
                    Text = "Phiên bản",
                    Padding = new Padding(14, 12, 14, 10)
                };
                var versionLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 2
                };
                versionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
                versionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                versionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                versionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                _currentVersion = MakeVersionValue(BuildTimeLocal.ToString("yyyyMMdd-HHmmss"));
                _latestVersion = MakeVersionValue("Chưa kiểm tra");
                versionLayout.Controls.Add(MakeVersionLabel("Bản dựng hiện tại:"), 0, 0);
                versionLayout.Controls.Add(_currentVersion, 1, 0);
                versionLayout.Controls.Add(MakeVersionLabel("Bản dựng mới nhất:"), 0, 1);
                versionLayout.Controls.Add(_latestVersion, 1, 1);
                versionGroup.Controls.Add(versionLayout);

                _status = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "Chưa kiểm tra phiên bản mới.",
                    TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                    Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold)
                };
                _progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous };
                var actions = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    Padding = new Padding(0, 4, 0, 0)
                };
                _check = new Button { Width = 190, Height = 36, Text = "Kiểm tra phiên bản" };
                _check.Click += CheckLatestVersion;
                _download = new Button { Width = 190, Height = 36, Text = "Tải file cập nhật", Enabled = false };
                _download.Click += DownloadUpdate;
                var close = new Button
                {
                    Width = 110,
                    Height = 36,
                    Text = "Đóng",
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                close.Click += (sender, args) => Close();
                actions.Controls.Add(_check);
                actions.Controls.Add(_download);

                var actionsRow = new Panel { Dock = DockStyle.Fill };
                actionsRow.Controls.Add(actions);
                close.Left = 950;
                close.Top = 4;
                actionsRow.Controls.Add(close);
                actionsRow.Resize += (sender, args) => close.Left = actionsRow.ClientSize.Width - close.Width;

                var note = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 34,
                    Padding = new Padding(4, 8, 0, 0),
                    Text = "File ISO sẽ được lưu tại vị trí bạn chọn. Tải file không tự cài đặt hoặc thay thế plugin đang chạy.",
                    ForeColor = System.Drawing.SystemColors.GrayText
                };

                layout.Controls.Add(heading, 0, 0);
                layout.Controls.Add(versionGroup, 0, 1);
                layout.Controls.Add(_status, 0, 2);
                layout.Controls.Add(_progress, 0, 3);
                layout.Controls.Add(actionsRow, 0, 4);
                layout.Controls.Add(note, 0, 5);
                Controls.Add(layout);
                CancelButton = close;
            }

            private async void CheckLatestVersion(object sender, EventArgs e)
            {
                _check.Enabled = false;
                _download.Enabled = false;
                _updateAvailable = false;
                _status.Text = "Đang kiểm tra phiên bản mới...";
                _progress.Style = ProgressBarStyle.Marquee;
                try
                {
                    DateTime latestTime = await GetLatestBuildTimeAsync();
                    if (IsDisposed) return;

                    _latestVersion.Text = latestTime.ToString("yyyyMMdd-HHmmss");
                    _updateAvailable = latestTime > BuildTimeLocal;
                    _status.Text = _updateAvailable
                        ? "Đã tìm thấy phiên bản mới. Bạn có thể tải file cập nhật."
                        : "Bạn đang dùng phiên bản mới nhất.";
                }
                catch (System.Exception ex)
                {
                    if (!IsDisposed)
                    {
                        _latestVersion.Text = "Không kiểm tra được";
                        _status.Text = "Không thể kiểm tra phiên bản mới: " + ex.Message;
                    }
                }
                finally
                {
                    if (!IsDisposed)
                    {
                        _progress.Style = ProgressBarStyle.Continuous;
                        _progress.Value = 0;
                        _check.Enabled = true;
                        _download.Enabled = _updateAvailable;
                    }
                }
            }

            private async void DownloadUpdate(object sender, EventArgs e)
            {
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Title = "Lưu file cập nhật";
                    dialog.Filter = "ISO image (*.iso)|*.iso";
                    dialog.DefaultExt = "iso";
                    dialog.AddExtension = true;
                    dialog.FileName = "AutoCad_SSP.iso";
                    dialog.OverwritePrompt = true;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;

                    _check.Enabled = false;
                    _download.Enabled = false;
                    _progress.Value = 0;
                    _progress.Style = ProgressBarStyle.Marquee;
                    _status.Text = "Đang tải file ISO...";
                    string temporaryPath = Path.Combine(Path.GetTempPath(), "CADtools_" + Guid.NewGuid().ToString("N") + ".iso");
                    try
                    {
                        await DownloadIsoAsync(temporaryPath);
                        File.Copy(temporaryPath, dialog.FileName, true);
                        _progress.Style = ProgressBarStyle.Continuous;
                        _progress.Value = 100;
                        _status.Text = "Tải hoàn tất. File đã lưu tại: " + dialog.FileName;
                    }
                    catch (System.Exception ex)
                    {
                        _progress.Style = ProgressBarStyle.Continuous;
                        _progress.Value = 0;
                        _status.Text = "Không thể tải file cập nhật: " + ex.Message;
                    }
                    finally
                    {
                        try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                        _check.Enabled = true;
                        _download.Enabled = _updateAvailable;
                    }
                }
            }

            private async Task DownloadIsoAsync(string path)
            {
                using (var response = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long? totalBytes = response.Content.Headers.ContentLength;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                        _progress.Style = ProgressBarStyle.Continuous;

                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        long received = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, count);
                            received += count;
                            if (totalBytes.HasValue && totalBytes.Value > 0)
                                _progress.Value = Math.Min(100, (int)(received * 100.0 / totalBytes.Value));
                        }
                    }
                    }
            }

            private static Label MakeVersionLabel(string text)
            {
                return new Label
                {
                    Dock = DockStyle.Fill,
                    Text = text,
                    TextAlign = System.Drawing.ContentAlignment.MiddleLeft
                };
            }

            private static Label MakeVersionValue(string text)
            {
                return new Label
                {
                    Dock = DockStyle.Fill,
                    Text = text,
                    TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                    Font = new System.Drawing.Font("Consolas", 10F)
                };
            }
        }
    }
}