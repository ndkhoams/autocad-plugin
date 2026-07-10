using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

// IExtensionApplication.Initialize() tu chay khi NETLOAD -> dung de dung Ribbon o day.
[assembly: ExtensionApplication(typeof(BatchPlotPdf.RibbonSetup))]

namespace BatchPlotPdf
{
    public class RibbonSetup : IExtensionApplication
    {
        private const string TabId = "BATCHPLOTPDF_TAB";

        public void Initialize()
        {
            // Luc NETLOAD, Ribbon co the CHUA khoi tao -> cho su kien ItemInitialized.
            if (ComponentManager.Ribbon != null)
                BuildRibbon();
            else
                ComponentManager.ItemInitialized += OnItemInitialized;
        }

        public void Terminate() { }

        private void OnItemInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            BuildRibbon();
        }

        private void BuildRibbon()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;

            // Tranh tao trung tab neu da co.
            foreach (RibbonTab t in ribbon.Tabs)
                if (t.Id == TabId) return;

            RibbonTab tab = new RibbonTab { Title = "Batch Plot PDF", Id = TabId };
            ribbon.Tabs.Add(tab);

            RibbonPanelSource src = new RibbonPanelSource { Title = "In PDF" };
            tab.Panels.Add(new RibbonPanel { Source = src });

            src.Items.Add(MakeButton("MTECH", "In theo\nSheet Set", "MTECH ",
                "Mo UserForm dat ten PDF theo Sheet Set Manager va in tung sheet."));

            src.Items.Add(MakeButton("SSMEDIT", "Quản lý\nSheet Set", "SSMEDIT ",
                "Mo bang chinh sua Number/Title/Description/Custom (va thu ghi Revision) roi luu vao Sheet Set."));
        }

        // macro ket thuc bang dau cach = Enter (chay lenh ngay).
        private static RibbonButton MakeButton(string id, string text, string macro, string tip)
        {
            var b = new RibbonButton
            {
                Id = "BATCHPLOTPDF_" + id,
                Text = text,
                ShowText = true,
                ShowImage = false,   // chua co icon; dat true + LargeImage neu muon hien icon
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = macro,
                CommandHandler = new CmdHandler()
            };
            b.ToolTip = new RibbonToolTip { Title = id, Content = tip };
            return b;
        }
    }

    // Gui macro xuong dong lenh khi bam nut.
    internal class CmdHandler : System.Windows.Input.ICommand
    {
        public event EventHandler CanExecuteChanged;
        public bool CanExecute(object parameter) { return true; }

        public void Execute(object parameter)
        {
            string macro = parameter as string;
            var rb = parameter as RibbonButton;
            if (string.IsNullOrEmpty(macro) && rb != null) macro = rb.CommandParameter as string;
            if (string.IsNullOrEmpty(macro)) return;

            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.SendStringToExecute(macro, true, false, true);
        }
    }
}