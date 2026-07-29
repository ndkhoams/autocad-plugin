using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.EditorInput;
using AcSm = ACSMCOMPONENTS24Lib; // COM AcSm: doi so phien ban cho khop

namespace CADtools
{
    // Ket qua ghi nguoc (bao cao truong nao an / khong an tren dong lenh).
    public class SaveResult
    {
        public int SheetsSaved = 0;
        public int RevisionOk = 0;
        public int RevisionFail = 0;
        public List<string> Warnings = new List<string>();
    }

    public static class SheetSetWriter
    {
        // Ghi thay doi tu SheetInfo (da chinh trong form) nguoc vao .dst.
        // Gom sheet theo database de LockDb/UnlockDb dung 1 lan moi .dst.
        public static SaveResult Save(IEnumerable<SheetInfo> sheets, Editor ed)
        {
            var res = new SaveResult();

            var groups = new List<KeyValuePair<object, List<SheetInfo>>>();
            foreach (var s in (sheets ?? new List<SheetInfo>()))
            {
                if (s == null || s.Com == null) continue;
                int gi = groups.FindIndex(x => ReferenceEquals(x.Key, s.DbCom));
                if (gi < 0) groups.Add(new KeyValuePair<object, List<SheetInfo>>(s.DbCom, new List<SheetInfo> { s }));
                else groups[gi].Value.Add(s);
            }

            foreach (var grp in groups)
            {
                var db = grp.Key as AcSm.IAcSmDatabase;
                AcSm.IAcSmPersist lockObj = null;
                bool locked = false;
                try
                {
                    if (db != null)
                    {
                        try
                        {
                            lockObj = db.GetSheetSet() as AcSm.IAcSmPersist;
                            db.LockDb(lockObj);
                            locked = true;
                        }
                        catch (Exception ex) { res.Warnings.Add("Khong khoa duoc database: " + ex.Message); }
                    }

                    foreach (var s in grp.Value) SaveSheet(s, res);
                }
                finally
                {
                    if (locked)
                    {
                        try { db.UnlockDb(lockObj, true); } // UnlockDb(..., true) = commit ghi thay doi xuong .dst
                        catch (Exception ex) { res.Warnings.Add("Loi mo khoa/luu: " + ex.Message); }
                    }
                }
            }
            return res;
        }

        private static void SaveSheet(SheetInfo s, SaveResult res)
        {
            var sheet = s.Com as AcSm.IAcSmSheet;
            if (sheet == null) return;

            // 0) Layout reference (DWG path + Layout name)
            try
            {
                var layRef = sheet.GetLayout();
                if (layRef != null)
                {
                    var objRefL = layRef as AcSm.IAcSmAcDbObjectReference;
                    string dwg = s.DwgPath ?? "";
                    if (objRefL != null)
                    {
                        try { string d = objRefL.GetFileName(); if (!string.IsNullOrEmpty(d)) dwg = d; } catch { }
                    }

                    // Rename layout THAT trong DWG. Dinh danh THEO HANDLE/ObjectId (ben vung), fallback ten cu.
                    // Chi xu ly khi ten co thay doi so voi luc doc.
                    if (!string.IsNullOrEmpty(s.LayoutName) &&
                        !string.Equals(s.LayoutName, s.OriginalLayoutName ?? "", StringComparison.Ordinal))
                    {
                        try
                        {
                            string warn;
                            bool ok = LayoutRenamer.RenameByHandle(dwg, s.LayoutHandle, s.OriginalLayoutName, s.LayoutName, out warn);
                            if (ok)
                            {
                                // CHOT identity theo ObjectId: handle KHONG doi khi rename -> luu vao custom prop
                                // de cac phien sau tim layout theo handle (khong phu thuoc ten -> khong lech).
                                string hNow = s.LayoutHandle;
                                if (string.IsNullOrEmpty(hNow))
                                    hNow = LayoutRenamer.GetHandleByNameInDwg(dwg, s.LayoutName); // lay handle sau khi rename
                                if (!string.IsNullOrEmpty(hNow))
                                {
                                    s.LayoutHandle = hNow;
                                    string wH;
                                    if (!SetOneCustom(sheet, SheetSetReader.LayoutHandleKey, hNow, out wH))
                                        res.Warnings.Add("Luu handle '" + s.Title + "': " + wH);
                                }
                                // Dong bo ten hien thi trong .dst (best-effort; da co handle nen khong bat buoc).
                                string wSet;
                                LayoutRenamer.TrySetRefName(objRefL, s.LayoutName, out wSet);

                                s.OriginalLayoutName = s.LayoutName; // tranh rename lai o lan luu sau
                            }
                            else res.Warnings.Add("LayoutName '" + s.Title + "': "
                                + (string.IsNullOrEmpty(warn) ? "khong doi duoc ten layout." : warn));
                        }
                        catch (Exception ex) { res.Warnings.Add("LayoutName '" + s.Title + "': " + ex.Message); }
                    }

                    // DWG path (chi khi thay doi)
                    if (!string.IsNullOrEmpty(s.DwgPath))
                    {
                        try
                        {
                            var objRef = layRef as AcSm.IAcSmAcDbObjectReference;
                            if (objRef != null)
                            {
                                string curDwg = "";
                                try { curDwg = objRef.GetFileName() ?? ""; } catch { }
                                if (!string.Equals(curDwg, s.DwgPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    var t2 = objRef.GetType();
                                    var mi2 = t2.GetMethod("SetFileName") ?? t2.GetMethod("put_FileName") ?? t2.GetMethod("SetPath");
                                    if (mi2 != null) mi2.Invoke(objRef, new object[] { s.DwgPath ?? "" });
                                    else res.Warnings.Add("DwgPath '" + s.Title + "': COM khong co SetFileName/put_FileName()");
                                }
                            }
                        }
                        catch (Exception ex) { res.Warnings.Add("DwgPath '" + s.Title + "': " + ex.Message); }
                    }
                }
            }
            catch (Exception ex)
            {
                res.Warnings.Add("LayoutRef '" + s.Title + "': " + ex.Message);
            }

            // 1) Number / Title / Description -> setter chinh thuc (ghi chac chan).
            try { if (Safe(() => sheet.GetNumber()) != (s.Number ?? "")) sheet.SetNumber(s.Number ?? ""); }
            catch (Exception ex) { res.Warnings.Add("Number '" + s.Title + "': " + ex.Message); }
            try { if (Safe(() => sheet.GetTitle()) != (s.Title ?? "")) sheet.SetTitle(s.Title ?? ""); }
            catch (Exception ex) { res.Warnings.Add("Title '" + s.Title + "': " + ex.Message); }
            try { if (Safe(() => sheet.GetDesc()) != (s.Desc ?? "")) sheet.SetDesc(s.Desc ?? ""); }
            catch (Exception ex) { res.Warnings.Add("Desc '" + s.Title + "': " + ex.Message); }

            // 2) Custom properties (chi cot form quan ly: CONT, SHT) -> qua CustomPropertyBag.
            WriteCustomProps(sheet, s.Custom, s.EditableCustomKeys, s.Title, res);

            // 3) Revision / RevisionDate / IssuePurpose -> IAcSmSheet2 (setter chinh thuc).
            var s2 = sheet as AcSm.IAcSmSheet2;
            if (s2 != null)
            {
                bool ok = true;
                try { if (Safe(() => s2.GetRevisionNumber()) != (s.Revision ?? "")) s2.SetRevisionNumber(s.Revision ?? ""); }
                catch (Exception ex) { ok = false; res.Warnings.Add("Revision '" + s.Title + "': " + ex.Message); }
                try { if (Safe(() => s2.GetRevisionDate()) != (s.RevisionDate ?? "")) s2.SetRevisionDate(s.RevisionDate ?? ""); }
                catch (Exception ex) { ok = false; res.Warnings.Add("RevisionDate '" + s.Title + "': " + ex.Message); }
                try { if (Safe(() => s2.GetIssuePurpose()) != (s.IssuePurpose ?? "")) s2.SetIssuePurpose(s.IssuePurpose ?? ""); }
                catch (Exception ex) { ok = false; res.Warnings.Add("IssuePurpose '" + s.Title + "': " + ex.Message); }
                if (ok) res.RevisionOk++; else res.RevisionFail++;
            }
            else res.RevisionFail++;

            res.SheetsSaved++;
        }

        // Ghi 1 custom property don le (an toan, chi dong den dung key do) - dung de luu handle.
        private static bool SetOneCustom(AcSm.IAcSmSheet sheet, string key, string value, out string warn)
        {
            warn = "";
            try
            {
                var bag = sheet.GetCustomPropertyBag();
                if (bag == null) { warn = "khong lay duoc property bag"; return false; }
                AcSm.AcSmCustomPropertyValue cur = null;
                try { cur = (AcSm.AcSmCustomPropertyValue)bag.GetProperty(key); } catch { }
                if (cur != null)
                {
                    string old = "";
                    try { object o = cur.GetValue(); old = o == null ? "" : o.ToString(); } catch { }
                    if (old == (value ?? "")) return true;
                    cur.SetValue(value ?? "");
                    bag.SetProperty(key, cur);
                }
                else
                {
                    var val = new AcSm.AcSmCustomPropertyValue();
                    TryInitNew(val, sheet); // AcSm can InitNew(owner) truoc khi dung, neu khong -> NullReference
                    val.SetValue(value ?? "");
                    bag.SetProperty(key, val);
                }
                return true;
            }
            catch (Exception ex) { warn = ex.Message; return false; }
        }

        // Cap nhat custom property NGAY tren value object co san de giu nguyen flags/kieu.
        // QUAN TRONG: chi ghi cac key trong editableKeys (CONT, SHT) - la cot form quan ly.
        private static void WriteCustomProps(AcSm.IAcSmSheet sheet, Dictionary<string, string> custom,
        List<string> editableKeys, string title, SaveResult res)
        {
            if (custom == null || custom.Count == 0) return;
            if (editableKeys == null || editableKeys.Count == 0) return; // khong co cot custom -> khong dung toi bag

            AcSm.IAcSmCustomPropertyBag bag = null;
            try { bag = sheet.GetCustomPropertyBag(); }
            catch (Exception ex) { res.Warnings.Add("Custom bag '" + title + "': " + ex.Message); return; }
            if (bag == null) { res.Warnings.Add("Custom '" + title + "': khong lay duoc property bag."); return; }

            foreach (var key in editableKeys)
            {
                string value;
                if (!custom.TryGetValue(key, out value)) continue;
                value = value ?? "";
                try
                {
                    AcSm.AcSmCustomPropertyValue cur = null;
                    try { cur = (AcSm.AcSmCustomPropertyValue)bag.GetProperty(key); } catch { }

                    if (cur != null)
                    {
                        string old = "";
                        try { object o = cur.GetValue(); old = o == null ? "" : o.ToString(); } catch { }
                        if (old == value) continue; // khong doi -> bo qua, giu nguyen prop

                        cur.SetValue(value);
                        bag.SetProperty(key, cur);
                    }
                    else
                    {
                        if (value.Length == 0) continue;
                        AcSm.AcSmCustomPropertyValue val = new AcSm.AcSmCustomPropertyValue();
                        TryInitNew(val, sheet); // InitNew(owner) truoc khi dung
                        val.SetValue(value);
                        bag.SetProperty(key, val);
                    }
                }
                catch (Exception ex)
                {
                    res.Warnings.Add("Custom '" + key + "' @ '" + title + "': " + ex.Message);
                }
            }
        }

        // AcSm yeu cau InitNew(owner) cho object moi tao truoc khi SetProperty (neu khong -> NullReferenceException).
        private static void TryInitNew(object val, object owner)
        {
            if (val == null) return;
            try
            {
                var mi = val.GetType().GetMethod("InitNew");
                if (mi != null)
                {
                    var ps = mi.GetParameters();
                    if (ps.Length == 1) mi.Invoke(val, new object[] { owner });
                    else if (ps.Length == 0) mi.Invoke(val, null);
                }
            }
            catch { }
        }

        private static string Safe(Func<string> f)
        {
            try { return (f == null ? "" : (f() ?? "")); } catch { return ""; }
        }
    }
}