using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcRxException = Autodesk.AutoCAD.Runtime.Exception;

namespace CADtools
{
    // Đổi tên LAYOUT thật trong DWG mà Sheet Set trỏ tới.
    // Sheet Set (.dst) chỉ lưu 1 THAM CHIẾU (handle + đường dẫn DWG) đến layout;
    // gọi COM SetName KHÔNG đổi được tab layout trong bản vẽ. Phải mở DWG, tìm layout
    // theo handle (fallback theo tên cũ) rồi rename qua LayoutManager, sau đó lưu DWG.
    public static class LayoutRenamer
    {
        public static bool RenameByHandle(string dwgPath, string handleStr, string originalName, string newName, out string warn)
        {
            warn = "";
            newName = (newName ?? "").Trim();
            originalName = (originalName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(newName)) { warn = "Tên layout mới rỗng."; return false; }
            if (string.Equals(newName, "Model", StringComparison.OrdinalIgnoreCase)) { warn = "Không thể đặt tên layout là 'Model'."; return false; }
            if (string.IsNullOrWhiteSpace(dwgPath)) { warn = "Không lấy được đường dẫn DWG."; return false; }

            Handle? hnd = null;
            if (!string.IsNullOrWhiteSpace(handleStr))
            {
                try { hnd = new Handle(Convert.ToInt64(handleStr, 16)); }
                catch { hnd = null; }
            }

            // 1) DWG đang mở trong phiên AutoCAD hiện tại -> rename trực tiếp (live, user tự lưu DWG).
            Document openDoc = FindOpenDocument(dwgPath);
            if (openDoc != null)
                return RenameInOpenDoc(openDoc, hnd, originalName, newName, out warn);

            // 2) DWG đóng -> side-load, rename, SaveAs (ghi đè file).
            return RenameInSideDb(dwgPath, hnd, originalName, newName, out warn);
        }

        internal static Document FindOpenDocument(string dwgPath)
        {
            try
            {
                string full = SafeFull(dwgPath);
                if (string.IsNullOrEmpty(full)) return null;
                foreach (Document d in AcApp.DocumentManager)
                {
                    string dn = "";
                    try { dn = SafeFull(d.Name); } catch { dn = ""; }
                    if (!string.IsNullOrEmpty(dn) && string.Equals(dn, full, StringComparison.OrdinalIgnoreCase))
                        return d;
                }
            }
            catch { }
            return null;
        }

        internal static string SafeFull(string p)
        {
            try { return string.IsNullOrWhiteSpace(p) ? "" : Path.GetFullPath(p); } catch { return p ?? ""; }
        }

        private static bool RenameInOpenDoc(Document doc, Handle? hnd, string originalName, string newName, out string warn)
        {
            warn = "";
            var db = doc.Database;
            var prev = HostApplicationServices.WorkingDatabase;
            using (doc.LockDocument())
            {
                try
                {
                    string oldName = ResolveOldName(db, hnd, originalName);
                    if (oldName == null)
                    {
                        warn = "Không tìm thấy layout trong DWG đang mở. handle='"
                            + (hnd.HasValue ? hnd.Value.ToString() : "(rỗng)") + "', tên cũ='" + (originalName ?? "")
                            + "', DWG='" + SafeFull(doc.Name) + "'. Layout có trong DWG: " + ListLayoutNames(db);
                        return false;
                    }
                    if (string.Equals(oldName, newName, StringComparison.Ordinal)) return true;

                    HostApplicationServices.WorkingDatabase = db;
                    LayoutManager.Current.RenameLayout(oldName, newName);
                    return true;
                }
                catch (AcRxException ex) { warn = ex.Message; return false; }
                catch (System.Exception ex) { warn = ex.Message; return false; }
                finally { HostApplicationServices.WorkingDatabase = prev; }
            }
        }

        private static bool RenameInSideDb(string dwgPath, Handle? hnd, string originalName, string newName, out string warn)
        {
            warn = "";
            if (!File.Exists(dwgPath)) { warn = "Không tìm thấy DWG: " + dwgPath; return false; }

            var prev = HostApplicationServices.WorkingDatabase;
            try
            {
                using (var db = new Database(false, true))
                {
                    try { db.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndWriteNoShare, false, null); }
                    catch (System.Exception ex) { warn = "Không mở được DWG (có thể đang mở ở nơi khác): " + ex.Message; return false; }

                    db.CloseInput(true);

                    string oldName = ResolveOldName(db, hnd, originalName);
                    if (oldName == null)
                    {
                        warn = "Không tìm thấy layout trong DWG (đóng). handle='"
                            + (hnd.HasValue ? hnd.Value.ToString() : "(rỗng)") + "', tên cũ='" + (originalName ?? "")
                            + "', DWG='" + dwgPath + "'. Layout có trong DWG: " + ListLayoutNames(db);
                        return false;
                    }
                    if (string.Equals(oldName, newName, StringComparison.Ordinal)) return true;

                    HostApplicationServices.WorkingDatabase = db;
                    try { LayoutManager.Current.RenameLayout(oldName, newName); }
                    finally { HostApplicationServices.WorkingDatabase = prev; }

                    try { db.SaveAs(dwgPath, db.OriginalFileVersion); }
                    catch (System.Exception ex) { warn = "Đổi tên OK nhưng lưu DWG lỗi: " + ex.Message; return false; }
                }
                return true;
            }
            catch (AcRxException ex) { warn = ex.Message; return false; }
            catch (System.Exception ex) { warn = ex.Message; return false; }
            finally { HostApplicationServices.WorkingDatabase = prev; }
        }

        // Tìm tên layout HIỆN TẠI (tên cũ) để đưa vào RenameLayout: ưu tiên theo handle, fallback theo tên cũ.
        private static string ResolveOldName(Database db, Handle? hnd, string originalName)
        {
            if (hnd.HasValue)
            {
                string byHandle = GetLayoutNameByHandle(db, hnd.Value);
                if (!string.IsNullOrEmpty(byHandle)) return byHandle;
            }
            if (!string.IsNullOrWhiteSpace(originalName) && LayoutExists(db, originalName))
                return originalName;
            return null;
        }

        internal static string GetLayoutNameByHandle(Database db, Handle hnd)
        {
            try
            {
                ObjectId id = db.GetObjectId(false, hnd, 0);
                if (id.IsNull || !id.IsValid) return null;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    var lay = obj as Layout;
                    if (lay == null)
                    {
                        var btr = obj as BlockTableRecord;
                        if (btr != null && !btr.LayoutId.IsNull)
                            lay = tr.GetObject(btr.LayoutId, OpenMode.ForRead) as Layout;
                    }
                    string nm = lay == null ? null : lay.LayoutName;
                    tr.Commit();
                    return nm;
                }
            }
            catch { return null; }
        }

        private static bool LayoutExists(Database db, string name)
        {
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var dict = tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                    bool has = dict != null && dict.Contains(name);
                    tr.Commit();
                    return has;
                }
            }
            catch { return false; }
        }

        // Liet ke ten cac layout (tru Model) trong DWG - dung de chan doan khi khong tim thay layout can rename.
        internal static string ListLayoutNames(Database db)
        {
            try
            {
                var names = new List<string>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var dict = tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                    if (dict != null)
                    {
                        foreach (DBDictionaryEntry e in dict)
                        {
                            if (!string.Equals(e.Key, "Model", StringComparison.OrdinalIgnoreCase))
                                names.Add(e.Key);
                        }
                    }
                    tr.Commit();
                }
                return names.Count == 0 ? "(khong co)" : string.Join(" | ", names);
            }
            catch (System.Exception ex) { return "(loi doc: " + ex.Message + ")"; }
        }

        // Cap nhat ten tham chieu layout ben trong Sheet Set (.dst) cho khop ten tab moi.
        // Reference cua Sheet Set nay NAME-BASED (khong co handle) -> sau khi rename tab PHAI ghi
        // lai ten vao reference, neu khong .dst se lech voi DWG.
        internal static bool TrySetRefName(object objRef, string newName, out string warn)
        {
            warn = "";
            if (objRef == null) { warn = "reference null"; return false; }
            try
            {
                var mi = objRef.GetType().GetMethod("SetName", new Type[] { typeof(string) });
                if (mi != null) { mi.Invoke(objRef, new object[] { newName ?? "" }); return true; }
            }
            catch (System.Exception ex) { warn = ex.Message; }
            try
            {
                objRef.GetType().InvokeMember("SetName",
                    System.Reflection.BindingFlags.InvokeMethod, null, objRef, new object[] { newName ?? "" });
                return true;
            }
            catch (System.Exception ex) { if (string.IsNullOrEmpty(warn)) warn = ex.Message; }
            if (string.IsNullOrEmpty(warn)) warn = "COM khong co SetName()";
            return false;
        }

        // Liet ke method (loc theo tu khoa) cua doi tuong COM - de kham pha API resolve/handle.
        internal static string DumpMethods(object o)
        {
            try
            {
                if (o == null) return "(null)";
                var names = new List<string>();
                foreach (var m in o.GetType().GetMethods())
                {
                    string n = m.Name;
                    if (n.IndexOf("Handle", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Resolve", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("ObjectId", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var ps = m.GetParameters();
                        var pt = new List<string>();
                        foreach (var p in ps) pt.Add(p.ParameterType.Name);
                        string sig = n + "(" + string.Join(",", pt) + ")";
                        if (!names.Contains(sig)) names.Add(sig);
                    }
                }
                return names.Count == 0 ? "(khong co method khop)" : string.Join(" ; ", names);
            }
            catch (System.Exception ex) { return "(loi dump: " + ex.Message + ")"; }
        }
    }

    // Đọc tên layout THẬT (live) từ DWG theo handle, có cache để không mở lại cùng 1 DWG.
    // Dùng khi ĐỌC Sheet Set: GetName() của reference có thể là tên CŨ (cache trong .dst),
    // không phản ánh tên tab layout thật sau khi đã rename.
    public sealed class LayoutNameResolver : IDisposable
    {
        private readonly Dictionary<string, Database> _sideDbs =
            new Dictionary<string, Database>(StringComparer.OrdinalIgnoreCase);

        public string Resolve(string dwgPath, string handleStr, string fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(handleStr)) return fallback;
                Handle hnd;
                try { hnd = new Handle(Convert.ToInt64(handleStr, 16)); }
                catch { return fallback; }

                // 1) DWG đang mở -> đọc live từ document database.
                Document openDoc = LayoutRenamer.FindOpenDocument(dwgPath);
                if (openDoc != null)
                {
                    string nmOpen = LayoutRenamer.GetLayoutNameByHandle(openDoc.Database, hnd);
                    return string.IsNullOrEmpty(nmOpen) ? fallback : nmOpen;
                }

                // 2) DWG đóng -> side-load read-only (cache theo đường dẫn).
                if (string.IsNullOrWhiteSpace(dwgPath) || !File.Exists(dwgPath)) return fallback;
                string key = LayoutRenamer.SafeFull(dwgPath);
                Database db;
                if (!_sideDbs.TryGetValue(key, out db))
                {
                    db = new Database(false, true);
                    try { db.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndAllShare, true, null); }
                    catch { try { db.Dispose(); } catch { } _sideDbs[key] = null; return fallback; }
                    _sideDbs[key] = db;
                }
                if (db == null) return fallback;
                string nm = LayoutRenamer.GetLayoutNameByHandle(db, hnd);
                return string.IsNullOrEmpty(nm) ? fallback : nm;
            }
            catch { return fallback; }
        }

        public void Dispose()
        {
            foreach (var kv in _sideDbs)
            {
                try { if (kv.Value != null) kv.Value.Dispose(); } catch { }
            }
            _sideDbs.Clear();
        }
    }
}