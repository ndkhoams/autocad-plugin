using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Publishing;
using Autodesk.AutoCAD.Runtime;
using CADtools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
// NOTE: tránh bị nhầm List của WPF (System.Windows.Documents.List). Chỉ alias cho danh sách SheetInfo.
using GList = System.Collections.Generic.List<CADtools.SheetInfo>;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception; // tranh nhap nhang voi Autodesk.AutoCAD.Runtime.Exception
using AcSm = ACSMCOMPONENTS24Lib;

[assembly: CommandClass(typeof(CADtools.CadToolsSheetSetCommand))]

namespace CADtools
{
    public static class SsmNaming
    {
        public static string Resolve(string template, SheetInfo s, bool mergedMode, string projectNumberOverride = null)
        {
            if (string.IsNullOrEmpty(template)) return "";
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (s != null)
            {

                map["SheetNumber"] = mergedMode ? "" : s.Number;
                map["SheetTitle"] = mergedMode ? "" : s.Title;
                map["SheetDesc"] = mergedMode ? "" : s.Desc;
                map["LayoutName"] = mergedMode ? "" : s.LayoutName;
                map["DwgName"] = mergedMode ? "" :
                (string.IsNullOrEmpty(s.DwgPath) ? "" : Path.GetFileNameWithoutExtension(s.DwgPath));
                map["Revision"] = mergedMode ? "" : s.Revision;
                map["RevisionDate"] = mergedMode ? "" : s.RevisionDate;
                map["IssuePurpose"] = mergedMode ? "" : s.IssuePurpose;
                map["SheetSetName"] = s.SheetSetName;
                foreach (var kv in s.Custom)
                    if (!map.ContainsKey(kv.Key)) map[kv.Key] = kv.Value;
            }

            // Project number nhap tay tu form (COM khong doc duoc gia tri that) -> ghi de token.
            if (!string.IsNullOrEmpty(projectNumberOverride))
            {
                map["Project Number"] = projectNumberOverride;
                map["ProjectNumber"] = projectNumberOverride;
            }

            var sb = new StringBuilder();
            int i = 0;
            while (i < template.Length)
            {
                if (i + 1 < template.Length && template[i] == '$' && template[i + 1] == '(')
                {

                    int end = template.IndexOf(')', i + 2);
                    if (end > 0)
                    {

                        string key = template.Substring(i + 2, end - i - 2);
                        string val;
                        sb.Append(map.TryGetValue(key, out val) ? val : "");
                        i = end + 1;
                        continue;
                    }
                }
                sb.Append(template[i]); i++;
            }
            return sb.ToString().Trim();
        }

        public static string SanitizeFile(string s)
        {

            if (string.IsNullOrEmpty(s)) return s;
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
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

            // 1) Default: doc sheet set dang mo (SSM hien hanh)
            // NOTE: Dùng List để tránh bị nhầm với System.Collections.List (non-generic)
            GList sheets;
            try { sheets = SheetSetReader.ReadOpenSheetSets(); }
            catch (Exception ex) { ed.WriteMessage("\nKhông đọc được Sheet Set hiện hành: " + ex.Message); return; }

            // Nếu chưa mở Sheet Set nào thì vẫn mở form để người dùng chọn file .dst.
            if (sheets == null) sheets = new GList();

            string defDir = !string.IsNullOrEmpty(doc.Database.Filename)
            ? Path.Combine(Path.GetDirectoryName(doc.Database.Filename), "PDF")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PDF");

            // DST path thật từ COM database (nếu không có sheet set đang mở thì để trống).
            string dstPath = (sheets.Count > 0) ? TryGetCurrentDstPath(sheets) : "";

            // 2) Loop: mo form -> neu chon DST khac thi reload sheets va mo lai form
            while (true)
            {

                string template, outDir, projNum;
                bool merged;
                PlotNamingForm.SsmAction action;
                GList allSheets = sheets; // giu ban day du de luu nguoc .dst
                GList selected;

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
                    projNum = form.ProjectNumber;
                    selected = form.SelectedSheets;
                }

                // Bam "Lưu Sheet Set" -> ghi thay doi nguoc vao .dst, khong in.
                if (action == PlotNamingForm.SsmAction.Save)
                {

                    SaveResult sr;
                    try { sr = SheetSetWriter.Save(allSheets, ed); }
                    catch (Exception ex) { ed.WriteMessage("\nLỗi ghi Sheet Set: " + ex.Message); return; }
                    ed.WriteMessage("\nĐã lưu {0} sheet. Revision ghi được: {1}, không ghi được: {2}.",
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

                        // Giữ DWG đang mở nếu sheet tiếp theo cùng file
                        EnsureDwgOpenForSheet(s.DwgPath);

                        // Tối ưu: nếu nhiều sheet liên tiếp cùng 1 DWG thì giữ DWG đang mở để in tiếp (tránh mở/đóng lại file nặng)
                        // Lưu ý: Publisher vẫn có thể tự load DB, nhưng việc giữ Document mở giúp giảm thời gian trên nhiều máy.
                        EnsureDwgOpenForSheet(s.DwgPath);
                        all.Add(new DsdEntry { DwgName = s.DwgPath, Layout = s.LayoutName, Title = s.Title, Nps = "" });
                    }
                    if (all.Count == 0) { ed.WriteMessage("\nKhông có sheet hợp lệ để in."); return; }

                    string mName = SsmNaming.SanitizeFile(SsmNaming.Resolve(template, sheets.Count > 0 ? sheets[0] : null, true, projNum));
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

                    string name = SsmNaming.SanitizeFile(SsmNaming.Resolve(template, s, false, projNum));
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
        string projNum,
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

                                string name = SsmNaming.SanitizeFile(SsmNaming.Resolve(template, s, false, projNum));
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