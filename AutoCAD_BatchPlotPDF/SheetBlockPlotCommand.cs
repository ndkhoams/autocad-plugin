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

            using (var f = new SheetBlockPlotForm(doc))
            {
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(f);
            }
        }
    }
}