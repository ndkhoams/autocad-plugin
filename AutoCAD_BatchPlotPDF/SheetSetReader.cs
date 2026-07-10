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
                        // Revision/issue KHONG phai method chuan cua IAcSmSheet tren moi typelib
                        // (build bao CS1061 GetRevisionNumber/GetRevisionDate/GetPurpose). Dung
                        // late-binding (IDispatch) thu nhieu ten ham -> khong loi compile; neu
                        // khong method nao ton tai thi lat sang doc custom property bag ben duoi.
                        Revision = TryGet(sheet, "GetRevisionNumber", "GetRevision", "GetSheetRevisionNumber"),
                        RevisionDate = TryGet(sheet, "GetRevisionDate", "GetSheetRevisionDate"),
                        IssuePurpose = TryGet(sheet, "GetPurpose", "GetIssuePurpose", "GetSheetIssuePurpose")
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

        // Goi method COM theo ten qua late-binding (IDispatch). Thu lan luot cac ten; ten nao
        // ton tai & tra ve gia tri thi dung. Ten khong ton tai -> bo qua, KHONG loi compile.
        // Neu Object Browser cho thay ten dung khac, chi can them ten do vao danh sach.
        private static string TryGet(object com, params string[] methodNames)
        {
            if (com == null) return "";
            foreach (var m in methodNames)
            {
                try
                {
                    object r = com.GetType().InvokeMember(m,
                        System.Reflection.BindingFlags.InvokeMethod, null, com, null);
                    if (r != null)
                    {
                        string s = r.ToString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                catch { }
            }
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