# AutoCAD Batch Plot PDF Plug-in

Plug-in AutoCAD .NET (C#) in hàng loạt layout ra PDF, hỗ trợ đặt tên file theo Sheet Set Manager.

## Lệnh

| Lệnh | Chức năng |
|------|----------|
| `BATCHPDF` | Mỗi layout trong bản vẽ hiện tại xuất ra 1 file PDF riêng. |
| `BATCHPDF1FILE` | Gộp tất cả layout thành 1 file PDF nhiều trang. |
| `BATCHPDFSSM` | Mở UserForm đặt tên file PDF theo thông tin Sheet Set Manager (số sheet, tiêu đề, tên sheet set, custom properties), in từng sheet (side-load được cả DWG khác). |

## Cấu trúc thư mục

```
AutoCAD_BatchPlotPDF/
├── BatchPlotPdf.csproj
├── BatchPlotCommands.cs        // BATCHPDF, BATCHPDF1FILE
├── SheetSetReader.cs           // Đọc Sheet Set Manager qua AcSm COM
├── PlotNamingForm.cs           // UserForm đặt tên + xem trước
└── BatchPlotSsmCommand.cs      // BATCHPDFSSM + engine in theo tên
```

## Hướng dẫn build & dùng

1. Sửa `AcadRoot` trong `.csproj` theo phiên bản AutoCAD đã cài (AutoCAD 2025+ dùng `net8.0-windows`, 2021–2024 dùng `net48`).
2. **Sheet Set Manager (COM):** chỉ thêm **MỘT** tham chiếu tới AcSm (đừng để trùng cả `COMFileReference` lẫn `COMReference`, nếu không sẽ báo `CS1760`). Cách gọn: **Dependencies → Add COM Reference → tích "AcSmComponents 1.0 Type Library"**, rồi vào Properties đặt **Embed Interop Types = False**. Namespace interop **có kèm số phiên bản** — ví dụ `ACSMCOMPONENTS24Lib` — nên sửa `using AcSm = ACSMCOMPONENTS24Lib;` trong `SheetSetReader.cs` cho khớp (đổi số nếu máy khác). Sau build VS sinh `Interop.ACSMCOMPONENTS24Lib.dll`, nhớ để cạnh DLL plug-in khi `NETLOAD`. Cảnh báo `MSB3305` marshaling thì bỏ qua được.
3. Build ra DLL → trong AutoCAD gõ `NETLOAD` để nạp.
4. Gõ `BATCHPDF`, `BATCHPDF1FILE` hoặc `BATCHPDFSSM`.

## Ghi chú

- Các phần gọi **AcSm COM** phụ thuộc phiên bản AutoCAD; nếu build báo sai chữ ký/namespace interop, chỉnh lại cho khớp.
- Máy in dùng mặc định là **DWG To PDF.pc3** (có sẵn trong AutoCAD).
- Muốn canh theo Extents thay vì page setup của layout: đổi `PlotType.Layout` → `PlotType.Extents` và `StdScale1To1` → `ScaleToFit`.
