using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(CADtools.SheetBlockPlotCommand))]

namespace CADtools
{
    public class SheetBlockPlotCommand
    {
        [CommandMethod("SBP", CommandFlags.Session)]
        public void Run()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (!LicenseManager.Ensure(doc.Editor)) return; //banquyen

            // MODELESS FIX: khong dung using(...) vi ShowModelessDialog tra ve ngay -> dispose som se crash.
            var f = new SheetBlockPlotForm(doc);
            f.FormClosed += (s, e) => { try { f.Dispose(); } catch { } };
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(f);
        }
    }
}