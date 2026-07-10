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
        public static string Resolve(string template, SheetInfo s, bool mergedMode)
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
                map["SheetSetName"] = s.SheetSetName;
                foreach (var kv in s.Custom)
                    if (!map.ContainsKey(kv.Key)) map[kv.Key] = kv.Value;
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
        [CommandMethod("BATCHPDFSSM", CommandFlags.Session)]
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

            string template, outDir;
            bool merged;
            using (var form = new PlotNamingForm(sheets, defDir))
            {
                if (AcadApp.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK) return;
                template = form.Template; outDir = form.OutputDir; merged = form.Merged;
                sheets = form.SelectedSheets;
            }
            if (sheets.Count == 0) { ed.WriteMessage("\nBạn chưa chọn sheet nào để in."); return; }
            Directory.CreateDirectory(outDir);

            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            { ed.WriteMessage("\nĐang có tiến trình in khác, thử lại sau."); return; }

            int ok = 0;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // In tung sheet bang Publisher (DSD): KHONG dung PlotEngine cho database side-load.
            // PlotEngine chi in duoc layout cua document dang MO & active -> in tu DWG side-load
            // (ReadDwgFile) se bao eInvalidInput o moi sheet. Publisher publish duoc layout tu
            // nhieu DWG khac ma khong can mo file.
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

                // Moi sheet = 1 DSD entry + SheetType.MultiPdf -> 1 file PDF dung ten tuy y (DestinationName)
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

        // Publish 1 hoac nhieu DsdEntry ra PDF. Dat BACKGROUNDPLOT=0 de chay foreground (dong bo);
        // patch PromptForDwgName=FALSE de khong hien hop thoai hoi ten file.
        private static bool PublishToPdf(DsdEntryCollection entries, string destPdf, string outDir, SheetType type, Editor ed)
        {
            if (entries == null || entries.Count == 0) return false;

            short bp = (short)AcadApp.GetSystemVariable("BACKGROUNDPLOT");
            AcadApp.SetSystemVariable("BACKGROUNDPLOT", 0);
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

                // DSD la file ANSI theo code page he thong; doc/ghi UTF-8 se lam hong ky tu
                // tieng Viet VA khien ReadDsd bo qua PromptForDwgName (nen hop thoai
                // "Specify PDF File" van hien). Dung Encoding.Default (ANSI).
                var enc = Encoding.Default;
                string txt = File.ReadAllText(dsdFile, enc)
                    .Replace("PromptForDwgName=TRUE", "PromptForDwgName=FALSE");
                File.WriteAllText(dsdFile, txt, enc);
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
                if (File.Exists(dsdFile)) File.Delete(dsdFile);
            }
        }
    }
}