using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AcException = Autodesk.AutoCAD.Runtime.Exception;
using Exception = System.Exception; // tránh nhầm với Autodesk.AutoCAD.Runtime.Exception

namespace CADtools
{
    // Gom toàn bộ "magic values" về một chỗ: đổi 1 nơi, tránh gõ sai tag ATT.
    internal static class SbpConstants
    {
        public const string DefaultFramePrefix = "KHUNG_MT";

        // Tag thuộc tính (ATTRIBUTE) trên block khung tên
        public const string AttrKyHieu = "MT_KH";
        public const string AttrHangMuc = "MT_TENHANGMUC";
        public const string AttrTenBanVe = "MT_TENBANVE";

        // Cấu hình plot
        public const string PdfPlotter = "DWG To PDF.pc3";
        public const double WindowShrink = 0.05;   // mm (Layout) / đơn vị (Model)

        // Ngưỡng nhận diện RECT
        public const double BulgeTol = 1e-9;
        public const double OrthoTol = 1e-2;   // càng nhỏ càng "vuông"
        public const double ParallelTol = 0.98;   // càng gần 1 càng "song song"
        public const double MinEdgeLen = 1e-6;
    }

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

        // Regex compiled & static: parse "(841 x 594 MM)" -> tái sử dụng, không tạo mới mỗi lần in.
        private static readonly Regex PaperSizeRegex = new Regex(
            @"\((\s*\d+(?:\.\d+)?)\s*x\s*(\d+(?:\.\d+)?)\s*MM\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Log gọn: luôn an toàn, không bao giờ ném lỗi ra ngoài.
        private void Log(string msg)
        {
            try { _ed.WriteMessage("\n" + msg); } catch { }
        }

        public class BlockItem
        {
            public int Stt { get; set; } = 0;
            public string LayoutName { get; set; } = ""; // "Model" hoặc tên layout
            public string KyHieu { get; set; } = "";     // MT_KH
            public string HangMuc { get; set; } = "";     // MT_TENHANGMUC
            public string TenBanVe { get; set; } = "";    // MT_TENBANVE
            public string PdfName { get; set; } = "";     // tên file PDF sẽ xuất
            public string Handle { get; set; } = "";

            public double PosX { get; set; } = 0;
            public double PosY { get; set; } = 0;

            public Extents2d Window { get; set; }         // WCS 2D window
            public bool RectLandscape { get; set; } = true;
        }

        // ============================================================
        // 1) QUÉT BLOCK
        // ============================================================
        public List<BlockItem> CollectBlocks(string targetName, HashSet<string> windowFilterHandles)
        {
            var items = new List<BlockItem>();
            string target = (targetName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(target)) target = SbpConstants.DefaultFramePrefix;

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
            catch (Exception ex)
            {
                Log("[LỖI CollectBlocks] " + ex.Message);
            }

            EnsureUniquePdfNames(items); // chống ghi đè khi trùng KyHieu_TenBanVe
            return items;
        }

        // Chống trùng tên PDF: file thứ 2 sẽ thành ..._2.pdf thay vì đè file thứ 1.
        private static void EnsureUniquePdfNames(List<BlockItem> items)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                string baseName = Path.GetFileNameWithoutExtension(it.PdfName);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "plot";

                if (seen.TryGetValue(baseName, out int n))
                {
                    seen[baseName] = ++n;
                    it.PdfName = baseName + "_" + n + ".pdf";
                }
                else
                {
                    seen[baseName] = 1;
                }
            }
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
                if (!TryGetOuterFrameWindow(tr, br, out Extents2d win, out bool rectLandscape))
                {
                    Log("[SBP] Không tìm thấy RECT polyline khung in (kể cả nested). Bỏ qua. Handle=" + br.Handle);
                    continue;
                }

                var map = ReadAttributesMap(tr, br);
                string kyHieu = GetAttr(map, SbpConstants.AttrKyHieu);
                string hangMuc = GetAttr(map, SbpConstants.AttrHangMuc);
                string tenBanVe = GetAttr(map, SbpConstants.AttrTenBanVe);

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

        // ============================================================
        // 2) TÌM KHUNG IN (RECT) — kể cả nested block, có rotation
        // ============================================================
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
                Extents3d best = new Extents3d();
                double bestArea = -1;
                Point3d[] bestPts = null; // 4 điểm RECT trong hệ def gốc (đã áp nested-transform)

                // CHỈ quét nội dung trực tiếp của block khung tên (bỏ qua nested block = chi tiết bên trong),
                // để các chi tiết vẽ trong bản vẽ không làm sai vùng in.
                ScanRectRecursive(tr, def, Matrix3d.Identity, false, ref has, ref best, ref bestArea, ref bestPts);

                // Fallback: nếu khung in được vẽ trong nested block (cấp trên cùng không có RECT) thì mới quét đệ quy,
                // để không phá vỡ tương thích với các bản vẽ cũ.
                if (!has)
                    ScanRectRecursive(tr, def, Matrix3d.Identity, true, ref has, ref best, ref bestArea, ref bestPts);

                if (!has || bestPts == null || bestPts.Length != 4) return false;

                // Transform 4 điểm RECT thật ra world/paperspace theo BlockTransform
                var m = br.BlockTransform;
                Point3d[] wpts = new Point3d[4];
                for (int i = 0; i < 4; i++) wpts[i] = bestPts[i].TransformBy(m);

                rectLandscape = IsLandscapeByLongestEdge(wpts);

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

        // Detect landscape/portrait theo cạnh dài nhất (tránh sai khi RECT bị rotate trong nested block).
        private static bool IsLandscapeByLongestEdge(Point3d[] pts)
        {
            double maxLen = -1;
            Vector3d longEdge = Vector3d.XAxis;
            for (int i = 0; i < pts.Length; i++)
            {
                var v = pts[(i + 1) % pts.Length] - pts[i];
                if (v.Length > maxLen) { maxLen = v.Length; longEdge = v; }
            }
            return Math.Abs(longEdge.X) >= Math.Abs(longEdge.Y);
        }

        // Tìm RECT polyline khung in trong 1 block definition.
        // allowNested=false: CHỈ xét entity ở cấp hiện tại (nội dung của block khung tên) — bỏ qua chi tiết là nested block.
        // allowNested=true : cho phép đệ quy vào nested block (chỉ dùng làm fallback khi cấp trên cùng không có RECT).
        // acc: transform tích luỹ từ nested block về hệ của def gốc.
        private static void ScanRectRecursive(
            Transaction tr,
            BlockTableRecord def,
            Matrix3d acc,
            bool allowNested,
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
                    if (!IsAxisRect(pl)) continue;

                    Point3d[] vpts;
                    if (!TryGetRectExtents(pl, acc, out Extents3d ex, out vpts)) continue;

                    double w = Math.Abs(ex.MaxPoint.X - ex.MinPoint.X);
                    double h = Math.Abs(ex.MaxPoint.Y - ex.MinPoint.Y);
                    if (w <= SbpConstants.MinEdgeLen || h <= SbpConstants.MinEdgeLen) continue;
                    double area = w * h;

                    // Nếu có nhiều RECT thì lấy cái diện tích lớn nhất (khung in ngoài cùng)
                    if (!has || area > bestArea)
                    {
                        bestArea = area;
                        best = ex;
                        bestPts = vpts;
                        has = true;
                    }
                    continue;
                }

                // 2) nested block — CHỈ đệ quy khi allowNested (fallback). Mặc định bỏ qua để không "ăn" chi tiết bên trong.
                if (!allowNested) continue;

                var br2 = ent as BlockReference;
                if (br2 != null)
                {
                    try
                    {
                        var def2 = (BlockTableRecord)tr.GetObject(br2.BlockTableRecord, OpenMode.ForRead);
                        if (def2 == null) continue;
                        // Tích luỹ transform theo chuỗi parent -> nested (đúng theo TransformBy).
                        var acc2 = acc * br2.BlockTransform;
                        ScanRectRecursive(tr, def2, acc2, true, ref has, ref best, ref bestArea, ref bestPts);
                    }
                    catch { }
                }
            }
        }

        // Kiểm tra polyline có phải RECT trục-chuẩn: đóng kín, 4 đỉnh, bulge=0, vuông góc + song song.
        private static bool IsAxisRect(Polyline pl)
        {
            if (!pl.Closed) return false;
            if (pl.NumberOfVertices != 4) return false;

            try
            {
                for (int i = 0; i < 4; i++)
                    if (Math.Abs(pl.GetBulgeAt(i)) > SbpConstants.BulgeTol) return false;

                var p0 = pl.GetPoint2dAt(0);
                var p1 = pl.GetPoint2dAt(1);
                var p2 = pl.GetPoint2dAt(2);
                var p3 = pl.GetPoint2dAt(3);

                Vector2d v01 = p1 - p0, v12 = p2 - p1, v23 = p3 - p2, v30 = p0 - p3;
                if (v01.Length < SbpConstants.MinEdgeLen || v12.Length < SbpConstants.MinEdgeLen ||
                    v23.Length < SbpConstants.MinEdgeLen || v30.Length < SbpConstants.MinEdgeLen) return false;

                double ortho1 = Math.Abs(v01.GetNormal().DotProduct(v12.GetNormal()));
                double ortho2 = Math.Abs(v12.GetNormal().DotProduct(v23.GetNormal()));
                if (ortho1 > SbpConstants.OrthoTol || ortho2 > SbpConstants.OrthoTol) return false;

                double para1 = Math.Abs(v01.GetNormal().DotProduct(v23.GetNormal()));
                double para2 = Math.Abs(v12.GetNormal().DotProduct(v30.GetNormal()));
                if (para1 < SbpConstants.ParallelTol || para2 < SbpConstants.ParallelTol) return false;

                return true;
            }
            catch { return false; }
        }

        // Tính extents đúng theo 4 đỉnh RECT (không dùng GeometricExtents bbox) để tránh "ăn"
        // sang khung bên cạnh khi RECT nằm trong nested block có rotation.
        private static bool TryGetRectExtents(Polyline pl, Matrix3d acc, out Extents3d ex, out Point3d[] vpts)
        {
            ex = new Extents3d();
            vpts = null;
            try
            {
                var pts = new Point3d[]
                {
                    new Point3d(pl.GetPoint2dAt(0).X, pl.GetPoint2dAt(0).Y, 0),
                    new Point3d(pl.GetPoint2dAt(1).X, pl.GetPoint2dAt(1).Y, 0),
                    new Point3d(pl.GetPoint2dAt(2).X, pl.GetPoint2dAt(2).Y, 0),
                    new Point3d(pl.GetPoint2dAt(3).X, pl.GetPoint2dAt(3).Y, 0)
                };

                for (int i = 0; i < pts.Length; i++) pts[i] = pts[i].TransformBy(acc);

                double minX = pts.Min(p => p.X);
                double minY = pts.Min(p => p.Y);
                double maxX = pts.Max(p => p.X);
                double maxY = pts.Max(p => p.Y);

                ex = new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
                vpts = pts;
                return true;
            }
            catch { return false; }
        }

        // ============================================================
        // 3) ĐỌC / GHI ATTRIBUTE
        // ============================================================
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
            return dict.TryGetValue(key, out string v) ? (v ?? "") : "";
        }

        // Ghi ngược 1 item (giữ signature cũ cho UI).
        public void WriteBackAttributes(BlockItem it)
        {
            if (it == null) return;
            WriteBackAttributes(new[] { it });
        }

        // Ghi ngược nhiều item trong MỘT transaction (nhanh hơn nhiều so với mở tr mỗi item).
        public void WriteBackAttributes(IEnumerable<BlockItem> list)
        {
            if (list == null) return;

            try
            {
                using (_doc.LockDocument())
                using (var tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    var db = _doc.Database;
                    foreach (var it in list)
                    {
                        if (it == null) continue;
                        if (!TryParseHandle(it.Handle, out Handle h))
                        {
                            Log("[SBP] Handle không hợp lệ, bỏ qua: '" + (it.Handle ?? "") + "'");
                            continue;
                        }

                        ObjectId id;
                        try { id = db.GetObjectId(false, h, 0); }
                        catch { Log("[SBP] Không tìm được ObjectId cho handle " + it.Handle); continue; }

                        var br = tr.GetObject(id, OpenMode.ForWrite) as BlockReference;
                        if (br == null) continue;

                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            var ar = tr.GetObject(aid, OpenMode.ForWrite) as AttributeReference;
                            if (ar == null) continue;
                            string t = (ar.Tag ?? "").Trim();
                            if (t.Length == 0) continue;

                            if (string.Equals(t, SbpConstants.AttrKyHieu, StringComparison.OrdinalIgnoreCase))
                                ar.TextString = it.KyHieu ?? "";
                            else if (string.Equals(t, SbpConstants.AttrTenBanVe, StringComparison.OrdinalIgnoreCase))
                                ar.TextString = it.TenBanVe ?? "";
                            else if (string.Equals(t, SbpConstants.AttrHangMuc, StringComparison.OrdinalIgnoreCase))
                                ar.TextString = it.HangMuc ?? "";
                        }
                    }
                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                Log("[SBP] Không ghi được ATT: " + ex.Message);
            }
        }

        private static bool TryParseHandle(string s, out Handle handle)
        {
            handle = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            try
            {
                long v = Convert.ToInt64(s.Trim(), 16);
                handle = new Handle(v);
                return true;
            }
            catch { return false; }
        }

        // ============================================================
        // 4) CHỌN VÙNG LỌC
        // ============================================================
        public HashSet<string> PromptSelectBlockHandles(Editor ed)
        {
            if (ed == null) return null;

            var opts = new PromptSelectionOptions
            {
                MessageForAdding = "\nQuét vùng để lọc khung tên (Enter để xong): ",
                AllowDuplicates = false
            };

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

        // ============================================================
        // 5) XUẤT PDF — đã tách nhỏ + khôi phục state + teardown an toàn
        // ============================================================
        public void PlotWindowToPdf(string layoutName, Extents2d win, bool rectLandscape, string pdfFile, string paperMedia, string styleSheet, bool fit)
        {
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new InvalidOperationException("Plot is busy");

            // NOTE: thao tác Plot cần lock Document để tránh eInvalidInput do context thay đổi.
            using (_doc.LockDocument())
            {
                Database db = _doc.Database;
                LayoutManager lm = LayoutManager.Current;

                bool isModel = !string.IsNullOrWhiteSpace(layoutName) &&
                               string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase);

                Log("[SBP-DIAG] >> begin plot layout=" + (layoutName ?? "") + " isModel=" + isModel
                    + " rawWin=[" + F(win.MinPoint.X) + "," + F(win.MinPoint.Y) + "]-[" + F(win.MaxPoint.X) + "," + F(win.MaxPoint.Y) + "] pdf=" + (pdfFile ?? ""));

                // Lưu state global để KHÔI PHỤC sau khi in (tránh ảnh hưởng khung sau / thao tác của user).
                bool oldTileMode = db.TileMode;
                string oldLayout = lm.CurrentLayout;

                try
                {
                    // Switch đúng layout TRƯỚC khi start Transaction (tránh eLayoutNotCurrent).
                    try { if (!string.IsNullOrWhiteSpace(layoutName)) lm.CurrentLayout = layoutName; } catch { }

                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        ObjectId layoutId;
                        try
                        {
                            layoutId = isModel ? lm.GetLayoutId("Model") : lm.GetLayoutId(lm.CurrentLayout);
                        }
                        catch { layoutId = lm.GetLayoutId(lm.CurrentLayout); }

                        Layout lo = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);

                        using (PlotSettings ps = new PlotSettings(lo.ModelType))
                        {
                            try { ps.CopyFrom(lo); }
                            catch (AcException cex) { Log("[SBP-SET-ERR] step=CopyFrom status=" + cex.ErrorStatus); throw; }
                            PlotSettingsValidator psv = PlotSettingsValidator.Current;

                            ConfigurePlotSettings(psv, ps, paperMedia, styleSheet);

                            // Model cần TileMode=true; Layout cần TileMode=false.
                            try { db.TileMode = isModel; } catch { }

                            // Auto-rotate giấy theo hướng khung.
                            try { psv.SetPlotRotation(ps, ResolvePlotRotation(paperMedia, rectLandscape)); } catch { }

                            Extents2d win2 = NormalizePlotWindow(win, isModel);

                            // [DIAG] Ghi lại window để soi nguyên nhân eInvalidInput (in ra cả khi thành công).
                            Log("[SBP-DIAG] layout=" + (layoutName ?? "") + " isModel=" + isModel
                                + " win=[" + F(win.MinPoint.X) + "," + F(win.MinPoint.Y) + "]-[" + F(win.MaxPoint.X) + "," + F(win.MaxPoint.Y) + "]"
                                + " win2=[" + F(win2.MinPoint.X) + "," + F(win2.MinPoint.Y) + "]-[" + F(win2.MaxPoint.X) + "," + F(win2.MaxPoint.Y) + "]"
                                + " w=" + F(win2.MaxPoint.X - win2.MinPoint.X) + " h=" + F(win2.MaxPoint.Y - win2.MinPoint.Y));

                            // PHẢI set vùng window TRƯỚC, rồi mới SetPlotType(Window):
                            // nếu ps chưa có window hợp lệ, SetPlotType(Window) sẽ ném eInvalidInput.
                            SafeSet("SetPlotWindowArea", () => psv.SetPlotWindowArea(ps, win2));
                            SafeSet("SetPlotType", () => psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window));

                            // In tay có tick "Center the plot".
                            try { psv.SetPlotCentered(ps, true); } catch { }
                            SafeSet("SetUseStandardScale", () => psv.SetUseStandardScale(ps, true));
                            SafeSet("SetStdScaleType", () => psv.SetStdScaleType(ps, StdScaleType.ScaleToFit));

                            PlotInfo pi = new PlotInfo { Layout = layoutId, OverrideSettings = ps };

                            var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
                            try { piv.Validate(pi); }
                            catch (AcException vex)
                            {
                                Log("[SBP-VALIDATE-ERR] layout=" + (lm.CurrentLayout ?? "")
                                    + " status=" + vex.ErrorStatus
                                    + " win=[" + win2.MinPoint.X + "," + win2.MinPoint.Y + "]-["
                                    + win2.MaxPoint.X + "," + win2.MaxPoint.Y + "]");
                                throw;
                            }

                            ExecutePlot(pi, pdfFile);
                        }
                        tr.Commit();
                    }
                }
                finally
                {
                    // Luôn khôi phục, kể cả khi lỗi giữa chừng.
                    try { db.TileMode = oldTileMode; } catch { }
                    try { lm.CurrentLayout = oldLayout; } catch { }
                }
            }
        }

        // Plotter (DWG To PDF) + style table (CTB/STB) + paper size.
        private void ConfigurePlotSettings(PlotSettingsValidator psv, PlotSettings ps, string paperMedia, string styleSheet)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(styleSheet) &&
                    !string.Equals(styleSheet, "None", StringComparison.OrdinalIgnoreCase))
                    psv.SetCurrentStyleSheet(ps, styleSheet);
            }
            catch { }

            try { psv.SetPlotConfigurationName(ps, SbpConstants.PdfPlotter, null); } catch { }
            psv.RefreshLists(ps);

            try
            {
                if (!string.IsNullOrWhiteSpace(paperMedia))
                {
                    psv.SetCanonicalMediaName(ps, paperMedia);
                    psv.RefreshLists(ps);
                }
            }
            catch { /* không set được media thì giữ nguyên media của layout */ }
        }

        // Nếu khung và giấy khác hướng -> xoay 90° để fit.
        private static PlotRotation ResolvePlotRotation(string paperMedia, bool rectLandscape)
        {
            bool paperLandscape = true;
            if (!string.IsNullOrWhiteSpace(paperMedia))
            {
                var m = PaperSizeRegex.Match(paperMedia);
                if (m.Success)
                {
                    double pw = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double ph = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    paperLandscape = (pw >= ph);
                }
            }
            return rectLandscape != paperLandscape ? PlotRotation.Degrees090 : PlotRotation.Degrees000;
        }

        // Nới eps khi window quá mỏng + shrink nhẹ để loại entity nằm đúng biên.
        private static Extents2d NormalizePlotWindow(Extents2d win, bool isModel)
        {
            // Chống window bị đảo (minPoint > maxPoint) -> tránh eInvalidInput ở SetPlotWindowArea/SetPlotType.
            double sminX = Math.Min(win.MinPoint.X, win.MaxPoint.X);
            double smaxX = Math.Max(win.MinPoint.X, win.MaxPoint.X);
            double sminY = Math.Min(win.MinPoint.Y, win.MaxPoint.Y);
            double smaxY = Math.Max(win.MinPoint.Y, win.MaxPoint.Y);

            var minPt = new Point2d(sminX, sminY);
            var maxPt = new Point2d(smaxX, smaxY);

            double w = Math.Abs(maxPt.X - minPt.X);
            double h = Math.Abs(maxPt.Y - minPt.Y);

            double eps = isModel ? 1e-4 : 1e-3; // paper(mm) dùng eps lớn hơn chút
            double dx = (w < eps) ? (isModel ? 1.0 : 0.1) : 0.0;
            double dy = (h < eps) ? (isModel ? 1.0 : 0.1) : 0.0;

            var win2 = new Extents2d(
                new Point2d(minPt.X - dx, minPt.Y - dy),
                new Point2d(maxPt.X + dx, maxPt.Y + dy));

            double shrink = SbpConstants.WindowShrink;
            if ((win2.MaxPoint.X - win2.MinPoint.X) > shrink * 2 &&
                (win2.MaxPoint.Y - win2.MinPoint.Y) > shrink * 2)
            {
                win2 = new Extents2d(
                    new Point2d(win2.MinPoint.X + shrink, win2.MinPoint.Y + shrink),
                    new Point2d(win2.MaxPoint.X - shrink, win2.MaxPoint.Y - shrink));
            }
            return win2;
        }

        // Vòng đời PlotEngine với teardown ĐẢM BẢO (tránh AutoCAD kẹt ở trạng thái plotting).
        private void ExecutePlot(PlotInfo pi, string pdfFile)
        {
            using (PlotEngine pe = PlotFactory.CreatePublishEngine())
            using (PlotProgressDialog ppd = new PlotProgressDialog(false, 1, true))
            {
                bool plotBegun = false, docBegun = false, pageBegun = false;
                ppd.OnBeginPlot();
                ppd.IsVisible = false;

                try
                {
                    pe.BeginPlot(ppd, null);
                    plotBegun = true;

                    pe.BeginDocument(pi, _doc.Name, null, 1, true, pdfFile);
                    docBegun = true;

                    PlotPageInfo ppi = new PlotPageInfo();
                    pe.BeginPage(ppi, pi, true, null);
                    pageBegun = true;

                    pe.BeginGenerateGraphics(null);
                    pe.EndGenerateGraphics(null);
                }
                catch (AcException pex)
                {
                    Log("[SBP-PLOT-ERR] status=" + pex.ErrorStatus + " file=" + pdfFile);
                    throw;
                }
                finally
                {
                    // Đóng theo đúng thứ tự ngược, nuốt lỗi teardown để không che lỗi gốc.
                    try { if (pageBegun) pe.EndPage(null); } catch { }
                    try { if (docBegun) pe.EndDocument(null); } catch { }
                    try { if (plotBegun) pe.EndPlot(null); } catch { }
                    try { ppd.OnEndPlot(); } catch { }
                }
            }
        }

        // ============================================================
        // Helpers
        // ============================================================
        // Chạy 1 lệnh Set* của PlotSettingsValidator, log rõ step nào ném eInvalidInput rồi rethrow.
        private void SafeSet(string step, Action act)
        {
            try { act(); }
            catch (AcException ex)
            {
                Log("[SBP-SET-ERR] step=" + step + " status=" + ex.ErrorStatus);
                throw;
            }
        }

        private static string F(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
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