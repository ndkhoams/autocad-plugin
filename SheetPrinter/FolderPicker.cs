using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CADtools
{
    // Hộp chọn thư mục dùng IFileDialog thật của Windows (FOS_PICKFOLDERS),
    // thay cho mẹo OpenFileDialog + tên file giả trước đây.
    //
    // Vì sao bỏ mẹo cũ: mẹo đó suy ra thư mục từ tên file giả trong ô "File name".
    // Chỉ cần ô đó bị xóa/trống, hoặc thư mục muốn chọn là chính thư mục đang mở
    // (ví dụ Desktop mở từ khung điều hướng) mà không kèm tên file giả,
    // là nút Open không trả về thư mục đó được — người dùng bắt buộc phải chui
    // vào một thư mục con mới chọn được. Hộp thoại thư mục thật không có ô tên file:
    // mở tới đâu bấm nút chọn là lấy đúng thư mục đó, kể cả Desktop.
    // Nhãn nút chọn theo ngôn ngữ của Windows (ví dụ "Select Folder").
    //
    // Không cần thêm package: chỉ dùng COM có sẵn của Windows (Vista trở lên),
    // chạy được cả net48 (R23/R24) lẫn net8.0-windows (R25). Mọi đối tượng COM
    // đều được ReleaseComObject trong finally, kể cả nhánh hủy/lỗi.
    // Dialog chạy trên UI thread (STA) của AutoCAD; gọi từ thread khác sẽ trả null.
    internal static class FolderPicker
    {
        private const uint FOS_PICKFOLDERS = 0x20;
        private const uint FOS_FORCEFILESYSTEM = 0x40;
        private const uint FOS_PATHMUSTEXIST = 0x800;

        // Trả về đường dẫn thư mục người dùng chọn, hoặc null khi hủy/lỗi.
        // initialDirectory không phải thư mục tồn tại thì bỏ qua, để dialog tự chọn.
        public static string PickFolder(IWin32Window owner, string title, string initialDirectory)
        {
            IFileOpenDialog dialog = null;
            IShellItem resultItem = null;
            IShellItem folderItem = null;
            try
            {
                dialog = (IFileOpenDialog)new FileOpenDialogRCW();
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                if (!string.IsNullOrWhiteSpace(title)) dialog.SetTitle(title);
                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    try
                    {
                        Guid shellItemGuid = typeof(IShellItem).GUID;
                        SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, ref shellItemGuid, out folderItem);
                        if (folderItem != null) dialog.SetFolder(folderItem);
                    }
                    catch { /* thư mục ban đầu không dùng được -> để dialog tự chọn */ }
                }

                IntPtr parent = owner != null ? owner.Handle : IntPtr.Zero;
                int hr = dialog.Show(parent);
                if (hr != 0) return null; // Hủy (0x800704C7) hoặc lỗi: coi như không chọn

                dialog.GetResult(out resultItem);
                if (resultItem == null) return null;
                resultItem.GetDisplayName(SIGDN.FILESYSPATH, out string path);
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch { return null; } // Mọi lỗi COM/dialog đều coi như không chọn, không làm sập form
            finally
            {
                if (resultItem != null) { try { Marshal.ReleaseComObject(resultItem); } catch { } }
                if (folderItem != null) { try { Marshal.ReleaseComObject(folderItem); } catch { } }
                if (dialog != null) { try { Marshal.ReleaseComObject(dialog); } catch { } }
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [In] ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        private enum SIGDN : uint
        {
            FILESYSPATH = 0x80058000
        }

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5B9-60F82A20C6C3")]
        private class FileOpenDialogRCW
        {
        }

        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // IModalWindow
            [PreserveSig]
            int Show(IntPtr parent);

            // IFileDialog (đúng thứ tự vtable; chỉ gọi vài hàm nhưng phải đủ slot)
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName(string pszName);
            void GetFileName(out string pszName);
            void SetTitle(string pszTitle);
            void SetOkButtonLabel(string pszText);
            void SetFileNameLabel(string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension(string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);

            // IFileOpenDialog (không dùng, chỉ giữ đúng vtable)
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
