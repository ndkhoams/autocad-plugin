using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Publishing;
using Autodesk.AutoCAD.Runtime;
using CADtools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#if CAD_ACSM_R23
using AcSm = ACSMCOMPONENTS23Lib;
#elif CAD_ACSM_R25
using AcSm = ACSMCOMPONENTS25Lib;
#else
using AcSm = ACSMCOMPONENTS24Lib;
#endif
using Exception = System.Exception; // tranh nhap nhang voi Autodesk.AutoCAD.Runtime.Exception
// NOTE: tránh bị nhầm List của WPF (System.Windows.Documents.List). Chỉ alias cho danh sách SheetInfo.
using GList = System.Collections.Generic.List<CADtools.SheetInfo>;

[assembly: CommandClass(typeof(CADtools.CadToolsSheetSetCommand))]

namespace CADtools
{
    public static class SsmNaming
    {
        public static string Resolve(string template, SheetInfo s, bool mergedMode)
        {
            if (string.IsNullOrEmpty(template)) return "";
            return ResolveTokens(template, s, mergedMode);
        }

        private static string ResolveTokens(string template, SheetInfo s, bool mergedMode)
        {
            var sb = new StringBuilder(template.Length + 32);
            int i = 0;
            while (i < template.Length)
            {
                if (i + 1 < template.Length && template[i] == '$' && template[i + 1] == '(')
                {
                    int end = template.IndexOf(')', i + 2);
                    if (end > 0)
                    {
                        string key = template.Substring(i + 2, end - i - 2);
                        sb.Append(TokenValue(key, s, mergedMode));
                        i = end + 1;
                        continue;
                    }
                }
                sb.Append(template[i++]);
            }
            return sb.ToString().Trim();
        }

        private static string TokenValue(string key, SheetInfo s, bool mergedMode)
        {
            if (s == null) return "";
            if (string.Equals(key, "SheetSetName", StringComparison.OrdinalIgnoreCase)) return s.SheetSetName ?? "";
            if (mergedMode) return "";
            if (string.Equals(key, "SheetNumber", StringComparison.OrdinalIgnoreCase)) return s.Number ?? "";
            if (string.Equals(key, "SheetTitle", StringComparison.OrdinalIgnoreCase)) return s.Title ?? "";
            if (string.Equals(key, "SheetDesc", StringComparison.OrdinalIgnoreCase)) return s.Desc ?? "";
            if (string.Equals(key, "LayoutName", StringComparison.OrdinalIgnoreCase)) return s.LayoutName ?? "";
            if (string.Equals(key, "DwgName", StringComparison.OrdinalIgnoreCase)) return string.IsNullOrEmpty(s.DwgPath) ? "" : Path.GetFileNameWithoutExtension(s.DwgPath);
            if (string.Equals(key, "Revision", StringComparison.OrdinalIgnoreCase)) return s.Revision ?? "";
            if (string.Equals(key, "RevisionDate", StringComparison.OrdinalIgnoreCase)) return s.RevisionDate ?? "";
            if (string.Equals(key, "IssuePurpose", StringComparison.OrdinalIgnoreCase)) return s.IssuePurpose ?? "";
            string custom;
            return s.Custom != null && s.Custom.TryGetValue(key, out custom) ? custom ?? "" : "";
        }

        public static string SanitizeFile(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.IsNullOrEmpty(s) ? s : "";
            return SheetBlockPlotLogic.SanitizeFileName(s);
        }

        public static string EnsurePdf(string s)
        {

            if (string.IsNullOrEmpty(s)) return s;
            return s.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? s : s + ".pdf";
        }
    }

    public class CadToolsSheetSetCommand
    {

        [CommandMethod("SSP", CommandFlags.Session)]
        public void BatchPdfSsm()
        {

            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            if (!LicenseManager.Ensure(ed)) return; // Kiểm tra bản quyền
            GList sheets = new GList();
            try
            {
            // 1) Ưu tiên sheetset đang được chọn trong Sheet Set Manager.
            // Nếu SSM không hiện đường dẫn thì mới dùng các database COM đang mở.
            string dstPath = TryGetDstPathFromSsmUi();
            try
            {
                if (!string.IsNullOrWhiteSpace(dstPath))
                {
                    sheets = SheetSetReader.ReadFromDst(dstPath);
                }
                else
                {
                    sheets = SheetSetReader.ReadOpenSheetSets();
                    dstPath = SelectSheetSetIfNeeded(sheets);
                    if (!string.IsNullOrWhiteSpace(dstPath))
                    {
                        GList loadedSheets = SheetSetReader.ReadFromDst(dstPath);
                        SheetSetReader.Release(sheets);
                        sheets = loadedSheets;
                    }
                }
            }
            catch (Exception ex)
            {
                SheetSetReader.Release(sheets);
                sheets = new GList();
                ed.WriteMessage("\nKhông đọc được Sheet Set hiện hành: " + ex.Message);
            }

            string defDir = !string.IsNullOrEmpty(doc.Database.Filename)
            ? Path.Combine(Path.GetDirectoryName(doc.Database.Filename), "PDF")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PDF");
            if (string.IsNullOrWhiteSpace(dstPath)) dstPath = TryGetCurrentDstPath(sheets);

            // dstPath được hiển thị trên form và dùng làm thư mục mặc định.

            // 2) Loop: mo form -> neu chon DST khac thi reload sheets va mo lai form
            while (true)
            {

                string template, outDir;
                bool merged;
                bool printWithOptions;
                string paperMedia, plotStyle;
                PlotNamingForm.SsmAction action;
                GList allSheets = sheets; // giu ban day du de luu nguoc .dst
                GList selected;
                GList deletedSheets = new GList();

                using (var form = new PlotNamingForm(sheets, defDir, dstPath))
                {

                    if (AcadApp.ShowModalDialog(form) != DialogResult.OK) return;

                    if (form.DstChanged)
                    {

                        dstPath = form.DstPath;
                        // Update default output folder suggestion based on the newly selected DST
                        try
                        {
                            defDir = Path.Combine(Path.GetDirectoryName(dstPath), "PDF");
                        }
                        catch { }
                        try
                        {
                            GList loadedSheets = SheetSetReader.ReadFromDst(dstPath);
                            SheetSetReader.Release(sheets);
                            sheets = loadedSheets;
                        }
                        catch (Exception ex)
                        {

                            ed.WriteMessage("\nKhông đọc được DST: " + ex.Message);
                            return;
                        }
                        if (sheets == null || sheets.Count == 0)
                        {

                            ed.WriteMessage("\nDST không có sheet nào.");
                            return;
                        }
                        continue; // open form again with new sheets
                    }

                    action = form.Action;
                    template = form.Template;
                    outDir = form.OutputDir;
                    merged = form.Merged;
                    printWithOptions = action == PlotNamingForm.SsmAction.PrintWithOptions;
                    paperMedia = form.PaperMedia;
                    plotStyle = form.PlotStyle;
                    selected = form.SelectedSheets;
                    deletedSheets.AddRange(form.DeletedSheets);
                }

                // Bam "Lưu Sheet Set" -> ghi thay doi nguoc vao .dst, khong in.
                if (action == PlotNamingForm.SsmAction.Save)
                {

                    SaveResult sr;
                    try { sr = SheetSetWriter.Save(allSheets, deletedSheets, ed); }
                    catch (Exception ex) { ed.WriteMessage("\nLỗi ghi Sheet Set: " + ex.Message); return; }
                    finally { SheetSetReader.Release(deletedSheets); }
                    ed.WriteMessage("\n{0} {1} sheet. Revision ghi được: {2}, không ghi được: {3}.",
                    sr.CommitSucceeded ? "Đã commit" : "Đã xử lý nhưng chưa commit",
                    sr.SheetsSaved, sr.RevisionOk, sr.RevisionFail);
                    if (sr.RevisionFail > 0)
                        ed.WriteMessage("\nRevision/Issue purpose không ghi được qua COM (bản AutoCAD này không lộ setter) — sửa trực tiếp trong hộp thoại Sheet Properties của SSM.");
                    foreach (var w in sr.Warnings) ed.WriteMessage("\n- " + w);
                    return;
                }

                // Nguoc lai: bam "In PDF" -> chi in cac sheet dang tich.
                GList printSheets = selected;
                if (printSheets == null || printSheets.Count == 0) { ed.WriteMessage("\nBạn chưa chọn sheet nào để in."); return; }
                if (string.IsNullOrWhiteSpace(outDir)) outDir = defDir;
                if (string.IsNullOrWhiteSpace(outDir))
                    outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PDF");
                try { Directory.CreateDirectory(outDir); }
                catch (Exception ex)
                {
                    ed.WriteMessage("\nKhông tạo được thư mục PDF '" + outDir + "': " + ex.Message);
                    return;
                }

                if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                { ed.WriteMessage("\nĐang có tiến trình in khác, thử lại sau."); return; }

                if (printWithOptions)
                {
                    string summary = PlotOptionalSheets(doc, printSheets, merged, template, outDir, paperMedia, plotStyle);
                    try { ed.WriteMessage("\n" + summary); } catch { }
                    return;
                }

                int ok = 0;
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // In tung sheet bang Publisher (DSD): KHONG dung PlotEngine cho database side-load.
                if (merged)
                {

                    var all = new DsdEntryCollection();
                    var staleSheets = new List<string>();
                    using (var locator = new LayoutLocator())
                    foreach (var s in printSheets)
                    {

                        if (s == null || string.IsNullOrWhiteSpace(s.DwgPath) || !File.Exists(s.DwgPath))
                        { ed.WriteMessage("\nBỏ qua (không tìm thấy DWG): " + (s == null ? "(sheet rỗng)" : s.Title)); continue; }

                        if (string.IsNullOrWhiteSpace(s.LayoutName))
                        { ed.WriteMessage("\nBỏ qua (sheet không có Layout): " + s.Title); continue; }

                        // Kiem tra layout co that trong DWG khong (uu tien handle, roi theo ten).
                        // DST cu co the luu ten layout da doi ten/xoa -> Publisher se loai khoi job.
                        string liveLayout = ResolveLiveLayoutName(locator, s);
                        if (liveLayout == null)
                        {
                            staleSheets.Add(s.Title + " [DWG: " + s.DwgPath + " | Layout: '" + s.LayoutName + "']");
                            continue;
                        }

                        // Giữ nguyên thứ tự Sheet Set; Publisher tự nạp DWG từ từng DSD entry.
                        all.Add(new DsdEntry { DwgName = s.DwgPath, Layout = liveLayout, Title = s.Title, Nps = "" });
                    }
                    if (staleSheets.Count > 0)
                    {
                        ed.WriteMessage("\n[CẢNH BÁO] Bỏ qua {0} sheet vì không tìm thấy layout trong DWG (tên trong sheet set đã cũ hoặc file lỗi):", staleSheets.Count);
                        foreach (var t in staleSheets) { try { ed.WriteMessage("\n  - " + t); } catch { } }
                        ed.WriteMessage("\n(Hãy mở Sheet Set Manager để link lại hoặc xóa các sheet này.)");
                    }
                    if (all.Count == 0) { ed.WriteMessage("\nKhông có sheet hợp lệ để in."); return; }

                    string mName = SsmNaming.SanitizeFile(SsmNaming.Resolve(template, printSheets.Count > 0 ? printSheets[0] : null, true));
                    if (string.IsNullOrWhiteSpace(mName)) mName = "MergedSheets";
                    string mFile = Path.Combine(outDir, SsmNaming.EnsurePdf(mName));

                    if (PublishToPdf(all, mFile, outDir, SheetType.MultiPdf, ed))
                        ed.WriteMessage("\nĐã xuất PDF gộp {0} sheet -> {1}", all.Count, mFile);
                    return;
                }

                using (var locator = new LayoutLocator())
                foreach (var s in printSheets)
                {
                    if (string.IsNullOrEmpty(s.DwgPath) || !File.Exists(s.DwgPath))
                    { ed.WriteMessage("\nBỏ qua (không tìm thấy DWG): " + s.Title); continue; }

                    string liveLayout = ResolveLiveLayoutName(locator, s);
                    if (liveLayout == null)
                    { ed.WriteMessage("\n[BỎ QUA] " + s.Title + ": không tìm thấy layout '" + s.LayoutName + "' trong DWG (tên trong sheet set đã cũ)."); continue; }

                    string name = SsmNaming.SanitizeFile(SsmNaming.Resolve(template, s, false));
                    if (string.IsNullOrWhiteSpace(name)) name = s.LayoutName;
                    string baseName = name; int n = 2;
                    while (!used.Add(name)) name = baseName + " (" + (n++) + ")";
                    string file = Path.Combine(outDir, SsmNaming.EnsurePdf(name));

                    var one = new DsdEntryCollection();
                    one.Add(new DsdEntry { DwgName = s.DwgPath, Layout = liveLayout, Title = s.Title, Nps = "" });

                    if (PublishToPdf(one, file, outDir, SheetType.MultiPdf, ed))
                    {
                        ok++;
                        ed.WriteMessage("\n[OK] " + Path.GetFileName(file));
                    }
                    else
                    {
                        ed.WriteMessage("\n[LỖI] " + s.Title);
                    }
                }
                ed.WriteMessage("\nHoàn tất: {0}/{1} sheet -> {2}", ok, printSheets.Count, outDir);
                return;
            }
            }
            finally
            {
                SheetSetReader.Release(sheets);
            }
        }



        private static string PlotOptionalSheets(
            Document originalDocument,
            GList sheets,
            bool merged,
            string template,
            string outputDirectory,
            string paperMedia,
            string plotStyle)
        {
            var openedDocuments = new List<Document>();
            var pagePdfs = new List<string>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string temporaryDirectory = merged
                ? Path.Combine(Path.GetTempPath(), "CADtools_SSP_" + Guid.NewGuid().ToString("N"))
                : null;
            object previousStandardsCheck = null;
            bool standardsCheckCaptured = false;
            Form progressForm = null;
            Label progressLabel = null;
            ProgressBar progressBar = null;
            string diagnosticLogPath = EnablePlotDiagnostics
                ? Path.Combine(outputDirectory, "_ssm_plot_diagnostics.log")
                : null;

            try
            {
                if (EnablePlotDiagnostics)
                {
                    try { File.WriteAllText(diagnosticLogPath, ""); } catch { }
                }

                try
                {
                    previousStandardsCheck = AcadApp.GetSystemVariable("STANDARDSCHECK");
                    standardsCheckCaptured = true;
                    AcadApp.SetSystemVariable("STANDARDSCHECK", 0);
                }
                catch { }

                if (merged) Directory.CreateDirectory(temporaryDirectory);

                progressForm = new Form
                {
                    Text = "SSP - In tùy chọn",
                    Width = 520,
                    Height = 135,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterScreen,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = false
                };
                progressLabel = new Label
                {
                    Left = 16,
                    Top = 14,
                    Width = 480,
                    Height = 24,
                    AutoEllipsis = true,
                    Text = "Đang chuẩn bị in..."
                };
                progressBar = new ProgressBar
                {
                    Left = 16,
                    Top = 48,
                    Width = 480,
                    Height = 22,
                    Minimum = 0,
                    Maximum = 100
                };
                progressForm.Controls.Add(progressLabel);
                progressForm.Controls.Add(progressBar);
                AcadApp.ShowModelessDialog(progressForm);

                // Tiet kiem RAM: tim sheet CUOI CUNG dung moi DWG de dong file ngay khi xong,
                // thay vi mo tat ca roi dong mot luc o cuoi.
                var lastUseIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int k = 0; k < sheets.Count; k++)
                {
                    SheetInfo sk = sheets[k];
                    if (sk == null || string.IsNullOrWhiteSpace(sk.DwgPath) || !File.Exists(sk.DwgPath)) continue;
                    if (string.IsNullOrWhiteSpace(sk.LayoutName)) continue;
                    lastUseIndex[NormalizeDwgKey(sk.DwgPath)] = k;
                }

                int successCount = 0;
                int failureCount = 0;
                for (int i = 0; i < sheets.Count; i++)
                {
                    SheetInfo sheet = sheets[i];
                    if (sheet == null || string.IsNullOrWhiteSpace(sheet.DwgPath) || !File.Exists(sheet.DwgPath))
                    {
                        System.Diagnostics.Trace.WriteLine("[SSP-OPTIONAL] Bỏ qua (không tìm thấy DWG): " + (sheet == null ? "(sheet rỗng)" : sheet.Title));
                        failureCount++;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(sheet.LayoutName))
                    {
                        System.Diagnostics.Trace.WriteLine("[SSP-OPTIONAL] Bỏ qua (sheet không có Layout): " + sheet.Title);
                        failureCount++;
                        continue;
                    }

                    string pdfPath;
                    if (merged)
                    {
                        pdfPath = Path.Combine(temporaryDirectory, (i + 1).ToString("D4") + ".pdf");
                    }
                    else
                    {
                        string name = SsmNaming.SanitizeFile(SsmNaming.Resolve(template, sheet, false));
                        if (string.IsNullOrWhiteSpace(name)) name = sheet.LayoutName;
                        string baseName = name;
                        int duplicate = 2;
                        while (!usedNames.Add(name)) name = baseName + " (" + (duplicate++) + ")";
                        pdfPath = Path.Combine(outputDirectory, SsmNaming.EnsurePdf(name));
                    }

                    Document sheetDocument = null;
                    bool weOpenedThis = false;
                    try
                    {
                        UpdateOptionalPlotProgress(progressLabel, progressBar,
                            i * 100 / Math.Max(1, sheets.Count),
                            "Đang in " + (i + 1) + "/" + sheets.Count + ": " + sheet.Title);

                        sheetDocument = FindOpenDocument(sheet.DwgPath);
                        if (sheetDocument == null)
                        {
                            sheetDocument = AcadApp.DocumentManager.Open(sheet.DwgPath, false);
                            openedDocuments.Add(sheetDocument);
                            weOpenedThis = true;
                        }

                        AcadApp.DocumentManager.MdiActiveDocument = sheetDocument;
                        string sheetIdentifier = (sheet.Number ?? "") + " | " + (sheet.Title ?? "");
                        new SheetBlockPlotLogic(sheetDocument).PlotLayoutToPdf(
                            sheet.LayoutName, pdfPath, paperMedia, plotStyle, sheetIdentifier, diagnosticLogPath);
                        successCount++;
                        if (merged) pagePdfs.Add(pdfPath);
                        UpdateOptionalPlotProgress(progressLabel, progressBar,
                            (i + 1) * 100 / Math.Max(1, sheets.Count),
                            "Đã in " + (i + 1) + "/" + sheets.Count + " | Thành công: " + successCount + " | Lỗi: " + failureCount);
                        System.Diagnostics.Trace.WriteLine("[SSP-OPTIONAL][OK] " + sheet.Title + " -> " + pdfPath);
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        System.Diagnostics.Trace.WriteLine("[SSP-OPTIONAL][LỖI] " + sheet.Title + ": " + ex);
                        UpdateOptionalPlotProgress(progressLabel, progressBar,
                            (i + 1) * 100 / Math.Max(1, sheets.Count),
                            "Lỗi " + sheet.Title + ": " + ex.Message);
                        try { if (File.Exists(pdfPath)) File.Delete(pdfPath); } catch { }
                    }
                    finally
                    {
                        // Dong DWG ngay khi da in xong sheet cuoi cung cua no de giam RAM.
                        // Chi dong file do plugin tu mo; khong dong file user dang mo san.
                        // DWG co nhieu layout van giu mo cho den khi in xong layout cuoi.
                        try
                        {
                            if (weOpenedThis && sheetDocument != null)
                            {
                                int lastIdx;
                                if (lastUseIndex.TryGetValue(NormalizeDwgKey(sheet.DwgPath), out lastIdx) && lastIdx == i)
                                {
                                    if (openedDocuments.Remove(sheetDocument))
                                    {
                                        try { sheetDocument.CloseAndDiscard(); } catch { }
                                        System.Diagnostics.Trace.WriteLine("[SSP-OPTIONAL] Đã đóng DWG sau sheet cuối: " + sheet.DwgPath);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                if (merged && pagePdfs.Count > 0)
                {
                    UpdateOptionalPlotProgress(progressLabel, progressBar, 95, "Đang ghép " + pagePdfs.Count + " PDF...");
                    string mergedName = SsmNaming.SanitizeFile(SsmNaming.Resolve(
                        template, sheets.Count > 0 ? sheets[0] : null, true));
                    if (string.IsNullOrWhiteSpace(mergedName)) mergedName = "MergedSheets";
                    string mergedPdf = Path.Combine(outputDirectory, SsmNaming.EnsurePdf(mergedName));
                    string temporaryPdf = Path.Combine(outputDirectory,
                        Path.GetFileNameWithoutExtension(mergedPdf) + "_" + Guid.NewGuid().ToString("N") + ".pdf");

                    try
                    {
                        using (var mergedDocument = new PdfDocument())
                        {
                            foreach (string pagePdf in pagePdfs)
                            {
                                using (PdfDocument source = PdfReader.Open(pagePdf, PdfDocumentOpenMode.Import))
                                {
                                    foreach (PdfPage page in source.Pages) mergedDocument.AddPage(page);
                                }
                            }
                            if (mergedDocument.PageCount == 0)
                                throw new InvalidDataException("Không có trang PDF hợp lệ để gộp.");
                            mergedDocument.Save(temporaryPdf);
                        }

                        if (File.Exists(mergedPdf)) File.Replace(temporaryPdf, mergedPdf, null);
                        else File.Move(temporaryPdf, mergedPdf);
                        UpdateOptionalPlotProgress(progressLabel, progressBar, 100, "Hoàn tất: " + Path.GetFileName(mergedPdf));
                        System.Diagnostics.Trace.WriteLine("[SSP-OPTIONAL] Đã xuất PDF gộp " + successCount + "/" + sheets.Count + " sheet -> " + mergedPdf);
                    }
                    finally
                    {
                        try { if (File.Exists(temporaryPdf)) File.Delete(temporaryPdf); } catch { }
                    }
                }

                string resultSummary = "Hoàn tất in tùy chọn: " + successCount + " thành công, " + failureCount
                    + " lỗi -> " + outputDirectory
                    + (EnablePlotDiagnostics ? " | Chẩn đoán: " + diagnosticLogPath : "");
                System.Diagnostics.Trace.WriteLine("[SSP-OPTIONAL] " + resultSummary);
                return resultSummary;
            }
            finally
            {
                try { if (progressForm != null && !progressForm.IsDisposed) progressForm.Close(); } catch { }

                try
                {
                    if (originalDocument != null)
                        AcadApp.DocumentManager.MdiActiveDocument = originalDocument;
                }
                catch { }

                for (int i = openedDocuments.Count - 1; i >= 0; i--)
                {
                    try { openedDocuments[i].CloseAndDiscard(); } catch { }
                }

                if (standardsCheckCaptured)
                {
                    try { AcadApp.SetSystemVariable("STANDARDSCHECK", previousStandardsCheck); } catch { }
                }

                try { if (!string.IsNullOrWhiteSpace(temporaryDirectory) && Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true); } catch { }
            }
        }

        private static void UpdateOptionalPlotProgress(Label label, ProgressBar progressBar, int percent, string status)
        {
            try
            {
                if (label == null || progressBar == null || label.IsDisposed || progressBar.IsDisposed) return;
                progressBar.Value = Math.Max(progressBar.Minimum, Math.Min(progressBar.Maximum, percent));
                label.Text = status ?? "";
                label.Refresh();
                progressBar.Refresh();
                System.Windows.Forms.Application.DoEvents();
            }
            catch { }
        }

        private static string NormalizeDwgKey(string dwgPath)
        {
            try { return Path.GetFullPath(dwgPath); }
            catch { return dwgPath ?? ""; }
        }

        private static Document FindOpenDocument(string dwgPath)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(dwgPath); }
            catch { fullPath = dwgPath; }

            foreach (Document document in AcadApp.DocumentManager)
            {
                try
                {
                    string openPath = document.Database.Filename;
                    if (string.IsNullOrWhiteSpace(openPath)) continue;
                    if (string.Equals(Path.GetFullPath(openPath), fullPath, StringComparison.OrdinalIgnoreCase))
                        return document;
                }
                catch { }
            }
            return null;
        }

        // Auto-detect DST currently shown in Sheet Set Manager palette (AutoCAD 2023) via UI Automation.
        // Best-effort: finds a visible text containing an absolute *.dst path.
        private static string TryGetDstPathFromSsmUi()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("acad"))
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        var acad = AutomationElement.FromHandle(p.MainWindowHandle);
                        if (acad == null) continue;

                        var texts = acad.FindAll(TreeScope.Descendants,
                         new OrCondition(
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));

                        foreach (AutomationElement t in texts)
                        {
                            try
                            {
                                string s = t.Current.Name;
                                if (string.IsNullOrWhiteSpace(s) && t.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
                                {
                                    s = (valuePattern as ValuePattern)?.Current.Value;
                                }
                                if (string.IsNullOrWhiteSpace(s)) continue;
                                Match match = Regex.Match(s,
                                    @"(?i)(?:[A-Z]:\\|\\\\[^\\/\s]+\\[^\\/\s]+\\)[^<>:""|?*\r\n]*?\.dst");
                                if (match.Success && File.Exists(match.Value))
                                    return match.Value;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        private static string TryGetCurrentDstPath(GList sheets)
        {

            try
            {

                if (sheets == null || sheets.Count == 0) return "";
                var si = sheets[0] as SheetInfo;
                var db = si?.DbCom as AcSm.IAcSmDatabase;
                if (db == null) return "";
                return db.GetFileName() ?? "";
            }
            catch { return ""; }
        }

        private static string SelectSheetSetIfNeeded(GList sheets)
        {
            var choices = new List<SheetSetChoice>();
            foreach (var group in (sheets ?? new GList()).GroupBy(s =>
                GetDatabaseFileName(s),
                StringComparer.OrdinalIgnoreCase))
            {
                string path = group.Key;
                if (string.IsNullOrWhiteSpace(path)) continue;
                SheetInfo first = group.FirstOrDefault(s => s != null);
                choices.Add(new SheetSetChoice
                {
                    Path = path,
                    Name = first == null || string.IsNullOrWhiteSpace(first.SheetSetName)
                        ? Path.GetFileNameWithoutExtension(path)
                        : first.SheetSetName,
                    SheetCount = group.Count()
                });
            }

            if (choices.Count <= 1) return choices.Count == 1 ? choices[0].Path : "";
            using (var form = new SheetSetSelectionForm(choices))
                return AcadApp.ShowModalDialog(form) == DialogResult.OK ? form.SelectedPath : "";
        }

        private static string GetDatabaseFileName(SheetInfo sheet)
        {
            try
            {
                var db = sheet == null ? null : sheet.DbCom as AcSm.IAcSmDatabase;
                return db == null ? "" : db.GetFileName() ?? "";
            }
            catch { return ""; }
        }

        // Publish 1 hoac nhieu DsdEntry ra PDF. BACKGROUNDPLOT=0 (dong bo) + FILEDIA=0 + ForceNoPrompt.
        // Resolve tên layout HIỆN TẠI trong DWG (ưu tiên handle đã lưu, rồi theo tên).
        // Trả về null nếu layout không tồn tại -> sheet trỏ sai (DST cũ), nên bỏ qua
        // thay vì để Publisher loại cả job gộp.
        private static string ResolveLiveLayoutName(LayoutLocator locator, SheetInfo s)
        {
            if (locator == null || s == null) return null;
            try
            {
                string liveName, liveHandle;
                if (locator.Resolve(s.DwgPath, s.LayoutHandle, s.LayoutName, out liveName, out liveHandle)
                    && !string.IsNullOrWhiteSpace(liveName))
                    return liveName;
            }
            catch { }
            return null;
        }

        // Tam thoi tat ghi file chan doan (log/DSD) ra thu muc output cung PDF.
        private static bool EnablePublishDiagnostics = false;
        // Tam thoi tat ghi file _ssm_plot_diagnostics.log khi in tuy chon.
        private static bool EnablePlotDiagnostics = false;

        private static bool PublishToPdf(DsdEntryCollection entries, string destPdf, string outDir, SheetType type, Editor ed)
        {

            if (entries == null || entries.Count == 0) return false;

            short bp = (short)AcadApp.GetSystemVariable("BACKGROUNDPLOT");
            short filedia = (short)AcadApp.GetSystemVariable("FILEDIA");
            AcadApp.SetSystemVariable("BACKGROUNDPLOT", 0);
            AcadApp.SetSystemVariable("FILEDIA", 0); // TAT hop thoai "Specify PDF File"
            string dsdFile = Path.Combine(outDir, "_ssm_batch.dsd");
            try
            {

                DsdData dsd = new DsdData
                {

                    SheetType = type,
                    DestinationName = destPdf,
                    ProjectPath = outDir,
                    NoOfCopies = 1,
                    IsHomogeneous = false
                };
                EnsureUniqueDsdTitles(entries);
                dsd.SetDsdEntryCollection(entries);
                dsd.WriteDsd(dsdFile);

                var enc = Encoding.Default;
                int sheetsAfterWrite = CountDsdSheetSections(dsdFile);
                ForceNoPrompt(dsdFile, enc);
                int sheetsAfterForce = CountDsdSheetSections(dsdFile);
                dsd.ReadDsd(dsdFile);
                int sheetsAfterRead = CountDsdSheetsAfterRead(dsd, outDir);

                if (EnablePublishDiagnostics)
                {
                    try
                    {
                        File.AppendAllText(Path.Combine(outDir, "_ssm_publish_diagnostics.log"),
                            string.Format("[{0:yyyy-MM-dd HH:mm:ss}] dest={1} inMemory={2} afterWrite={3} afterForceNoPrompt={4} afterReadDsd={5}{6}",
                                DateTime.Now, destPdf, entries.Count, sheetsAfterWrite, sheetsAfterForce, sheetsAfterRead, Environment.NewLine));
                    }
                    catch { }
                }

                if (EnablePublishDiagnostics)
                {
                    try
                    {
                        string keptDsd = Path.Combine(outDir, "_ssm_batch_kept.dsd");
                        File.Copy(dsdFile, keptDsd, true);
                        var fiKept = new FileInfo(keptDsd);
                        File.AppendAllText(Path.Combine(outDir, "_ssm_publish_diagnostics.log"),
                            string.Format("[{0:yyyy-MM-dd HH:mm:ss}] kept DSD: {1} ({2} bytes){3}",
                                DateTime.Now, keptDsd, fiKept.Length, Environment.NewLine));
                    }
                    catch { }
                }

                AcadApp.Publisher.PublishExecute(
                dsd, PlotConfigManager.SetCurrentConfig("DWG To PDF.pc3"));
                if (!File.Exists(destPdf))
                {
                    WritePublishFailureReport(outDir, destPdf, entries, dsdFile,
                        "Publisher completed without creating the destination PDF.");
                    try { ed.WriteMessage("\n[LỖI publish] Không tạo được PDF: " + destPdf + "."); } catch { }
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                WritePublishFailureReport(outDir, destPdf, entries, dsdFile, ex.ToString());
                try { ed.WriteMessage("\n[LỖI publish] " + ex.Message + "."); } catch { }
                return false;
            }
            finally
            {

                AcadApp.SetSystemVariable("BACKGROUNDPLOT", bp);
                AcadApp.SetSystemVariable("FILEDIA", filedia);
                if (File.Exists(dsdFile)) File.Delete(dsdFile);
            }
        }

        private static void WritePublishFailureReport(
            string outputDirectory,
            string destinationPdf,
            DsdEntryCollection entries,
            string dsdFile,
            string error)
        {
            if (!EnablePublishDiagnostics) return;
            try
            {
                string reportPath = Path.Combine(outputDirectory,
                    "_ssm_publish_failure_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
                var report = new StringBuilder();
                report.AppendLine("Destination PDF: " + (destinationPdf ?? ""));
                report.AppendLine("Error: " + (error ?? ""));
                report.AppendLine("Entries:");
                if (entries != null)
                {
                    foreach (DsdEntry entry in entries)
                    {
                        report.AppendLine("Title=" + (entry.Title ?? "")
                            + " | DWG=" + (entry.DwgName ?? "")
                            + " | Layout=" + (entry.Layout ?? ""));
                    }
                }
                File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(dsdFile) && File.Exists(dsdFile))
                {
                    string dsdCopy = Path.ChangeExtension(reportPath, ".dsd");
                    File.Copy(dsdFile, dsdCopy, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("[SSP-PUBLISH-DIAG] Could not save failure report: " + ex);
            }
        }

        // Ep DSD khong hoi ten file: moi token PromptFor* -> FALSE theo tung dong; chen vao [Target] neu thieu.
        // DSD ghi moi sheet duoi dang section [DWF6Sheet:<Title>]. Title rong hoac trung
        // nhau lam WriteDsd bo sot/ghi de entries -> thieu sheet khi gop.
        // Dam bao moi entry co Title khac rong va duy nhat truoc khi publish.
        private static void EnsureUniqueDsdTitles(DsdEntryCollection entries)
        {
            if (entries == null) return;
            try
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DsdEntry e in entries)
                {
                    string t = (e.Title ?? "").Trim();
                    if (t.Length == 0)
                        t = (e.Layout ?? "").Trim();
                    if (t.Length == 0)
                    {
                        try { t = Path.GetFileNameWithoutExtension(e.DwgName ?? ""); }
                        catch { t = ""; }
                        t = (t ?? "").Trim();
                    }
                    if (t.Length == 0)
                        t = "Sheet";
                    string u = t; int d = 1;
                    while (!used.Add(u))
                        u = t + " (" + (++d) + ")";
                    e.Title = u;
                }
            }
            catch { }
        }

        // Phat hien BOM de giu nguyen encoding goc cua file DSD khi ghi lai.
        // WriteDsd ghi UTF-16 co BOM; neu ghi lai bang Encoding.Default (ANSI, khong BOM)
        // thi ReadDsd se doc sai cac ky tu tieng Viet trong ten layout/duong dan DWG
        // -> Publisher loai sheet ("Layout not found" / thieu sheet khi gop).
        private static Encoding DetectFileEncoding(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] bom = new byte[4];
                    int n = fs.Read(bom, 0, 4);
                    if (n >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                        return new UTF8Encoding(true);
                    if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                        return new UnicodeEncoding(false, true);
                    if (n >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                        return new UnicodeEncoding(true, true);
                }
            }
            catch { }
            return null;
        }

        private static int CountDsdSheetSections(string dsdFile)
        {
            try
            {
                int n = 0;
                foreach (var line in File.ReadLines(dsdFile))
                {
                    if (line.Trim().StartsWith("[Sheet", StringComparison.OrdinalIgnoreCase))
                        n++;
                }
                return n;
            }
            catch { return -1; }
        }

        private static int CountDsdSheetsAfterRead(DsdData dsd, string outDir)
        {
            string tmp = null;
            try
            {
                tmp = Path.Combine(outDir, "_ssm_dsd_reread_check.dsd");
                dsd.WriteDsd(tmp);
                return CountDsdSheetSections(tmp);
            }
            catch { return -1; }
            finally { try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static void ForceNoPrompt(string dsdFile, Encoding enc)
        {

            try
            {

                Encoding actualEnc = DetectFileEncoding(dsdFile) ?? enc;
                var lines = new System.Collections.Generic.List<string>(File.ReadAllLines(dsdFile, actualEnc));
                bool foundDwg = false; int targetIdx = -1;
                for (int i = 0; i < lines.Count; i++)
                {

                    string t = lines[i].Trim();
                    if (t.StartsWith("[Target]", StringComparison.OrdinalIgnoreCase)) targetIdx = i;
                    if (t.StartsWith("PromptFor", StringComparison.OrdinalIgnoreCase))
                    {

                        int eq = lines[i].IndexOf('=');
                        string key = eq > 0 ? lines[i].Substring(0, eq).Trim() : t;
                        lines[i] = key + "=FALSE";
                        if (key.Equals("PromptForDwgName", StringComparison.OrdinalIgnoreCase))
                            foundDwg = true;
                    }
                }
                if (!foundDwg && targetIdx >= 0)
                    lines.Insert(targetIdx + 1, "PromptForDwgName=FALSE");
                File.WriteAllLines(dsdFile, lines, actualEnc);
            }
            catch { }
        }
    }
}