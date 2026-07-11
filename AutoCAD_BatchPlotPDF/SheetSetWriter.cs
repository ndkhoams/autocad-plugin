using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.EditorInput;
using AcSm = ACSMCOMPONENTS24Lib;   // COM AcSm: doi so phien ban cho khop

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
            foreach (var s in sheets)
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
                        try { db.UnlockDb(lockObj); }   // UnlockDb ghi thay doi xuong .dst
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
            if (editableKeys == null || editableKeys.Count == 0) return;   // khong co cot custom -> khong dung toi bag

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
                        if (old == value) continue;   // khong doi -> bo qua, giu nguyen prop

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

        // Quet moi interface trong assembly interop AcSm, tim setter 1-tham-so-string co ten khop
        // includeAny (bat dau Set.../put_...) va khong chua exclude, roi Invoke. True neu it nhat 1 setter chay.
        private static bool ScanInvokeSet(object com, string[] includeAny, string exclude, string value)
        {
            if (com == null) return false;
            bool done = false;
            try
            {
                var asm = typeof(AcSm.IAcSmSheet).Assembly;
                foreach (Type t in asm.GetExportedTypes())
                {
                    if (!t.IsInterface) continue;
                    foreach (var m in t.GetMethods())
                    {
                        var ps = m.GetParameters();
                        if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;
                        if (m.ReturnType != typeof(void)) continue;
                        string n = m.Name;
                        bool looksSetter = n.StartsWith("Set", StringComparison.OrdinalIgnoreCase)
                            || n.StartsWith("put_", StringComparison.OrdinalIgnoreCase);
                        if (!looksSetter) continue;
                        if (!string.IsNullOrEmpty(exclude) &&
                            n.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        bool inc = false;
                        foreach (var k in includeAny)
                            if (n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { inc = true; break; }
                        if (!inc) continue;
                        try { m.Invoke(com, new object[] { value ?? "" }); done = true; }
                        catch { }
                    }
                }
            }
            catch { }
            return done;
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }
    }
}