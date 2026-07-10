using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;
using AcSm = ACSMCOMPONENTS24Lib;

[assembly: CommandClass(typeof(BatchPlotPdf.SheetSetProbe))]

namespace BatchPlotPdf
{
    // SSMPROBE: do tim noi luu Revision/RevisionDate/IssuePurpose tren may hien tai.
    public class SheetSetProbe
    {
        [CommandMethod("SSMPROBE", CommandFlags.Session)]
        public void Probe()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var sb = new StringBuilder();
            try
            {
                List<SheetInfo> sheets = SheetSetReader.ReadOpenSheetSets();
                sb.AppendLine("=== SSM PROBE ===");
                sb.AppendLine("So sheet doc duoc: " + sheets.Count);
                sb.AppendLine();

                var first = FirstSheet(sheets);
                if (first != null && first.Com != null)
                {
                    var sheet = first.Com as AcSm.IAcSmSheet;
                    sb.AppendLine("### Sheet dau tien: " + first.Number + " - " + first.Title);
                    sb.AppendLine();

                    sb.AppendLine("--- Custom property bag (cap sheet) ---");
                    DumpBag(sheet != null ? sheet.GetCustomPropertyBag() : null, sb);
                    sb.AppendLine();

                    sb.AppendLine("--- Getter COM tra ve gia tri (loc Rev/Purpose/Issue) ---");
                    DumpGetters(first.Com, sb);
                    sb.AppendLine();

                    sb.AppendLine("--- Method/setter COM trong typelib (loc Rev/Purpose/Issue) ---");
                    DumpMethods(sb);
                }
                else
                {
                    sb.AppendLine("Khong co sheet nao (mo Sheet Set truoc).");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("LOI: " + ex);
            }

            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "ssm_probe.txt");
            try
            {
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                ed.WriteMessage("\nDa xuat: " + path);
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
            }
            catch (Exception ex) { ed.WriteMessage("\nKhong ghi duoc file: " + ex.Message); }
        }

        private static SheetInfo FirstSheet(List<SheetInfo> sheets)
        {
            foreach (var s in sheets) if (s != null && s.Com != null) return s;
            return null;
        }

        private static bool Match(string n)
        {
            return n.IndexOf("Rev", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Purpose", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Issue", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Liet ke moi custom property (ten + gia tri + flags).
        private static void DumpBag(AcSm.IAcSmCustomPropertyBag bag, StringBuilder sb)
        {
            if (bag == null) { sb.AppendLine("(khong co bag)"); return; }
            try
            {
                AcSm.IAcSmEnumProperty pe = bag.GetPropertyEnumerator();
                pe.Reset();
                string name; AcSm.AcSmCustomPropertyValue val;
                pe.Next(out name, out val);
                int c = 0;
                while (!string.IsNullOrEmpty(name))
                {
                    string v = "";
                    try { object o = val.GetValue(); v = o == null ? "" : o.ToString(); } catch { }
                    object flags = null; try { flags = val.GetFlags(); } catch { }
                    sb.AppendLine("  [" + name + "] = \"" + v + "\"  (flags=" + flags + ")");
                    c++; name = null; val = null;
                    pe.Next(out name, out val);
                }
                if (c == 0) sb.AppendLine("(bag rong)");
            }
            catch (Exception ex) { sb.AppendLine("(loi doc bag: " + ex.Message + ")"); }
        }

        // Goi moi getter parameterless (ten khop Rev/Purpose/Issue) tren object COM song, in gia tri.
        private static void DumpGetters(object com, StringBuilder sb)
        {
            try
            {
                var asm = typeof(AcSm.IAcSmSheet).Assembly;
                foreach (Type t in asm.GetExportedTypes())
                {
                    if (!t.IsInterface) continue;
                    foreach (var m in t.GetMethods())
                    {
                        if (m.GetParameters().Length != 0) continue;
                        if (m.ReturnType == typeof(void)) continue;
                        if (!Match(m.Name)) continue;
                        try
                        {
                            object r = m.Invoke(com, null);
                            sb.AppendLine("  " + t.Name + "." + m.Name + "() -> \"" + (r == null ? "<null>" : r.ToString()) + "\"");
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("(loi getter: " + ex.Message + ")"); }
        }

        // Liet ke moi method (ten khop Rev/Purpose/Issue) trong typelib kem chu ky de tim dung setter.
        private static void DumpMethods(StringBuilder sb)
        {
            try
            {
                var asm = typeof(AcSm.IAcSmSheet).Assembly;
                foreach (Type t in asm.GetExportedTypes())
                {
                    if (!t.IsInterface) continue;
                    foreach (var m in t.GetMethods())
                    {
                        if (!Match(m.Name)) continue;
                        var ps = m.GetParameters();
                        var args = new List<string>();
                        foreach (var p in ps) args.Add(p.ParameterType.Name + " " + p.Name);
                        sb.AppendLine("  " + t.Name + "." + m.Name
                            + "(" + string.Join(", ", args.ToArray()) + ") : " + m.ReturnType.Name);
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("(loi method: " + ex.Message + ")"); }
        }
    }
}