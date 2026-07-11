using System;
using System.Collections.Generic;
using AcSm = ACSMCOMPONENTS24Lib;   // COM AcSm: namespace CO KEM SO PHIEN BAN (vd 24 tren may nay); doi so cho khop

namespace CADtools
{
    public class SheetInfo
    {
        public string SheetSetName = "";
        public string Number = "";
        public string Title = "";
        public string Desc = "";
        public string LayoutName = "";
        public string DwgPath = "";
        public string Revision = "";
        public string RevisionDate = "";
        public string IssuePurpose = "";
        public Dictionary<string, string> Custom =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Chi cac custom key nay duoc ghi nguoc (form dat = _whitelist: CONT, SHT).
        // Neu null -> KHONG ghi custom nao (tranh dung vao property cap Sheet Set khac -> mat/hong).
        public List<string> EditableCustomKeys = null;

        // Tham chieu COM song de ghi nguoc vao .dst (dat boi SheetSetReader.ReadOpenSheetSets).
        public object Com;      // AcSm.IAcSmSheet
        public object DbCom;    // AcSm.IAcSmDatabase
    }

    public static class SheetSetReader
    {
        public static List<SheetInfo> ReadOpenSheetSets()
        {
            var result = new List<SheetInfo>();
            AcSm.AcSmSheetSetMgr mgr = new AcSm.AcSmSheetSetMgr();

            AcSm.IAcSmEnumDatabase dbEnum = mgr.GetDatabaseEnumerator();
            dbEnum.Reset();
            AcSm.IAcSmDatabase db;
            while ((db = dbEnum.Next()) != null)
            {
                AcSm.IAcSmSheetSet ss = db.GetSheetSet();
                if (ss == null) continue;

                string ssName = Safe(() => ss.GetName());
                var ssCustom = ReadCustomProps(ss.GetCustomPropertyBag());

                CollectSheets(ss, db, ssName, ssCustom, result);
            }
            return result;
        }

        private static void CollectSheets(AcSm.IAcSmSubset subset, AcSm.IAcSmDatabase db, string ssName,
            Dictionary<string, string> ssCustom, List<SheetInfo> outList)
        {
            AcSm.IAcSmEnumComponent en = subset.GetSheetEnumerator();
            en.Reset();
            AcSm.IAcSmComponent comp;
            while ((comp = en.Next()) != null)
            {
                var sheet = comp as AcSm.IAcSmSheet;
                if (sheet != null)
                {
                    // Revision/RevisionDate/IssuePurpose nam tren IAcSmSheet2 (xac dinh bang SSMPROBE).
                    var s2 = sheet as AcSm.IAcSmSheet2;
                    var si = new SheetInfo
                    {
                        SheetSetName = ssName,
                        Number = Safe(() => sheet.GetNumber()),
                        Title = Safe(() => sheet.GetTitle()),
                        Desc = Safe(() => sheet.GetDesc()),
                        Revision = s2 == null ? "" : Safe(() => s2.GetRevisionNumber()),
                        RevisionDate = s2 == null ? "" : Safe(() => s2.GetRevisionDate()),
                        IssuePurpose = s2 == null ? "" : Safe(() => s2.GetIssuePurpose())
                    };
                    si.Com = sheet;   // giu tham chieu COM de ghi nguoc
                    si.DbCom = db;

                    try
                    {
                        AcSm.IAcSmAcDbLayoutReference layRef = sheet.GetLayout();
                        if (layRef != null)
                        {
                            si.LayoutName = Safe(() => layRef.GetName());
                            var objRef = layRef as AcSm.IAcSmAcDbObjectReference;
                            if (objRef != null)
                                si.DwgPath = Safe(() => objRef.GetFileName());
                        }
                    }
                    catch { }

                    foreach (var kv in ssCustom) si.Custom[kv.Key] = kv.Value;
                    foreach (var kv in ReadCustomProps(sheet.GetCustomPropertyBag()))
                        si.Custom[kv.Key] = kv.Value;

                    if (string.IsNullOrEmpty(si.Revision))
                        si.Revision = FromCustom(si.Custom, "Revision", "RevisionNumber",
                            "Sheet revision number", "Revision Number");
                    if (string.IsNullOrEmpty(si.RevisionDate))
                        si.RevisionDate = FromCustom(si.Custom, "RevisionDate",
                            "Sheet revision date", "Revision Date");
                    if (string.IsNullOrEmpty(si.IssuePurpose))
                        si.IssuePurpose = FromCustom(si.Custom, "IssuePurpose", "Purpose",
                            "Sheet issue purpose", "Issue Purpose");

                    outList.Add(si);
                }
                else
                {
                    var sub = comp as AcSm.IAcSmSubset;
                    if (sub != null) CollectSheets(sub, db, ssName, ssCustom, outList);
                }
            }
        }

        private static Dictionary<string, string> ReadCustomProps(AcSm.IAcSmCustomPropertyBag bag)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (bag == null) return dict;
            try
            {
                AcSm.IAcSmEnumProperty pe = bag.GetPropertyEnumerator();
                pe.Reset();
                string name;
                // QUAN TRONG: pe.Next tra ve 'out AcSmCustomPropertyValue' (COCLASS), KHONG phai
                // interface IAcSmCustomPropertyValue -> khai bao bang interface se bao CS1503.
                AcSm.AcSmCustomPropertyValue val;
                pe.Next(out name, out val);
                while (!string.IsNullOrEmpty(name))
                {
                    object v = null;
                    try { v = val.GetValue(); } catch { }
                    dict[name] = v == null ? "" : v.ToString();
                    name = null; val = null;
                    pe.Next(out name, out val);
                }
            }
            catch { }
            return dict;
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }

        // Quet MOI interface trong assembly interop AcSm, tim method/getter parameterless tra ve
        // string/object co ten khop tu khoa (includeAny) va khong chua 'exclude', roi Invoke tren
        // object -> CLR tu QueryInterface; interface nao object khong support thi nem & bo qua.
        private static string ScanInvoke(object com, string[] includeAny, string exclude)
        {
            if (com == null) return "";
            try
            {
                var asm = typeof(AcSm.IAcSmSheet).Assembly;
                foreach (Type t in asm.GetExportedTypes())
                {
                    if (!t.IsInterface) continue;
                    foreach (var m in t.GetMethods())
                    {
                        if (m.GetParameters().Length != 0) continue;
                        if (m.ReturnType != typeof(string) && m.ReturnType != typeof(object)) continue;
                        string n = m.Name;
                        if (!string.IsNullOrEmpty(exclude) &&
                            n.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        bool inc = false;
                        foreach (var k in includeAny)
                            if (n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { inc = true; break; }
                        if (!inc) continue;
                        try
                        {
                            object r = m.Invoke(com, null);
                            if (r != null && (r is string || r.GetType().IsValueType))
                            {
                                string s = r.ToString();
                                if (!string.IsNullOrEmpty(s)) return s;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return "";
        }

        private static string FromCustom(Dictionary<string, string> custom, params string[] keys)
        {
            if (custom == null) return "";
            foreach (var k in keys)
            {
                string v;
                if (custom.TryGetValue(k, out v) && !string.IsNullOrEmpty(v)) return v;
            }
            return "";
        }
    }
}