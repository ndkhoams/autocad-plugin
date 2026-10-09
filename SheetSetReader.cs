using System;
using System.Collections.Generic;
using System.IO;
#if CAD_ACSM_R23
using AcSm = ACSMCOMPONENTS23Lib;
#elif CAD_ACSM_R25
using AcSm = ACSMCOMPONENTS25Lib;
#else
using AcSm = ACSMCOMPONENTS24Lib;
#endif

namespace CADtools
{
    // Helper to safely release COM objects
    internal static class ComHelper
    {
        internal static void Release(object comObj)
        {
            if (comObj != null && System.Runtime.InteropServices.Marshal.IsComObject(comObj))
            {
                try
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(comObj);
                }
                catch { /* Ignore COM release errors */ }
            }
        }
    }

    public class SheetInfo
    {
        public string SubsetPath = "";
        public string SheetSetName = "";
        public string Number = "";
        public string Title = "";
        public string Desc = "";
        public string LayoutName = "";
        public string OriginalLayoutName = "";
        public string LayoutHandle = ""; // handle (hex) cua layout - identity ben vung theo ObjectId
        public string DwgPath = "";
        public string Revision = "";
        public string RevisionDate = "";
        public string IssuePurpose = "";
        public Dictionary<string, string> Custom =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<string> EditableCustomKeys = null;

        public object Com; // AcSm.IAcSmSheet
        public object DbCom; // AcSm.IAcSmDatabase
        public object OwnerCom; // AcSm.IAcSmSubset or AcSm.IAcSmSheetSet
    }

    public static class SheetSetReader
    {
        // Custom property luu handle layout (tool tu quan ly) de dinh danh theo ObjectId/handle.
        public const string LayoutHandleKey = "AR_LayoutHandle";

        public static List<SheetInfo> ReadOpenSheetSets()
        {
            var result = new List<SheetInfo>();
            AcSm.AcSmSheetSetMgr mgr = null;
            AcSm.IAcSmEnumDatabase dbEnum = null;

            try
            {
                mgr = new AcSm.AcSmSheetSetMgr();
                dbEnum = mgr.GetDatabaseEnumerator();
                dbEnum.Reset();

                AcSm.IAcSmDatabase db;
                while ((db = dbEnum.Next()) != null)
                {
                    AcSm.IAcSmSheetSet ss = null;
                    try
                    {
                        ss = db.GetSheetSet();
                        if (ss == null) continue;

                        string ssName = Safe(() => ss.GetName());
                        var ssCustom = ReadCustomProps(ss.GetCustomPropertyBag());

                        // Chi doc du lieu trong DST/SSM, khong mo DWG khi khoi dong SSP.
                        CollectSheets(ss, db, ssName, ssCustom, result, "", null);
                    }
                    finally
                    {
                        ComHelper.Release(ss);
                        // Note: Don't release db here - it's still referenced in SheetInfo.DbCom
                    }
                }
            }
            finally
            {
                ComHelper.Release(dbEnum);
                ComHelper.Release(mgr);
            }

            return result;
        }

        // Doc sheet tu file DST (khong can sheet set dang mo)
        public static List<SheetInfo> ReadFromDst(string dstPath)
        {
            if (string.IsNullOrWhiteSpace(dstPath)) return new List<SheetInfo>();
            if (!File.Exists(dstPath)) throw new FileNotFoundException("Khong tim thay DST", dstPath);

            var result = new List<SheetInfo>();
            AcSm.AcSmDatabase db = null;
            AcSm.IAcSmSheetSet ss = null;

            try
            {
                db = new AcSm.AcSmDatabase();
                db.SetFileName(dstPath);
                db.LoadFromFile(dstPath);

                ss = db.GetSheetSet();
                if (ss == null) return result;

                string ssName = Safe(() => ss.GetName());
                var ssCustom = ReadCustomProps(ss.GetCustomPropertyBag());

                // Chi doc noi dung trong DST. Khong mo tung DWG de resolve layout khi load,
                // vi viec do lam cham dang ke voi sheet set co hang tram sheet.
                CollectSheets(ss, db, ssName, ssCustom, result, "", null);
            }
            finally
            {
                ComHelper.Release(ss);
                // Note: Don't release db here - it's still referenced in SheetInfo.DbCom
            }

            return result;
        }

        private static void CollectSheets(AcSm.IAcSmSubset subset, AcSm.IAcSmDatabase db, string ssName,
        Dictionary<string, string> ssCustom, List<SheetInfo> outList, string subsetPath, LayoutLocator locator)
        {
            AcSm.IAcSmEnumComponent en = null;

            try
            {
                en = subset.GetSheetEnumerator();
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
                        Dictionary<string, string> sheetCustom = ReadCustomProps(sheet.GetCustomPropertyBag());
                        si.Com = sheet;
                        si.DbCom = db;
                        si.OwnerCom = subset;

                        try
                        {
                            AcSm.IAcSmAcDbLayoutReference layRef = sheet.GetLayout();
                            if (layRef != null)
                            {
                                string refName = Safe(() => layRef.GetName());
                                si.LayoutName = refName;
                                var objRef = layRef as AcSm.IAcSmAcDbObjectReference;
                                if (objRef != null) si.DwgPath = Safe(() => objRef.GetFileName());

                                // Identity theo ObjectId/handle: uu tien handle da luu, roi den ten reference.
                                string storedHandle;
                                if (!sheetCustom.TryGetValue(LayoutHandleKey, out storedHandle)) storedHandle = "";
                                string liveName, handle;
                                if (locator != null && locator.Resolve(si.DwgPath, storedHandle, refName, out liveName, out handle))
                                {
                                    if (!string.IsNullOrEmpty(liveName)) si.LayoutName = liveName;
                                    si.LayoutHandle = handle;
                                }
                                else
                                {
                                    si.LayoutHandle = storedHandle; // giu lai neu co (du chua resolve duoc)
                                }
                            }
                        }
                        catch { }

                        si.OriginalLayoutName = si.LayoutName;

                        foreach (var kv in ssCustom) si.Custom[kv.Key] = kv.Value;
                        foreach (var kv in sheetCustom)
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
                            CollectSheets(sub, db, ssName, ssCustom, outList, p, locator);
                        }
                    }
                }
            }
            finally
            {
                ComHelper.Release(en);
            }
        }

        private static Dictionary<string, string> ReadCustomProps(AcSm.IAcSmCustomPropertyBag bag)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (bag == null) return dict;

            AcSm.IAcSmEnumProperty pe = null;
            try
            {
                pe = bag.GetPropertyEnumerator();
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
            finally
            {
                ComHelper.Release(pe);
            }

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