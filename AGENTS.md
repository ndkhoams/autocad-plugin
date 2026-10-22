# AGENTS.md — CADtools (autocad-plugin)

Hướng dẫn cho AI coding agent (Muse Code / Muse) làm việc trong repo này.
Đọc hết file này trước khi sửa bất kỳ dòng code nào. Ngôn ngữ trao đổi với chủ dự án: **tiếng Việt**.

## 1. Dự án là gì

- Plugin AutoCAD viết bằng **C#**, tên sản phẩm **CADtools**: quản lý Sheet Set, in hàng loạt theo block/layout, đổi tên layout, quản lý license, ribbon riêng.
- Solution mở bằng **Visual Studio bản đầy đủ** (không phải VS Code): `SheetPrinter/CADtools.slnx`, project `SheetPrinter/CADtools.csproj`.
- Chủ dự án là kỹ sư kết cấu, tự build trong Visual Studio và test trực tiếp trong AutoCAD. Mọi thay đổi phải build sạch và chạy được trong AutoCAD thật mới tính là xong.

## 2. Cấu trúc repo — BẮT BUỘC GIỮ NGUYÊN

- Root chỉ có: `.gitignore`, `AutoCad_SSP.iso`, `SheetPrinter/`.
- **Toàn bộ file .cs nằm trong `SheetPrinter/`** (thư mục duy nhất). Không tạo thư mục mới ở root, không khôi phục thư mục `Test/` (đã xóa hẳn từ 2026-10-11).
- Mọi sửa code nhắm `SheetPrinter/*.cs`.

## 3. Bản đồ module (head kiểm chứng: bac4ddc)

| File | Vai trò |
|---|---|
| `LicenseManager.cs`, `CadLicenseCommands.cs` | License: kiểm tra/kích hoạt bản quyền, lệnh CAD của module license |
| `SheetSetReader.cs`, `SheetSetWriter.cs`, `SheetSetProbe.cs` | Đọc/ghi/khảo sát Sheet Set (.dst) qua COM |
| `CadToolsSheetSetCommand.cs` | Lệnh chính liên quan Sheet Set |
| `SheetBlockPlotCommand.cs`, `SheetBlockPlotLogic.cs`, `SheetBlockPlotForm.UI.cs` | In theo block: lệnh + logic + giao diện form |
| `PlotNamingForm.cs`, `SheetSetSelectionForm.cs` | Form đặt tên file in, form chọn sheet |
| `LayoutRenamer.cs` | Đổi tên layout hàng loạt |
| `RibbonSetup.cs` | Dựng ribbon CADtools |
| `UpdateCommands.cs`, `UpdateBuildTimestamp.ps1` | Lệnh cập nhật + script đóng dấu thời gian build |

## 4. Luật làm việc — BẮT BUỘC

1. **Xác nhận đúng hàm/lệnh bị lỗi từ dữ liệu thật** (log, thao tác user mô tả, bản vẽ/sheet set thật) trước khi sửa. Không sửa theo phỏng đoán — loạt fix in ấn 2026-10 đã phải trả giá vì sửa sai hàm.
2. **COM phải release đủ mọi nhánh**: mọi đối tượng COM (Sheet Set, AutoCAD objects...) phải `Marshal.ReleaseComObject` / dispose ở **tất cả** các nhánh, kể cả nhánh Save và nhánh lỗi. Việc còn sót đã biết: `deletedSheets` chưa release COM trong nhánh Save của SheetSetWriter — khi chạm vào file đó phải xử lý luôn.
3. Khi fix một lỗi, quét **cả lớp lỗi** tương tự (mọi lệnh/form song song) trong cùng vòng, không fix điểm rồi dừng.
4. Không đổi tên lệnh CAD, tên class public, cấu trúc menu/ribbon và định dạng file .dst nếu không được yêu cầu rõ — user đang dùng quen và có bản vẽ thật phụ thuộc.
5. Không báo "xong" khi chưa build sạch trong Visual Studio và (nếu là logic in/sheet set) chưa test trong AutoCAD. Nêu rõ đã kiểm chứng tới đâu, phần nào chưa test được.
6. `SheetPrinter/CADtools.csproj.user` đang bị track nhầm — khi được yêu cầu dọn repo: `git rm --cached` một lần, đừng xóa file của user.

## 5. Git & môi trường máy chủ dự án

- **Luôn lấy bản mới nhất từ GitHub trước khi sửa** — chủ dự án có thể đã tự sửa khác bản local. Lệch lớn thì báo trước, đừng sửa đè.
- Git trên máy chủ dự án **không có trong PATH** — thao tác git qua Git UI tích hợp của Visual Studio (Git Changes), đừng hướng dẫn chạy lệnh git CLI. Riêng terminal tích hợp của Visual Studio thì dùng được cho Muse Code (xem hướng dẫn kèm repo của chủ dự án).
- Build bằng Visual Studio (F6 / Ctrl+Shift+B), không dùng `dotnet build` qua CLI làm chuẩn kết luận.
- Chủ dự án đã duyệt trước việc push commit lên repo này — push file text dạng văn bản thuần, và fetch lại từ remote đối chiếu trước khi kết luận khớp.
