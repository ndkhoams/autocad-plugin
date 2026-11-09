using System;
using System.IO;
using System.Windows.Forms;

namespace CADtools
{
    // Hộp chọn thư mục dùng API có sẵn của .NET Framework 4.8.
    internal static class FolderPicker
    {
        // Trả về đường dẫn thư mục người dùng chọn, hoặc null khi hủy/lỗi.
        // initialDirectory không phải thư mục tồn tại thì bỏ qua, để dialog tự chọn.
        public static string PickFolder(IWin32Window owner, string title, string initialDirectory)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = string.IsNullOrWhiteSpace(title) ? "Chọn thư mục" : title;
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                    dialog.SelectedPath = initialDirectory;

                return dialog.ShowDialog(owner) == DialogResult.OK
                    && !string.IsNullOrWhiteSpace(dialog.SelectedPath)
                    ? dialog.SelectedPath
                    : null;
            }
        }
    }
}
