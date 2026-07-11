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
            // Lưu ý: COM API tuỳ phiên bản có thể KHÔNG expose setter -> sẽ warning nếu không ghi được.
            try
            {
                var layRef = sheet.GetLayout();
                if (layRef != null)
                {
                    // Layout name
                    if (!string.IsNullOrEmpty(s.LayoutName))
                    {
                        try
                        {
                            var mi = layRef.GetType().GetMethod("SetName");
                            if (mi != null) mi.Invoke(layRef, new object[] { s.LayoutName ?? "" });
                            else res.Warnings.Add("LayoutName '" + s.Title + "': COM không có SetName()");
                        }
                        catch (Exception ex) { res.Warnings.Add("LayoutName '" + s.Title + "': " + ex.Message); }
                    }

                    // DWG path
                    if (!string.IsNullOrEmpty(s.DwgPath))
                    {
                        try
                        {
                            var objRef = layRef as AcSm.IAcSmAcDbObjectReference;
                            if (objRef != null)
                            {
                                // Thử các tên setter phổ biến (COM interop có thể expose khác nhau)
                                var t2 = objRef.GetType();
                                var mi2 = t2.GetMethod("SetFileName") ?? t2.GetMethod("put_FileName") ?? t2.GetMethod("SetPath");
                                if (mi2 != null) mi2.Invoke(objRef, new object[] { s.DwgPath ?? "" });
                                else res.Warnings.Add("DwgPath '" + s.Title + "': COM không có SetFileName/put_FileName()");
                            }
                            else
                            {
                                res.Warnings.Add("DwgPath '" + s.Title + "': LayoutRef không cast được sang IAcSmAcDbObjectReference");
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

            // 3) Revision / RevisionDate / IssuePurpose -> IAcSmSheet2 (setter chinh thuc, SSMPROBE xac dinh).
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

        // Cap nhat custom property NGAY tren value object co san de giu nguyen flags/kieu.
        // QUAN TRONG: chi ghi cac key trong editableKeys (CONT, SHT) - la cot form quan ly.
        // KHONG lap qua toan bo custom (co ca property cap Sheet Set nhu Client/Project) vi ghi
        // de len chung o cap sheet se lam MAT/hong cac custom property khac tren sheet.
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
                    // GetProperty co the tra ve interface -> cast ve coclass van bien dich & chay.
                    AcSm.AcSmCustomPropertyValue cur = null;
                    try { cur = (AcSm.AcSmCustomPropertyValue)bag.GetProperty(key); } catch { }

                    if (cur != null)
                    {
                        string old = "";
                        try { object o = cur.GetValue(); old = o == null ? "" : o.ToString(); } catch { }
                        if (old == value) continue; // khong doi -> bo qua, giu nguyen prop

                        // Sua thang tren object co san (giu flags/kieu) roi ghi lai -> on dinh, khong mat prop.
                        cur.SetValue(value);
                        bag.SetProperty(key, cur);
                    }
                    else
                    {
                        // Prop chua co o cap sheet -> chi tao moi khi that su co gia tri (tranh tao rac).
                        if (value.Length == 0) continue;
                        AcSm.AcSmCustomPropertyValue val = new AcSm.AcSmCustomPropertyValue();
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

        private static string Safe(Func<string> f)
        {
            try { return (f == null ? "" : (f() ?? "")); } catch { return ""; }
        }
    }
}