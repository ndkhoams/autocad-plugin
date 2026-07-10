using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.PlottingServices;
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
                map["SheetTitle"]  = mergedMode ? "" : s.Title;
                map["SheetDesc"]   = mergedMode ? "" : s.Desc;
                map["LayoutName"]  = mergedMode ? "" : s.LayoutName;
                map["DwgName"]     = mergedMode ? "" :
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
            using (var form = new PlotNamingForm(sheets, defDir))
            {
                if (AcadApp.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK) return;
                template = form.Template; outDir = form.OutputDir;
            }
            Directory.CreateDirectory(outDir);

            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            { ed.WriteMessage("\nĐang có tiến trình in khác, thử lại sau."); return; }

            // BAT BUOC khi dung PlotEngine (foreground): tat in nen, neu khong moi sheet se loi eInvalidInput
            short bp = (short)AcadApp.GetSystemVariable("BACKGROUNDPLOT");
            AcadApp.SetSystemVariable("BACKGROUNDPLOT", 0);

            int ok = 0;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (doc.LockDocument())
            {
                try
                {
                    foreach (var s in sheets)
                    {
                        if (string.IsNullOrEmpty(s.DwgPath) || !File.Exists(s.DwgPath))
                        { ed.WriteMessage("\nBỏ qua (không tìm thấy DWG): " + s.Title); continue; }

                        string name = SsmNaming.SanitizeFile(SsmNaming.Resolve(template, s, false));
                        if (string.IsNullOrWhiteSpace(name)) name = s.LayoutName;
                        string baseName = name; int n = 2;
                        while (!used.Add(name)) name = baseName + " (" + (n++) + ")";
                        string file = Path.Combine(outDir, SsmNaming.EnsurePdf(name));

                        try
                        {
                            PlotLayoutFromFile(doc.Database, s.DwgPath, s.LayoutName, file);
                            ok++;
                            ed.WriteMessage("\n[OK] " + Path.GetFileName(file));
                        }
                        catch (Exception ex) { ed.WriteMessage("\n[LỖI] " + s.Title + ": " + ex.Message); }
                    }
                }
                finally { AcadApp.SetSystemVariable("BACKGROUNDPLOT", bp); }
            }
            ed.WriteMessage("\nHoàn tất: {0}/{1} sheet -> {2}", ok, sheets.Count, outDir);
        }

        private static void PlotLayoutFromFile(Database activeDb, string dwgPath, string layoutName, string outFile)
        {
            bool isCurrent = !string.IsNullOrEmpty(activeDb.Filename) &&
                string.Equals(Path.GetFullPath(activeDb.Filename), Path.GetFullPath(dwgPath),
                    StringComparison.OrdinalIgnoreCase);

            if (isCurrent) { PlotLayout(activeDb, layoutName, outFile); return; }

            using (Database sdb = new Database(false, true))
            {
                sdb.ReadDwgFile(dwgPath, FileShare.Read, true, null);
                PlotLayout(sdb, layoutName, outFile);
            }
        }

        private static void PlotLayout(Database db, string layoutName, string outFile)
        {
            Database prev = HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase = db;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBDictionary dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    if (!dict.Contains(layoutName)) throw new Exception("Không có layout '" + layoutName + "'");
                    Layout lo = (Layout)tr.GetObject(dict.GetAt(layoutName), OpenMode.ForRead);

                    PlotInfo pi = new PlotInfo { Layout = lo.ObjectId };
                    PlotSettings ps = new PlotSettings(lo.ModelType);
                    ps.CopyFrom(lo);
                    var psv = PlotSettingsValidator.Current;
                    psv.SetPlotConfigurationName(ps, "DWG To PDF.pc3", null);
                    psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.StdScale1To1);
                    psv.SetPlotCentered(ps, true);
                    pi.OverrideSettings = ps;
                    new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(pi);

                    using (PlotEngine engine = PlotFactory.CreatePublishEngine())
                    {
                        engine.BeginPlot(null, null);
                        engine.BeginDocument(pi, layoutName, null, 1, true, outFile);
                        engine.BeginPage(new PlotPageInfo(), pi, true, null);
                        engine.BeginGenerateGraphics(null);
                        engine.EndGenerateGraphics(null);
                        engine.EndPage(null);
                        engine.EndDocument(null);
                        engine.EndPlot(null);
                    }
                    tr.Commit();
                }
            }
            finally { HostApplicationServices.WorkingDatabase = prev; }
        }
    }
}
