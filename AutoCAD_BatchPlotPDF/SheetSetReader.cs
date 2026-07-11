using System;
using System.Collections.Generic;
using System.IO;
using AcSm = ACSMCOMPONENTS24Lib; // COM AcSm: namespace CO KEM SO PHIEN BAN (vd 24 tren may nay); doi so cho khop

namespace CADtools
{
    public class SheetInfo
    {
        public string SubsetPath = "";
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

        public List<string> EditableCustomKeys = null;

        public object Com; // AcSm.IAcSmSheet
        public object DbCom; // AcSm.IAcSmDatabase
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

                CollectSheets(ss, db, ssName, ssCustom, result, "");
            }
            return result;
        }

        // NEW: Đọc sheet từ file DST (không cần sheet set đang mở)
        public static List<SheetInfo> ReadFromDst(string dstPath)
        {
            if (string.IsNullOrWhiteSpace(dstPath)) return new List<SheetInfo>();
            if (!File.Exists(dstPath)) throw new FileNotFoundException("Không tìm thấy DST", dstPath);

            var result = new List<SheetInfo>();

            // Mở database từ file .dst (API đã thấy trong SSM PROBE)
            var db = new AcSm.AcSmDatabase();
            db.SetFileName(dstPath);
            db.LoadFromFile(dstPath);

            AcSm.IAcSmSheetSet ss = db.GetSheetSet();
            if (ss == null) return result;

            string ssName = Safe(() => ss.GetName());
            var ssCustom = ReadCustomProps(ss.GetCustomPropertyBag());

            CollectSheets(ss, db, ssName, ssCustom, result, "");
            return result;
        }

        private static void CollectSheets(AcSm.IAcSmSubset subset, AcSm.IAcSmDatabase db, string ssName,
        Dictionary<string, string> ssCustom, List<SheetInfo> outList, string subsetPath)
        {
            AcSm.IAcSmEnumComponent en = subset.GetSheetEnumerator();
            en.Reset();
            AcSm.IAcSmComponent comp;
            while ((comp = en.Next()) != null)
            {
                var sheet = comp as AcSm.IAcSmSheet;
                if (sheet != null)
                {
                    var s2 = sheet as AcSm.IAcSmSheet2;
                    var si = new SheetInfo
                    {
                        SubsetPath = subsetPath ?? "",
                        SheetSetName = ssName,
                        Number = Safe(() => sheet.GetNumber()),
                        Title = Safe(() => sheet.GetTitle()),
                        Desc = Safe(() => sheet.GetDesc()),
                        Revision = s2 == null ? "" : Safe(() => s2.GetRevisionNumber()),
                        RevisionDate = s2 == null ? "" : Safe(() => s2.GetRevisionDate()),
                        IssuePurpose = s2 == null ? "" : Safe(() => s2.GetIssuePurpose())
                    };
                    si.Com = sheet;
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
                    if (sub != null)
                    {
                        string subName = "";
                        try { subName = Safe(() => sub.GetName()); } catch { }
                        string p = string.IsNullOrWhiteSpace(subsetPath) ? subName : (subsetPath + " / " + subName);
                        CollectSheets(sub, db, ssName, ssCustom, outList, p);
                    }
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
            try { return (f == null ? "" : (f() ?? "")); } catch { return ""; }
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