using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcRxException = Autodesk.AutoCAD.Runtime.Exception;

namespace CADtools
{
    public static class LayoutRenamer
    {
        // Doi ten tab layout THAT trong DWG. Uu tien tim theo HANDLE (ben vung), fallback theo ten cu.
        public static bool RenameByHandle(string dwgPath, string handleStr, string originalName, string newName, out string warn)
        {
            warn = "";
            newName = (newName ?? "").Trim();
            originalName = (originalName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(newName)) { warn = "Tên layout mới rỗng."; return false; }
            if (string.Equals(newName, "Model", StringComparison.OrdinalIgnoreCase)) { warn = "Không thể đặt tên layout là 'Model'."; return false; }
            if (string.IsNullOrWhiteSpace(dwgPath)) { warn = "Không lấy được đường dẫn DWG."; return false; }

            Handle? hnd = ParseHandle(handleStr);

            Document openDoc = FindOpenDocument(dwgPath);
            if (openDoc != null)
                return RenameInOpenDoc(openDoc, hnd, originalName, newName, out warn);

            return RenameInSideDb(dwgPath, hnd, originalName, newName, out warn);
        }

        internal static Handle? ParseHandle(string handleStr)
        {
            if (string.IsNullOrWhiteSpace(handleStr)) return null;
            try { return new Handle(Convert.ToInt64(handleStr, 16)); }
            catch { return null; }
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
                            + (hnd.HasValue ? hnd.Value.ToString() : "(rong)") + "', ten cu='" + (originalName ?? "")
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
                            + (hnd.HasValue ? hnd.Value.ToString() : "(rong)") + "', ten cu='" + (originalName ?? "")
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

        // Ten layout theo handle (ho tro ca truong hop handle tro toi BlockTableRecord cua layout).
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

        // Handle (hex) cua Layout theo ten - dung de "chot" identity theo ObjectId/handle.
        internal static string GetHandleByName(Database db, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var dict = tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                    string h = "";
                    if (dict != null && dict.Contains(name))
                    {
                        var id = dict.GetAt(name);
                        var lay = tr.GetObject(id, OpenMode.ForRead) as Layout;
                        if (lay != null) h = lay.Handle.ToString();
                    }
                    tr.Commit();
                    return h;
                }
            }
            catch { return ""; }
        }

        // Mo DWG (dang mo hoac side-load read-only) va lay handle layout theo ten - dung sau khi rename de chot handle.
        internal static string GetHandleByNameInDwg(string dwgPath, string name)
        {
            try
            {
                Document openDoc = FindOpenDocument(dwgPath);
                if (openDoc != null) return GetHandleByName(openDoc.Database, name);
                if (string.IsNullOrWhiteSpace(dwgPath) || !File.Exists(dwgPath)) return "";
                using (var db = new Database(false, true))
                {
                    try { db.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndAllShare, true, null); }
                    catch { return ""; }
                    return GetHandleByName(db, name);
                }
            }
            catch { return ""; }
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

        // Cap nhat ten hien thi cua reference trong .dst (best-effort). Reference name-based nen day chi de hien thi.
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
    }

    // Xac dinh layout THAT trong DWG, tra ve (ten hien tai + handle) de dinh danh theo ObjectId/handle.
    // Uu tien: (1) handle da luu -> ten hien tai; (2) theo ten reference -> lay handle de luu lai.
    // Cache database: open doc dung truc tiep; DWG dong side-load read-only 1 lan.
    public sealed class LayoutLocator : IDisposable
    {
        private readonly Dictionary<string, Database> _sideDbs =
            new Dictionary<string, Database>(StringComparer.OrdinalIgnoreCase);

        public bool Resolve(string dwgPath, string storedHandle, string refName,
            out string liveName, out string handle)
        {
            liveName = ""; handle = "";
            try
            {
                Database db = GetDb(dwgPath);
                if (db == null) return false;

                Handle? h = LayoutRenamer.ParseHandle(storedHandle);
                if (h.HasValue)
                {
                    string nm = LayoutRenamer.GetLayoutNameByHandle(db, h.Value);
                    if (!string.IsNullOrEmpty(nm)) { liveName = nm; handle = storedHandle; return true; }
                }

                if (!string.IsNullOrWhiteSpace(refName))
                {
                    string hByName = LayoutRenamer.GetHandleByName(db, refName);
                    if (!string.IsNullOrEmpty(hByName)) { liveName = refName; handle = hByName; return true; }
                }
            }
            catch { }
            return false;
        }

        private Database GetDb(string dwgPath)
        {
            Document openDoc = LayoutRenamer.FindOpenDocument(dwgPath);
            if (openDoc != null) return openDoc.Database;

            if (string.IsNullOrWhiteSpace(dwgPath) || !File.Exists(dwgPath)) return null;
            string key = LayoutRenamer.SafeFull(dwgPath);
            Database db;
            if (!_sideDbs.TryGetValue(key, out db))
            {
                db = new Database(false, true);
                try { db.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndAllShare, true, null); }
                catch { try { db.Dispose(); } catch { } _sideDbs[key] = null; return null; }
                _sideDbs[key] = db;
            }
            return db;
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