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
                        // Cac truong revision (theo hop thoai Sheet Properties). Neu typelib khac
                        // ten ham (GetRevisionNumber/GetRevisionDate/GetPurpose), F12 kiem tra roi
                        // chinh cho khop; hoac bo dong nao khong ton tai.
                        Revision = Safe(() => sheet.GetRevisionNumber()),
                        RevisionDate = Safe(() => sheet.GetRevisionDate()),
                        IssuePurpose = Safe(() => sheet.GetPurpose())
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
    }
}