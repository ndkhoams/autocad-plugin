using System;
using System.IO;
using System.Windows.Forms;

namespace CADtools
{
    // Hộp chọn thư mục hiển thị bằng giao diện OpenFileDialog quen thuộc của Windows.
    internal static class FolderPicker
    {
        // Ký tự không hiển thị nhưng giúp OpenFileDialog luôn có tên tạm để
        // nút Open lấy được thư mục hiện tại khi ô File name nhìn như trống.
        private const string FolderMarker = "\u200B";

        // Trả về đường dẫn thư mục người dùng chọn, hoặc null khi hủy/lỗi.
        // initialDirectory không phải thư mục tồn tại thì bỏ qua, để dialog tự chọn.
        public static string PickFolder(IWin32Window owner, string title, string initialDirectory)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = string.IsNullOrWhiteSpace(title) ? "Chọn thư mục" : title;
                dialog.Filter = "All Files (*.*)|*.*";
                dialog.Multiselect = false;
                dialog.CheckFileExists = false;
                dialog.ValidateNames = false;
                dialog.FileName = FolderMarker;
                dialog.RestoreDirectory = true;

                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                    dialog.FileName = FolderMarker;
                }

                if (dialog.ShowDialog(owner) != DialogResult.OK)
                    return null;

                string selectedPath = dialog.FileName;
                if (string.IsNullOrWhiteSpace(selectedPath))
                    return Directory.Exists(initialDirectory) ? initialDirectory : null;
                if (File.Exists(selectedPath))
                    return null;

                selectedPath = selectedPath.Replace(FolderMarker, "");
                if (string.IsNullOrWhiteSpace(selectedPath))
                    return Directory.Exists(initialDirectory) ? initialDirectory : null;

                // Người dùng bấm Open tại thư mục hiện tại: FileName là tên giả
                // nằm trong thư mục đó, nên lấy thư mục cha làm kết quả.
                string selectedDirectory = Directory.Exists(selectedPath)
                    ? selectedPath
                    : Path.GetDirectoryName(selectedPath);

                return !string.IsNullOrWhiteSpace(selectedDirectory) && Directory.Exists(selectedDirectory)
                    ? selectedDirectory
                    : null;
            }
        }
    }
}
