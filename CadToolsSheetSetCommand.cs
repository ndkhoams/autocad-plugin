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
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;
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
            if (string.IsNullOrEmpty(s)) return s;

            // Step 1: Extract only the filename, removing any path components
            // This prevents path traversal attacks like "../../system.pdf"
            s = Path.GetFileName(s);

            // Step 2: Replace invalid filename characters
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            return s.Trim();
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
            // 1) Ưu tiên sheetset đang được chọn trong Sheet Set Manager.
            // Nếu SSM không hiện đường dẫn thì mới dùng các database COM đang mở.
            GList sheets;
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
                        sheets = SheetSetReader.ReadFromDst(dstPath);
                }
            }
            catch (Exception ex) { ed.WriteMessage("\nKhông đọc được Sheet Set hiện hành: " + ex.Message); sheets = new GList(); }

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
                        try { sheets = SheetSetReader.ReadFromDst(dstPath); }
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
                    selected = form.SelectedSheets;
                    deletedSheets.AddRange(form.DeletedSheets);
                }

                // Bam "Lưu Sheet Set" -> ghi thay doi nguoc vao .dst, khong in.
                if (action == PlotNamingForm.SsmAction.Save)
                {

                    SaveResult sr;
                    try { sr = SheetSetWriter.Save(allSheets, deletedSheets, ed); }
                    catch (Exception ex) { ed.WriteMessage("\nLỗi ghi Sheet Set: " + ex.Message); return; }
                    ed.WriteMessage("\n{0} {1} sheet. Revision ghi được: {2}, không ghi được: {3}.",
                    sr.CommitSucceeded ? "Đã commit" : "Đã xử lý nhưng chưa commit",
                    sr.SheetsSaved, sr.RevisionOk, sr.RevisionFail);
                    if (sr.RevisionFail > 0)
                        ed.WriteMessage("\nRevision/Issue purpose không ghi được qua COM (bản AutoCAD này không lộ setter) — sửa trực tiếp trong hộp thoại Sheet Properties của SSM.");
                    foreach (var w in sr.Warnings) ed.WriteMessage("\n- " + w);
                    return;
                }

                // Nguoc lai: bam "In PDF" -> chi in cac sheet dang tich.
                sheets = selected;
                if (sheets == null || sheets.Count == 0) { ed.WriteMessage("\nBạn chưa chọn sheet nào để in."); return; }
                Directory.CreateDirectory(outDir);

                if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                { ed.WriteMessage("\nĐang có tiến trình in khác, thử lại sau."); return; }

                int ok = 0;
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // In tung sheet bang Publisher (DSD): KHONG dung PlotEngine cho database side-load.
                if (merged)
                {

                    var all = new DsdEntryCollection();
                    // Tối ưu: gom các sheet cùng DWG cạnh nhau để hạn chế mở/đóng file nặng
                    sheets.Sort((a, b) => string.Compare(a == null ? "" : (a.DwgPath ?? ""), b == null ? "" : (b.DwgPath ?? ""), StringComparison.OrdinalIgnoreCase));

                    foreach (var s in sheets)
                    {

                        if (string.IsNullOrEmpty(s.DwgPath) || !File.Exists(s.DwgPath))
                        { ed.WriteMessage("\nBỏ qua (không tìm thấy DWG): " + s.Title); continue; }

                        // Tối ưu: nếu nhiều sheet liên tiếp cùng 1 DWG thì giữ DWG đang mở để in tiếp (tránh mở/đóng lại file nặng)
                        // Lưu ý: Publisher vẫn có thể tự load DB, nhưng việc giữ Document mở giúp giảm thời gian trên nhiều máy.
                        EnsureDwgOpenForSheet(s.DwgPath);
                        all.Add(new DsdEntry { DwgName = s.DwgPath, Layout = s.LayoutName, Title = s.Title, Nps = "" });
                    }
                    if (all.Count == 0) { ed.WriteMessage("\nKhông có sheet hợp lệ để in."); return; }

                    string mName = SsmNaming.SanitizeFile(SsmNaming.Resolve(template, sheets.Count > 0 ? sheets[0] : null, true));
                    if (string.IsNullOrWhiteSpace(mName)) mName = "MergedSheets";
                    string mFile = Path.Combine(outDir, SsmNaming.EnsurePdf(mName));

                    if (PublishToPdf(all, mFile, outDir, SheetType.MultiPdf, ed))
                        ed.WriteMessage("\nĐã xuất PDF gộp {0} sheet -> {1}", all.Count, mFile);
                    return;
                }

                foreach (var s in sheets)
                {

                    if (string.IsNullOrEmpty(s.DwgPath) || !File.Exists(s.DwgPath))
                    { ed.WriteMessage("\nBỏ qua (không tìm thấy DWG): " + s.Title); continue; }

                    string name = SsmNaming.SanitizeFile(SsmNaming.Resolve(template, s, false));
                    if (string.IsNullOrWhiteSpace(name)) name = s.LayoutName;
                    string baseName = name; int n = 2;
                    while (!used.Add(name)) name = baseName + " (" + (n++) + ")";
                    string file = Path.Combine(outDir, SsmNaming.EnsurePdf(name));

                    var one = new DsdEntryCollection();
                    one.Add(new DsdEntry { DwgName = s.DwgPath, Layout = s.LayoutName, Title = s.Title, Nps = "" });

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
                ed.WriteMessage("\nHoàn tất: {0}/{1} sheet -> {2}", ok, sheets.Count, outDir);
                return;
            }
        }



        // Cache Document theo DWG để tránh mở/đóng liên tục
        private static string _openDwgPath = null;
        private static Document _openDwgDoc = null;
        private static bool _openDwgOwned = false;

        private static void EnsureDwgOpenForSheet(string dwgPath)
        {

            try
            {

                if (string.IsNullOrWhiteSpace(dwgPath)) return;

                // Nếu đang đúng DWG thì thôi
                if (_openDwgDoc != null && string.Equals(_openDwgPath ?? "", dwgPath, StringComparison.OrdinalIgnoreCase))
                    return;

                // Không đóng DWG đã mở: giữ lại để tận dụng cache khi các sheet cùng DWG

                _openDwgPath = dwgPath;
                _openDwgDoc = null;
                _openDwgOwned = false;

                // Nếu DWG đã mở sẵn trong AutoCAD thì dùng lại
                foreach (Document d in AcadApp.DocumentManager)
                {

                    try
                    {

                        if (!string.IsNullOrEmpty(d.Name) && string.Equals(d.Name, dwgPath, StringComparison.OrdinalIgnoreCase))
                        {

                            _openDwgDoc = d;
                            _openDwgOwned = false;
                            return;
                        }
                    }
                    catch { }
                }

                // Nếu chưa mở thì mở nền (không activate) để Publisher dùng lại
                try
                {

                    _openDwgDoc = AcadApp.DocumentManager.Open(dwgPath, false);
                    _openDwgOwned = true;
                }
                catch
                {

                    _openDwgDoc = null;
                    _openDwgOwned = false;
                }
            }
            catch { }
        }

        // In mỗi sheet 1 PDF bằng PlotEngine, nhóm theo DWG để mở 1 lần rồi plot nhiều layout.
        private static int PlotPerSheetByPlotEngine(
        GList sheets,
        string template,
        string outDir,
        HashSet<string> usedNames,
        Editor ed)
        {

            if (sheets == null || sheets.Count == 0) return 0;

            // Nhóm theo DWG
            var byDwg = new Dictionary<string, GList>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sheets)
            {

                if (s == null) continue;
                string p = (s.DwgPath ?? "").Trim();
                if (p.Length == 0) continue;

                GList list;
                if (!byDwg.TryGetValue(p, out list))
                {

                    list = new GList();
                    byDwg[p] = list;
                }
                list.Add(s);
            }

            int ok = 0;
            foreach (var kv in byDwg)
            {

                string dwgPath = kv.Key;
                var list = kv.Value;

                if (!File.Exists(dwgPath))
                {

                    ed.WriteMessage("\nBỏ qua (không tìm thấy DWG): " + dwgPath);
                    continue;
                }

                Document dwgDoc = null;
                bool openedByTool = false;

                try
                {

                    // Dùng lại document nếu đã mở
                    foreach (Document d in AcadApp.DocumentManager)
                    {

                        try
                        {

                            if (!string.IsNullOrEmpty(d.Name) && string.Equals(d.Name, dwgPath, StringComparison.OrdinalIgnoreCase))
                            {

                                dwgDoc = d;
                                break;
                            }
                        }
                        catch { }
                    }

                    if (dwgDoc == null)
                    {

                        dwgDoc = AcadApp.DocumentManager.Open(dwgPath, false);
                        openedByTool = true;
                    }

                    if (dwgDoc == null)
                    {

                        ed.WriteMessage("\nKhông mở được DWG: " + dwgPath);
                        continue;
                    }

                    using (dwgDoc.LockDocument())
                    {

                        foreach (var s in list)
                        {

                            if (s == null) continue;

                            try
                            {

                                string name = SsmNaming.SanitizeFile(SsmNaming.Resolve(template, s, false));
                                if (string.IsNullOrWhiteSpace(name)) name = s.LayoutName;
                                string baseName = name; int n = 2;
                                while (!usedNames.Add(name)) name = baseName + " (" + (n++) + ")";
                                string pdfFile = Path.Combine(outDir, SsmNaming.EnsurePdf(name));

                                if (PlotLayoutToPdf(dwgDoc, s.LayoutName, pdfFile))
                                {

                                    ok++;
                                    ed.WriteMessage("\n[OK] " + Path.GetFileName(pdfFile));
                                }
                                else
                                {

                                    ed.WriteMessage("\n[LỖI] " + s.Title);
                                }
                            }
                            catch (Exception ex2)
                            {

                                ed.WriteMessage("\n[LỖI] " + s.Title + ": " + ex2.Message);
                            }
                        }
                    }
                }
                finally
                {

                    if (dwgDoc != null && openedByTool)
                    {

                        try { dwgDoc.CloseAndDiscard(); } catch { }
                    }
                }
            }

            return ok;
        }

        // Plot 1 layout ra 1 file PDF (không dùng Publisher/DSD)
        private static bool PlotLayoutToPdf(Document dwgDoc, string layoutName, string pdfFile)
        {

            if (dwgDoc == null) return false;
            if (string.IsNullOrWhiteSpace(layoutName)) return false;

            Database db = dwgDoc.Database;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {

                DBDictionary layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                if (!layoutDict.Contains(layoutName)) return false;

                ObjectId layoutId = layoutDict.GetAt(layoutName);
                Layout lo = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);

                using (PlotSettings ps = new PlotSettings(lo.ModelType))
                {

                    ps.CopyFrom(lo);

                    PlotSettingsValidator psv = PlotSettingsValidator.Current;

                    // cấu hình PDF
                    try { psv.SetPlotConfigurationName(ps, "DWG To PDF.pc3", null); } catch { }
                    psv.RefreshLists(ps);

                    psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                    psv.SetPlotCentered(ps, true);

                    PlotInfo pi = new PlotInfo();
                    pi.Layout = layoutId;
                    pi.OverrideSettings = ps;

                    PlotInfoValidator piv = new PlotInfoValidator();
                    piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                    piv.Validate(pi);

                    if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting) return false;

                    using (PlotEngine pe = PlotFactory.CreatePublishEngine())
                    {

                        PlotProgressDialog ppd = new PlotProgressDialog(false, 1, true);
                        using (ppd)
                        {

                            ppd.OnBeginPlot();
                            ppd.IsVisible = false;

                            pe.BeginPlot(ppd, null);
                            pe.BeginDocument(pi, dwgDoc.Name, null, 1, true, pdfFile);

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

                    tr.Commit();
                }

                return File.Exists(pdfFile);
            }
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
                                int i = s.IndexOf(".dst", StringComparison.OrdinalIgnoreCase);
                                if (i < 0) continue;
                                s = s.Trim();

                                // accept absolute drive path or UNC
                                if (!(s.Contains(":\\") || s.StartsWith("\\\\"))) continue;

                                // trim to end of .dst
                                int j = s.IndexOf(".dst", StringComparison.OrdinalIgnoreCase);
                                if (j >= 0) s = s.Substring(0, j + 4);
                                return s;
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
                dsd.SetDsdEntryCollection(entries);
                dsd.WriteDsd(dsdFile);

                var enc = Encoding.Default;
                ForceNoPrompt(dsdFile, enc);
                try { File.Copy(dsdFile, Path.Combine(outDir, "_dsd_debug.txt"), true); } catch { }
                dsd.ReadDsd(dsdFile);

                AcadApp.Publisher.PublishExecute(
                dsd, PlotConfigManager.SetCurrentConfig("DWG To PDF.pc3"));
                return true;
            }
            catch (Exception ex)
            {

                ed.WriteMessage("\n[LỖI publish] " + ex.Message);
                return false;
            }
            finally
            {

                AcadApp.SetSystemVariable("BACKGROUNDPLOT", bp);
                AcadApp.SetSystemVariable("FILEDIA", filedia);
                if (File.Exists(dsdFile)) File.Delete(dsdFile);
            }
        }

        // Ep DSD khong hoi ten file: moi token PromptFor* -> FALSE theo tung dong; chen vao [Target] neu thieu.
        private static void ForceNoPrompt(string dsdFile, Encoding enc)
        {

            try
            {

                var lines = new System.Collections.Generic.List<string>(File.ReadAllLines(dsdFile, enc));
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
                File.WriteAllLines(dsdFile, lines, enc);
            }
            catch { }
        }
    }
}