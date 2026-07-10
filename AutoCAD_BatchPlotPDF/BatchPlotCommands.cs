using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Publishing;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(BatchPlotPdf.BatchPlotCommands))]

namespace BatchPlotPdf
{
    public class BatchPlotCommands
    {
        // Mỗi layout -> 1 file PDF riêng
        [CommandMethod("BATCHPDF", CommandFlags.Session)]
        public void BatchPdf()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            if (string.IsNullOrEmpty(db.Filename))
            {
                ed.WriteMessage("\nHãy lưu bản vẽ trước khi in.");
                return;
            }
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            {
                ed.WriteMessage("\nĐang có tiến trình in khác, thử lại sau.");
                return;
            }

            string outDir = Path.Combine(Path.GetDirectoryName(db.Filename), "PDF");
            Directory.CreateDirectory(outDir);
            string baseName = Path.GetFileNameWithoutExtension(db.Filename);
            int count = 0;

            // BAT BUOC khi dung PlotEngine (foreground): tat in nen, neu khong se loi eInvalidInput
            short bp = (short)AcadApp.GetSystemVariable("BACKGROUNDPLOT");
            AcadApp.SetSystemVariable("BACKGROUNDPLOT", 0);
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var layouts = GetLayouts(db, tr);
                    if (layouts.Count == 0) { ed.WriteMessage("\nKhông có layout nào để in."); return; }

                    using (PlotProgressDialog ppd = new PlotProgressDialog(false, layouts.Count, true))
                    using (PlotEngine engine = PlotFactory.CreatePublishEngine())
                    {
                        ppd.set_PlotMsgString(PlotMessageIndex.DialogTitle, "In hàng loạt ra PDF");
                        ppd.LowerPlotProgressRange = 0;
                        ppd.UpperPlotProgressRange = 100;
                        ppd.PlotProgressPos = 0;
                        ppd.OnBeginPlot();
                        ppd.IsVisible = true;

                        engine.BeginPlot(ppd, null);

                        foreach (Layout lo in layouts)
                        {
                            ppd.set_PlotMsgString(PlotMessageIndex.Status, "Đang in: " + lo.LayoutName);
                            ppd.OnBeginSheet();
                            ppd.LowerSheetProgressRange = 0;
                            ppd.UpperSheetProgressRange = 100;
                            ppd.SheetProgressPos = 0;

                            PlotInfo pi = new PlotInfo { Layout = lo.ObjectId };
                            PlotSettings ps = new PlotSettings(lo.ModelType);
                            ps.CopyFrom(lo);
                            PlotSettingsValidator psv = PlotSettingsValidator.Current;
                            psv.SetPlotConfigurationName(ps, "DWG To PDF.pc3", null);
                            psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                            psv.SetUseStandardScale(ps, true);
                            psv.SetStdScaleType(ps, StdScaleType.StdScale1To1);
                            psv.SetPlotCentered(ps, true);
                            pi.OverrideSettings = ps;

                            PlotInfoValidator piv = new PlotInfoValidator
                            {
                                MediaMatchingPolicy = MatchingPolicy.MatchEnabled
                            };
                            piv.Validate(pi);

                            string file = Path.Combine(outDir, baseName + " - " + Sanitize(lo.LayoutName) + ".pdf");

                            PlotPageInfo ppi = new PlotPageInfo();
                            engine.BeginDocument(pi, doc.Name, null, 1, true, file);
                            engine.BeginPage(ppi, pi, true, null);
                            engine.BeginGenerateGraphics(null);
                            engine.EndGenerateGraphics(null);
                            engine.EndPage(null);
                            engine.EndDocument(null);

                            ppd.SheetProgressPos = 100;
                            ppd.OnEndSheet();
                            count++;
                            ppd.PlotProgressPos = (int)(100.0 * count / layouts.Count);
                        }

                        engine.EndPlot(null);
                        ppd.PlotProgressPos = 100;
                        ppd.OnEndPlot();
                    }
                    tr.Commit();
                }
            }
            finally { AcadApp.SetSystemVariable("BACKGROUNDPLOT", bp); }
            ed.WriteMessage("\nĐã in {0} layout ra PDF trong: {1}", count, outDir);
        }

        // Tất cả layout -> 1 file PDF nhiều trang (gộp)
        [CommandMethod("BATCHPDF1FILE", CommandFlags.Session)]
        public void BatchPdfSingleFile()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            if (string.IsNullOrEmpty(db.Filename)) { ed.WriteMessage("\nHãy lưu bản vẽ."); return; }

            string outDir = Path.Combine(Path.GetDirectoryName(db.Filename), "PDF");
            Directory.CreateDirectory(outDir);
            string pdfPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(db.Filename) + ".pdf");

            var entries = new DsdEntryCollection();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (Layout lo in GetLayouts(db, tr))
                {
                    entries.Add(new DsdEntry
                    {
                        DwgName = db.Filename,
                        Layout = lo.LayoutName,
                        Title = lo.LayoutName,
                        Nps = ""
                    });
                }
                tr.Commit();
            }
            if (entries.Count == 0) { ed.WriteMessage("\nKhông có layout."); return; }

            short bp = (short)AcadApp.GetSystemVariable("BACKGROUNDPLOT");
            AcadApp.SetSystemVariable("BACKGROUNDPLOT", 0);

            DsdData dsd = new DsdData
            {
                SheetType = SheetType.MultiPdf,
                DestinationName = pdfPath,
                ProjectPath = outDir,
                NoOfCopies = 1,
                IsHomogeneous = false
            };
            dsd.SetDsdEntryCollection(entries);

            string dsdFile = Path.Combine(outDir, "_batch.dsd");
            dsd.WriteDsd(dsdFile);
            string txt = File.ReadAllText(dsdFile)
                .Replace("PromptForDwgName=TRUE", "PromptForDwgName=FALSE");
            File.WriteAllText(dsdFile, txt);
            dsd.ReadDsd(dsdFile);

            try
            {
                AcadApp.Publisher.PublishExecute(
                    dsd, PlotConfigManager.SetCurrentConfig("DWG To PDF.pc3"));
                ed.WriteMessage("\nĐã xuất PDF gộp: {0}", pdfPath);
            }
            finally
            {
                AcadApp.SetSystemVariable("BACKGROUNDPLOT", bp);
                if (File.Exists(dsdFile)) File.Delete(dsdFile);
            }
        }

        private static List<Layout> GetLayouts(Database db, Transaction tr)
        {
            var list = new List<Layout>();
            DBDictionary dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                Layout lo = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (!lo.LayoutName.Equals("Model", StringComparison.OrdinalIgnoreCase))
                    list.Add(lo);
            }
            list.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));
            return list;
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }
    }
}
