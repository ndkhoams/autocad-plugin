using System;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application; // tranh nham voi System.Windows.Forms.Application

[assembly: CommandClass(typeof(CADtools.CadLicenseCommands))]
namespace CADtools
{
    public class CadLicenseCommands
    {
        [CommandMethod("CDLIC")]
        public void InstallLicense()
        {
            var ed = AcadApp.DocumentManager.MdiActiveDocument.Editor;
            string key = PromptKey();
            if (string.IsNullOrEmpty(key)) { ed.WriteMessage("\nĐã hủy.\n"); return; }
            string msg;
            LicenseManager.InstallLicense(key, out msg);
            ed.WriteMessage("\n[KhoanD13@hotmail.com] " + msg + "\n");
        }

        [CommandMethod("INFOLIC")]
        public void Info()
        {
            var ed = AcadApp.DocumentManager.MdiActiveDocument.Editor;
            string info = LicenseManager.Status;
            ed.WriteMessage("\n[KhoanD13@hotmail.com] " + info + "\n");
            // Popup thong bao thong tin ban quyen (icon thay doi theo trang thai hop le).
            MessageBox.Show(
                info,
                "Thông tin bản quyền",
                MessageBoxButtons.OK,
                LicenseManager.IsValid ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        [CommandMethod("CDMAY")]
        public void Fingerprint()
        {
            var ed = AcadApp.DocumentManager.MdiActiveDocument.Editor;
            string fp = LicenseManager.GetMachineFingerprint();
            ed.WriteMessage("\n[KhoanD13@hotmail.com] Mã máy của bạn: " + fp
                + "\nGửi mã này cho người cấp key.\n");
            bool copied = false;
            try { Clipboard.SetText(fp); copied = true; ed.WriteMessage("(Đã copy vào clipboard)\n"); } catch { }
            // Popup thong bao ma may cho nguoi dung de dang copy/gui.
            MessageBox.Show(
                "Mã máy (fingerprint) của bạn:\n\n" + fp
                    + (copied ? "\n\n(Đã tự động copy vào clipboard)" : "")
                    + "\n\nGửi mã này cho người cấp key. KhoaND13@hotmail.com - Zalo: 090.450.4193",
                "DEVICE CODE",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static string PromptKey()
        {
            using (var f = new Form())
            {
                f.Text = "Nhập license key";
                f.Width = 660; f.Height = 210;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MinimizeBox = false; f.MaximizeBox = false;

                var lbl = new Label { Left = 12, Top = 12, Width = 620, Text = "Dán license key vào ô dưới:" };
                var txt = new TextBox { Left = 12, Top = 36, Width = 620, Height = 70, Multiline = true };
                var ok = new Button { Text = "Cài đặt", Left = 476, Top = 125, Width = 75, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Hủy", Left = 557, Top = 125, Width = 75, DialogResult = DialogResult.Cancel };
                f.Controls.Add(lbl); f.Controls.Add(txt); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? txt.Text.Trim() : "";
            }
        }
    }
}