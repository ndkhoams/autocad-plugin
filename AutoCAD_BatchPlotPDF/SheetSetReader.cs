using System;
using System.Collections.Generic;
using AcSm = ACSMCOMPONENTS24Lib;   // COM AcSm: namespace CO KEM SO PHIEN BAN (vd 24 tren may nay); doi so cho khop

namespace BatchPlotPdf
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

                CollectSheets(ss, ssName, ssCustom, result);
            }
            return result;
        }

        private static void CollectSheets(AcSm.IAcSmSubset subset, string ssName,
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
                    var si = new SheetInfo
                    {
                        SheetSetName = ssName,
                        Number = Safe(() => sheet.GetNumber()),
                        Title = Safe(() => sheet.GetTitle()),
                        Desc = Safe(() => sheet.GetDesc()),
                        // Revision/issue KHONG lo ra tren IAcSmSheet (CS1061) va cung KHONG nam
                        // trong custom property bag. Chung nam tren mot interface KHAC ma coclass
                        // AcSmSheet co implement (co the la dang property get_RevisionNumber...).
                        // -> Quet MOI interface trong assembly interop AcSm, tim getter khong tham
                        // so tra ve string co ten khop tu khoa roi Invoke (CLR tu QueryInterface).
                        // Cach nay khong phu thuoc ten/phien ban typelib cu the.
                        Revision = ScanInvoke(sheet, new[] { "RevisionNumber", "Revision" }, "Date"),
                        RevisionDate = ScanInvoke(sheet, new[] { "RevisionDate" }, null),
                        IssuePurpose = ScanInvoke(sheet, new[] { "IssuePurpose", "Purpose" }, null)
                    };

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

                    // Neu khong lay duoc qua method, thu tim revision/issue trong custom properties
                    // (mot so bo Sheet Set luu cac truong nay duoi dang custom property).
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
                    if (sub != null) CollectSheets(sub, ssName, ssCustom, outList);
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

        // Revision/IssuePurpose khong lo ra qua IAcSmSheet, nhung coclass AcSmSheet thuong
        // implement chung o mot interface khac (hoac dang property). Ta quet MOI interface trong
        // assembly interop AcSm, tim method/getter parameterless tra ve string co ten khop tu
        // khoa (includeAny) va khong chua 'exclude', roi Invoke tren object -> CLR tu
        // QueryInterface; interface nao object khong support thi nem & bo qua. Nho vay khong can
        // biet chinh xac ten interface/method, khong phu thuoc phien ban typelib.
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
                        // Broaden: nhieu getter cua AcSm tra ve 'object' (variant) chu khong
                        // phai 'string' -> truoc day loc string-only nen BO SOT revision. Nhan ca hai.
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
                            // Chi nhan string hoac value type -> tranh tra ve "System.__ComObject"
                            // khi getter tra ve mot COM object (khong phai gia tri thuc).
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

        // Lay gia tri custom property dau tien khop mot trong cac ten (khong phan biet hoa thuong).
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