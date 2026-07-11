using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Publishing;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;   // tranh nhap nhang voi Autodesk.AutoCAD.Runtime.Exception

[assembly: CommandClass(typeof(BatchPlotPdf.BatchPlotSsmCommand))]

namespace BatchPlotPdf
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
            // Project number nhap tay tu form MTECH (COM khong doc duoc gia tri that) -> ghi de token.
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

    public class BatchPlotSsmCommand
    {
        [CommandMethod("MTECH", CommandFlags.Session)]
        public void BatchPdfSsm()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            List<SheetInfo> sheets;
            try { sheets = SheetSetReader.ReadOpenSheetSets(); }
            catch (Exception ex) { ed.WriteMessage("\nKhông đọc được Sheet Set: " + ex.Message); return; }

            if (sheets.Count == 0)
            {
                ed.WriteMessage("\nChưa mở Sheet Set nào trong Sheet Set Manager.");
                return;
            }

            string defDir = !string.IsNullOrEmpty(doc.Database.Filename)
                ? Path.Combine(Path.GetDirectoryName(doc.Database.Filename), "PDF")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PDF");

            string template, outDir, projNum;
            bool merged;
            PlotNamingForm.SsmAction action;
            List<SheetInfo> allSheets = sheets;   // giu ban day du de luu nguoc .dst
            List<SheetInfo> selected;
            using (var form = new PlotNamingForm(sheets, defDir))
            {
                if (AcadApp.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK) return;
                action = form.Action;
                template = form.Template; outDir = form.OutputDir; merged = form.Merged;
                projNum = form.ProjectNumber;
                selected = form.SelectedSheets;
            }

            // Bam "Lưu Sheet Set" -> ghi thay doi (Number/Title/Rev/CONT/SHT...) nguoc vao .dst, khong in.
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
            if (sheets.Count == 0) { ed.WriteMessage("\nBạn chưa chọn sheet nào để in."); return; }
            Directory.CreateDirectory(outDir);

            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            { ed.WriteMessage("\nĐang có tiến trình in khác, thử lại sau."); return; }

            int ok = 0;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // In tung sheet bang Publisher (DSD): KHONG dung PlotEngine cho database side-load.
            if (merged)
            {
                var all = new DsdEntryCollection();
                foreach (var s in sheets)
                {
                    if (string.IsNullOrEmpty(s.DwgPath) || !File.Exists(s.DwgPath))
                    { ed.WriteMessage("\nBỏ qua (không tìm thấy DWG): " + s.Title); continue; }
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
        }

        // SSMEDIT giu lai nhu alias -> mo cung 1 form gop (khong con form quan ly rieng).
        [CommandMethod("SSMEDIT", CommandFlags.Session)]
        public void SheetSetEdit() { BatchPdfSsm(); }

        // Publish 1 hoac nhieu DsdEntry ra PDF. BACKGROUNDPLOT=0 (dong bo) + FILEDIA=0 + ForceNoPrompt.
        private static bool PublishToPdf(DsdEntryCollection entries, string destPdf, string outDir, SheetType type, Editor ed)
        {
            if (entries == null || entries.Count == 0) return false;

            short bp = (short)AcadApp.GetSystemVariable("BACKGROUNDPLOT");
            short filedia = (short)AcadApp.GetSystemVariable("FILEDIA");
            AcadApp.SetSystemVariable("BACKGROUNDPLOT", 0);
            AcadApp.SetSystemVariable("FILEDIA", 0);   // TAT hop thoai "Specify PDF File"
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
                var lines = new List<string>(File.ReadAllLines(dsdFile, enc));
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