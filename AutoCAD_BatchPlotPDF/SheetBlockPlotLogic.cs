using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Exception = System.Exception; // tránh nhầm với Autodesk.AutoCAD.Runtime.Exception

namespace CADtools
{
    // File Logic: tất cả xử lý AutoCAD DB / scan block / plot PDF nằm ở đây.
    public class SheetBlockPlotLogic
    {
        private readonly Document _doc;
        private readonly Editor _ed;

        public SheetBlockPlotLogic(Document doc)
        {
            _doc = doc;
            _ed = doc.Editor;
        }

        public class BlockItem
        {
            public int Stt = 0;
            public string LayoutName = ""; // "Model" hoặc tên layout
            public string KyHieu = "";     // MT_KH
            public string HangMuc = "";    // MT_TENHANGMUC
            public string TenBanVe = "";   // MT_TENBANVE
            public string PdfName = "";    // tên file PDF sẽ xuất
            public string Handle = "";

            public double PosX = 0;
            public double PosY = 0;

            public Extents2d Window;       // WCS 2D window
            public bool RectLandscape = true;
        }

        public List<BlockItem> CollectBlocks(string targetName, HashSet<string> windowFilterHandles)
        {
            var items = new List<BlockItem>();
            string target = (targetName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(target)) target = "KHUNG_MT";

            try
            {
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);

                    // Model space
                    TryCollectFromBtr(tr, items, windowFilterHandles,
                        (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead),
                        "Model", "Model", target);

                    // All paper layouts
                    DBDictionary layoutDict = (DBDictionary)tr.GetObject(_doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry de in layoutDict)
                    {
                        Layout lo = (Layout)tr.GetObject(de.Value, OpenMode.ForRead);
                        if (lo.ModelType) continue;
                        var btr = (BlockTableRecord)tr.GetObject(lo.BlockTableRecordId, OpenMode.ForRead);
                        TryCollectFromBtr(tr, items, windowFilterHandles, btr, "Layout:" + lo.LayoutName, lo.LayoutName, target);
                    }

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                try { _ed.WriteMessage("\n[LỖI CollectBlocks] " + ex.Message); } catch { }
            }

            return items;
        }

        private void TryCollectFromBtr(
            Transaction tr,
            List<BlockItem> items,
            HashSet<string> windowFilterHandles,
            BlockTableRecord btr,
            string spaceLabel,
            string layoutName,
            string targetName)
        {
            foreach (ObjectId id in btr)
            {
                BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null) continue;

                string name = ResolveBlockName(tr, br);
                // Lọc theo prefix: "KHUNG_MT" sẽ bắt cả "KHUNG_MT", "KHUNG_MT_A1", ...
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(targetName)
                    || !name.StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Nếu đang bật filter theo vùng quét: chỉ lấy các block nằm trong selection
                if (windowFilterHandles != null && windowFilterHandles.Count > 0)
                {
                    string hh = "";
                    try { hh = br.Handle.ToString(); } catch { hh = ""; }
                    if (string.IsNullOrWhiteSpace(hh) || !windowFilterHandles.Contains(hh))
                        continue;
                }

                // window = polyline RECT khung in trong block (kể cả nested block).
                // KHÔNG fallback sang GeometricExtents để tránh in sai vùng.
                Extents2d win;
                bool rectLandscape;
                if (!TryGetOuterFrameWindow(tr, br, out win, out rectLandscape))
                {
                    try { _ed.WriteMessage("\n[SBP] Không tìm thấy RECT polyline khung in (kể cả nested). Bỏ qua. Handle=" + br.Handle); } catch { }
                    continue;
                }

                var map = ReadAttributesMap(tr, br);
                string kyHieu = GetAttr(map, "MT_KH");
                string hangMuc = GetAttr(map, "MT_TENHANGMUC");
                string tenBanVe = GetAttr(map, "MT_TENBANVE");

                string pdfBase = (kyHieu + "_" + tenBanVe).Trim('_').Trim();
                if (string.IsNullOrWhiteSpace(pdfBase)) pdfBase = "KHUNG_MT_" + br.Handle.ToString();

                items.Add(new BlockItem
                {
                    Stt = items.Count + 1,
                    LayoutName = layoutName ?? "",
                    KyHieu = kyHieu,
                    HangMuc = hangMuc,
                    TenBanVe = tenBanVe,
                    PdfName = SanitizeFileName(pdfBase) + ".pdf",
                    Handle = br.Handle.ToString(),

                    // Lấy vị trí block theo WCS (đủ để sort trong cùng layout)
                    PosX = br.Position.X,
                    PosY = br.Position.Y,

                    Window = win,
                    RectLandscape = rectLandscape
                });
            }
        }

        // Lấy extents của polyline RECT khung in trong block (có thể nằm trong nested block),
        // rồi transform theo BlockReference.
        private static bool TryGetOuterFrameWindow(Transaction tr, BlockReference br, out Extents2d win, out bool rectLandscape)
        {
            win = new Extents2d(new Point2d(0, 0), new Point2d(0, 0));
            rectLandscape = true;
            if (tr == null || br == null) return false;

            try
            {
                var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (def == null) return false;

                bool has = false;
                Extents3d best = new Extents3d(); // extents trong hệ def gốc (đã áp nested-transform)
                double bestArea = -1;
                Point3d[] bestPts = null; // 4 điểm RECT trong hệ def gốc (đã áp nested-transform)

                ScanRectRecursive(tr, def, Matrix3d.Identity, ref has, ref best, ref bestArea, ref bestPts);
                if (!has || bestPts == null || bestPts.Length != 4) return false;

                var m = br.BlockTransform;
                // Transform 4 điểm RECT thật ra world/paperspace theo BlockTransform
                Point3d[] wpts = new Point3d[4];
                for (int i = 0; i < 4; i++) wpts[i] = bestPts[i].TransformBy(m);

                // Detect landscape/portrait theo cạnh dài nhất (tránh sai khi RECT bị rotate trong nested block)
                double maxLen = -1;
                Vector3d longEdge = Vector3d.XAxis;
                for (int i = 0; i < 4; i++)
                {
                    var a = wpts[i];
                    var b = wpts[(i + 1) % 4];
                    var v = b - a;
                    double len = v.Length;
                    if (len > maxLen)
                    {
                        maxLen = len;
                        longEdge = v;
                    }
                }
                double ax = Math.Abs(longEdge.X);
                double ay = Math.Abs(longEdge.Y);
                rectLandscape = (ax >= ay);

                double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                foreach (var p in wpts)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }

                win = new Extents2d(new Point2d(minX, minY), new Point2d(maxX, maxY));
                return true;
            }
            catch { return false; }
        }

        // Duyệt đệ quy: tìm RECT polyline trong block definition và nested block.
        // acc: transform tích luỹ từ nested block về hệ của def gốc.
        private static void ScanRectRecursive(
            Transaction tr,
            BlockTableRecord def,
            Matrix3d acc,
            ref bool has,
            ref Extents3d best,
            ref double bestArea,
            ref Point3d[] bestPts)
        {
            foreach (ObjectId eid in def)
            {
                Entity ent = null;
                try { ent = tr.GetObject(eid, OpenMode.ForRead) as Entity; } catch { ent = null; }
                if (ent == null) continue;

                // 1) RECT polyline
                var pl = ent as Polyline;
                if (pl != null)
                {
                    if (!pl.Closed) continue;
                    if (pl.NumberOfVertices != 4) continue;

                    // Cạnh thẳng (bulge=0)
                    bool ok = true;
                    try
                    {
                        for (int i = 0; i < 4; i++)
                            if (Math.Abs(pl.GetBulgeAt(i)) > 1e-9) { ok = false; break; }
                    }
                    catch { ok = false; }
                    if (!ok) continue;

                    // vuông góc + song song
                    try
                    {
                        var p0 = pl.GetPoint2dAt(0);
                        var p1 = pl.GetPoint2dAt(1);
                        var p2 = pl.GetPoint2dAt(2);
                        var p3 = pl.GetPoint2dAt(3);

                        Vector2d v01 = p1 - p0;
                        Vector2d v12 = p2 - p1;
                        Vector2d v23 = p3 - p2;
                        Vector2d v30 = p0 - p3;

                        if (v01.Length < 1e-6 || v12.Length < 1e-6 || v23.Length < 1e-6 || v30.Length < 1e-6) continue;

                        double ortho1 = Math.Abs(v01.GetNormal().DotProduct(v12.GetNormal()));
                        double ortho2 = Math.Abs(v12.GetNormal().DotProduct(v23.GetNormal()));
                        if (ortho1 > 1e-2) continue;
                        if (ortho2 > 1e-2) continue;

                        double para1 = Math.Abs(v01.GetNormal().DotProduct(v23.GetNormal()));
                        double para2 = Math.Abs(v12.GetNormal().DotProduct(v30.GetNormal()));
                        if (para1 < 0.98) continue;
                        if (para2 < 0.98) continue;
                    }
                    catch { continue; }

                    // Tính extents đúng theo 4 đỉnh RECT (không dùng GeometricExtents bbox) để tránh bị "ăn" sang khung bên cạnh
                    // khi RECT nằm trong nested block và có rotation.
                    Extents3d ex;
                    Point3d[] vpts = null;
                    try
                    {
                        vpts = new Point3d[]
                        {
                            new Point3d(pl.GetPoint2dAt(0).X, pl.GetPoint2dAt(0).Y, 0),
                            new Point3d(pl.GetPoint2dAt(1).X, pl.GetPoint2dAt(1).Y, 0),
                            new Point3d(pl.GetPoint2dAt(2).X, pl.GetPoint2dAt(2).Y, 0),
                            new Point3d(pl.GetPoint2dAt(3).X, pl.GetPoint2dAt(3).Y, 0)
                        };

                        // apply nested transform
                        for (int i = 0; i < vpts.Length; i++) vpts[i] = vpts[i].TransformBy(acc);

                        double minX = vpts.Min(p => p.X);
                        double minY = vpts.Min(p => p.Y);
                        double maxX = vpts.Max(p => p.X);
                        double maxY = vpts.Max(p => p.Y);
                        ex = new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
                    }
                    catch { continue; }

                    double w = Math.Abs(ex.MaxPoint.X - ex.MinPoint.X);
                    double h = Math.Abs(ex.MaxPoint.Y - ex.MinPoint.Y);
                    if (w <= 1e-6 || h <= 1e-6) continue;
                    double area = w * h;

                    // Nếu chỉ có 1 rectang khung in thì lấy rectang có diện tích lớn nhất
                    if (!has || area > bestArea)
                    {
                        bestArea = area;
                        best = ex;
                        bestPts = vpts;
                        has = true;
                    }

                    continue;
                }

                // 2) nested block
                var br2 = ent as BlockReference;
                if (br2 != null)
                {
                    try
                    {
                        var def2 = (BlockTableRecord)tr.GetObject(br2.BlockTableRecord, OpenMode.ForRead);
                        if (def2 == null) continue;
                        // Thứ tự nhân matrix: tích luỹ transform theo chuỗi parent -> nested.
                        // Dùng acc * br2.BlockTransform để áp transform nested đúng theo TransformBy.
                        var acc2 = acc * br2.BlockTransform;
                        ScanRectRecursive(tr, def2, acc2, ref has, ref best, ref bestArea, ref bestPts);
                    }
                    catch { }
                }
            }
        }

        private static string ResolveBlockName(Transaction tr, BlockReference br)
        {
            try
            {
                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                return btr == null ? "" : (btr.Name ?? "");
            }
            catch { return ""; }
        }

        private static Dictionary<string, string> ReadAttributesMap(Transaction tr, BlockReference br)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    var ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                    if (ar == null) continue;
                    string tag = (ar.Tag ?? "").Trim();
                    string val = (ar.TextString ?? "").Trim();
                    if (tag.Length == 0) continue;
                    dict[tag] = val;
                }
            }
            catch { }
            return dict;
        }

        private static string GetAttr(Dictionary<string, string> dict, string key)
        {
            if (dict == null || string.IsNullOrWhiteSpace(key)) return "";
            string v;
            return dict.TryGetValue(key, out v) ? (v ?? "") : "";
        }

        public void WriteBackAttributes(BlockItem it)
        {
            if (it == null) return;
            try
            {
                using (_doc.LockDocument())
                {
                    var db = _doc.Database;
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        ObjectId id;
                        // Handle string (hex)
                        var h = new Handle(Convert.ToInt64(it.Handle, 16));
                        id = db.GetObjectId(false, h, 0);

                        var br = tr.GetObject(id, OpenMode.ForWrite) as BlockReference;
                        if (br == null) { tr.Commit(); return; }

                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            var ar = tr.GetObject(aid, OpenMode.ForWrite) as AttributeReference;
                            if (ar == null) continue;
                            string t = (ar.Tag ?? "").Trim();
                            if (t.Length == 0) continue;

                            if (string.Equals(t, "MT_KH", StringComparison.OrdinalIgnoreCase))
                                ar.TextString = it.KyHieu ?? "";
                            else if (string.Equals(t, "MT_TENBANVE", StringComparison.OrdinalIgnoreCase))
                                ar.TextString = it.TenBanVe ?? "";
                            else if (string.Equals(t, "MT_TENHANGMUC", StringComparison.OrdinalIgnoreCase))
                                ar.TextString = it.HangMuc ?? "";
                        }

                        tr.Commit();
                    }
                }
            }
            catch (System.Exception ex)
            {
                try { _ed.WriteMessage("\n[SBP] Không ghi được ATT: " + ex.Message); } catch { }
            }
        }

        public HashSet<string> PromptSelectBlockHandles(Editor ed)
        {
            if (ed == null) return null;

            var opts = new PromptSelectionOptions();
            opts.MessageForAdding = "\nQuét vùng để lọc khung tên (Enter để xong): ";
            opts.AllowDuplicates = false;

            PromptSelectionResult res = null;
            try { res = ed.GetSelection(opts); } catch { res = null; }

            if (res == null || res.Status != PromptStatus.OK || res.Value == null)
                return null;

            var hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (_doc.LockDocument())
            using (var tr = _doc.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in res.Value)
                {
                    if (so == null) continue;
                    var br = tr.GetObject(so.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    try { hs.Add(br.Handle.ToString()); } catch { }
                }
                tr.Commit();
            }

            return hs.Count > 0 ? hs : null;
        }

        // UI gọi thẳng method này (giữ signature y như file cũ, nhưng không truyền Document nữa)
        public void PlotWindowToPdf(string layoutName, Extents2d win, bool rectLandscape, string pdfFile, string paperMedia, string styleSheet, bool fit)
        {
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new InvalidOperationException("Plot is busy");

            // NOTE: thao tác Plot cần lock Document để tránh eInvalidInput do context thay đổi
            using (_doc.LockDocument())
            {
                LayoutManager lm = LayoutManager.Current;
                // Chuyển đúng layout trước khi start Transaction (ổn định context)
                // Nếu không switch đúng layout thì PlotInfoValidator có thể báo eLayoutNotCurrent.
                try
                {
                    if (!string.IsNullOrWhiteSpace(layoutName))
                        lm.CurrentLayout = layoutName; // gồm cả "Model"
                }
                catch { }

                Database db = _doc.Database;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId layoutId;
                    try
                    {
                        layoutId = (!string.IsNullOrWhiteSpace(layoutName) && string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase))
                            ? lm.GetLayoutId("Model")
                            : lm.GetLayoutId(lm.CurrentLayout);
                    }
                    catch
                    {
                        layoutId = lm.GetLayoutId(lm.CurrentLayout);
                    }
                    Layout lo = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);

                    using (PlotSettings ps = new PlotSettings(lo.ModelType))
                    {
                        ps.CopyFrom(lo);
                        PlotSettingsValidator psv = PlotSettingsValidator.Current;

                        // Plot style table (CTB/STB) giống dialog Plot
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(styleSheet) && !string.Equals(styleSheet, "None", StringComparison.OrdinalIgnoreCase))
                                psv.SetCurrentStyleSheet(ps, styleSheet);
                        }
                        catch { }

                        // Fix plotter như ban đầu: DWG To PDF.pc3
                        try { psv.SetPlotConfigurationName(ps, "DWG To PDF.pc3", null); } catch { }
                        psv.RefreshLists(ps);

                        // Paper size: dùng canonical media name từ combobox (giống CAD dialog)
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(paperMedia))
                            {
                                psv.SetCanonicalMediaName(ps, paperMedia);
                                psv.RefreshLists(ps);
                            }
                        }
                        catch { /* nếu không set được media thì giữ nguyên media của layout */ }

                        psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);

                        var minPt = win.MinPoint;
                        var maxPt = win.MaxPoint;
                        bool isModel = (!string.IsNullOrWhiteSpace(layoutName) && string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase));

                        // Model cần TileMode=true để plot đúng context; Layout cần TileMode=false
                        try { db.TileMode = isModel; } catch { }

                        double w = Math.Abs(maxPt.X - minPt.X);
                        double h = Math.Abs(maxPt.Y - minPt.Y);

                        // Fix chiều bản vẽ: tự xoay theo hướng giấy (paperMedia) và hướng RECT khung in
                        try
                        {
                            bool paperLandscape = true;

                            if (!string.IsNullOrWhiteSpace(paperMedia))
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(
                                    paperMedia,
                                    @"\((\s*\d+(?:\.\d+)?)\s*x\s*(\d+(?:\.\d+)?)\s*MM\)",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                                );

                                if (m.Success)
                                {
                                    double pw = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                                    double ph = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                                    paperLandscape = (pw >= ph);
                                }
                            }

                            // Nếu khung và giấy khác hướng -> xoay 90 độ để fit
                            var rot = PlotRotation.Degrees000;
                            if (rectLandscape != paperLandscape)
                            {
                                rot = PlotRotation.Degrees090;
                            }
                            psv.SetPlotRotation(ps, rot);
                        }
                        catch { }

                        double eps = isModel ? 1e-4 : 1e-3; // paper(mm) dùng eps lớn hơn chút
                        double dx = (w < eps) ? (isModel ? 1.0 : 0.1) : 0.0;
                        double dy = (h < eps) ? (isModel ? 1.0 : 0.1) : 0.0;
                        var win2 = new Extents2d(new Point2d(minPt.X - dx, minPt.Y - dy), new Point2d(maxPt.X + dx, maxPt.Y + dy));

                        // Shrink nhẹ window để loại bỏ entity nằm đúng biên.
                        try
                        {
                            double shrink = 0.05; // mm (Layout) / đơn vị bản vẽ (Model).
                            if ((win2.MaxPoint.X - win2.MinPoint.X) > shrink * 2 && (win2.MaxPoint.Y - win2.MinPoint.Y) > shrink * 2)
                            {
                                win2 = new Extents2d(
                                    new Point2d(win2.MinPoint.X + shrink, win2.MinPoint.Y + shrink),
                                    new Point2d(win2.MaxPoint.X - shrink, win2.MaxPoint.Y - shrink)
                                );
                            }
                        }
                        catch { }

                        psv.SetPlotWindowArea(ps, win2);

                        // Bạn in tay có tick "Center the plot" -> center
                        try { psv.SetPlotCentered(ps, true); } catch { }
                        psv.SetUseStandardScale(ps, true);
                        psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);

                        PlotInfo pi = new PlotInfo();
                        pi.Layout = layoutId;
                        pi.OverrideSettings = ps;

                        PlotInfoValidator piv = new PlotInfoValidator();
                        piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                        try
                        {
                            piv.Validate(pi);
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception vex)
                        {
                            try
                            {
                                _doc.Editor.WriteMessage(
                                    "\n[SBP-VALIDATE-ERR] layout=" + (lm.CurrentLayout ?? "")
                                    + " status=" + vex.ErrorStatus
                                    + " win=[" + win2.MinPoint.X + "," + win2.MinPoint.Y + "]-[" + win2.MaxPoint.X + "," + win2.MaxPoint.Y + "]"
                                );
                            }
                            catch { }

                            throw;
                        }

                        using (PlotEngine pe = PlotFactory.CreatePublishEngine())
                        {
                            using (PlotProgressDialog ppd = new PlotProgressDialog(false, 1, true))
                            {
                                ppd.OnBeginPlot();
                                ppd.IsVisible = false;
                                try
                                {
                                    pe.BeginPlot(ppd, null);

                                    pe.BeginDocument(pi, _doc.Name, null, 1, true, pdfFile);
                                }
                                catch (Autodesk.AutoCAD.Runtime.Exception pex)
                                {
                                    try { _doc.Editor.WriteMessage("\n[SBP-PLOT-ERR] status=" + pex.ErrorStatus + " file=" + pdfFile); } catch { }
                                    throw;
                                }
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
                    }
                    tr.Commit();
                }
            }
        }

        public static string Csv(string s)
        {
            s = s ?? "";
            s = s.Replace("\"", "\"\"");
            return "\"" + s + "\"";
        }

        public static string SanitizeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "plot";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }
    }
}