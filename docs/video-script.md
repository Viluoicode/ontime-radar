# Kịch bản video demo – OnTime Radar (~4 phút)

Mục tiêu: người xem (mentor FCAJ, nhà tuyển dụng) hiểu **vấn đề → kiến trúc → hệ thống chạy thật**
trong 4 phút. Nói chậm, mỗi cảnh một ý. Lời thoại là gợi ý – nói tự nhiên theo cách của bạn.

## 1. Chuẩn bị trước khi quay

| Khi nào | Việc | Ghi chú |
| --- | --- | --- |
| Hôm trước | `.\tools\daily-check.ps1` → tất cả ĐẠT | Replayer `Failed = 0`, DLQ = 0 |
| Trước ~40 phút | `.\tools\demo.ps1 -Email <quản lý> -NoPause` (có cảnh 4) | Để **có sẵn** email ALARM và OK – không chờ trong video |
| Trước 10 phút | Đăng nhập web bằng tài khoản **Quản lý** (trình duyệt 1) và **CSKH** (cửa sổ ẩn danh) | Tránh quay màn hình đăng nhập / mật khẩu |
| Trước 10 phút | Mở sẵn tab: README (ảnh sơ đồ), GitHub Actions (1 lần Deploy xanh), hộp thư (email cảnh báo), Cost Explorer | Đóng các tab khác, tắt thông báo |
| Trước 10 phút | Terminal: cỡ chữ ≥ 18, nền tối, thư mục repo | Chạy thử `demo.ps1 -SkipAlarm -NoPause` một lần cho "ấm" Lambda |
| Khi quay | Độ phân giải 1920×1080, thu tiếng bằng tai nghe có mic | OBS hoặc Xbox Game Bar (Win + G) |

**Phải che trước khi đăng:** email cá nhân (dùng `quanly@example.com` như ảnh trong README),
số tài khoản AWS (12 chữ số – có trong ARN, URL hàng đợi, domain Cognito), domain CloudFront nếu không
muốn công khai. Không quay `samconfig.toml` hay trang Lambda → Configuration (có `WEBHOOK_SECRET`).

## 2. Kịch bản

| Thời gian | Màn hình | Lời thoại (gợi ý) | Ghi chú quay |
| --- | --- | --- | --- |
| **0:00 – 0:30** Mở đầu | README – đoạn giới thiệu | "Shop online ở Việt Nam thường giao qua 2–4 hãng. Mỗi hãng một mã trạng thái, một trang tra cứu riêng, và shop thường chỉ biết đơn giao trễ khi khách đã phàn nàn. Em làm OnTime Radar: một hệ thống serverless trên AWS gom sự kiện từ mọi hãng về một chỗ, phát hiện giao trễ gần realtime và đo tỉ lệ giao đúng hạn theo hãng, theo khu vực." | Nói 1 câu về dữ liệu: "dữ liệu là ~99 nghìn đơn thật của bộ Olist, được phát lại như sự kiện realtime" |
| **0:30 – 1:15** Kiến trúc | `architecture.png` (phóng to) | "Có 3 luồng. **Cam**: hãng gửi webhook qua API Gateway, Lambda chuẩn hoá rồi đưa vào SQS FIFO theo từng vận đơn, Processor ghi DynamoDB và lưu sự kiện ra S3. **Xanh**: nhân viên vào website trên CloudFront, đăng nhập Cognito, tra cứu từ DynamoDB, báo cáo SLA bằng Athena. **Đỏ**: khi tỉ lệ trễ vượt 20 % trong 2 trên 3 khung 15 phút, CloudWatch gửi email." | Dùng chuột chỉ theo số 1 → 13. Không đọc tên từng icon |
| **1:15 – 1:45** CSKH tra đơn | Web **Tra cứu đơn** (tài khoản CSKH) | "Khách gọi hỏi đơn đâu. CSKH nhập mã – thấy ngay trạng thái và toàn bộ hành trình, kể cả lần giao thất bại, không cần mở trang của hãng. Tài khoản CSKH chỉ thấy 2 trang này." | Chỉ vào menu chỉ có *Tra cứu đơn* + *Cần xử lý* |
| **1:45 – 2:30** Quản lý | Web **Cần xử lý** → **Tổng quan SLA** (tài khoản Quản lý) | "*Cần xử lý* lọc đơn giao trễ, từng giao thất bại, quá hạn. *Tổng quan SLA* cho quản lý thấy tỉ lệ đúng hạn theo tuần, theo hãng, theo bang – để chọn hãng bằng số liệu thay vì cảm tính." | Bấm 1 dòng ở *Cần xử lý* để mở hành trình. Chuyển giao diện sáng/tối 1 lần |
| **2:30 – 3:00** Dữ liệu bẩn | Terminal: `demo.ps1` cảnh 2 | "Thực tế webhook đến trùng, sai thứ tự. Em gửi 8 webhook lộn xộn cho một đơn: bản gửi lại bị loại, sự kiện cũ chỉ lưu lịch sử, trạng thái không bao giờ nhảy lùi từ Đã giao." | Dừng ở bảng *Quyết định*: Applied / Rejected / Stale |
| **3:00 – 3:20** Cảnh báo | Hộp thư: email **ALARM** rồi **OK** | "Khi một hãng giao trễ hàng loạt kéo dài, hệ thống gửi email kèm link trang *Cần xử lý*; khi trở lại bình thường có email báo đã hết sự cố. Một khung xấu lẻ loi sẽ không gửi – tránh báo động giả." | Email đã quay sẵn – **che địa chỉ email** |
| **3:20 – 3:35** Phân quyền | Terminal: `demo.ps1` bằng tài khoản CSKH, cảnh 5 | "Phân quyền nằm ở API, không chỉ ẩn menu: token CSKH gọi báo cáo SLA bị trả 403." | Dòng vàng `403 Forbidden` |
| **3:35 – 3:50** CI/CD | GitHub Actions – run **Deploy** xanh | "Mỗi lần push code: chạy test, chờ em duyệt, rồi GitHub deploy lên AWS qua OIDC – không có access key nào lưu trên GitHub." | Mở run có bước *Review deployments* |
| **3:50 – 4:10** Kết | Cost Explorer → README | "Toàn bộ chạy serverless, khoảng vài USD mỗi tháng. Điều em học được nhiều nhất: xử lý dữ liệu không hoàn hảo và thiết kế cảnh báo để người ta tin nó. Link repo ở mô tả – em rất mong nhận góp ý." | Chèn link repo trên màn hình |

## 3. Sau khi quay

- Cắt đoạn chờ, tăng tốc đoạn terminal chạy lâu (×1,5).
- Phụ đề tiếng Việt (CapCut / YouTube tự tạo rồi sửa) – nhiều người xem không bật tiếng.
- Xem lại toàn bộ 1 lần **chỉ để tìm thông tin cần che** (email, số tài khoản AWS, secret).
- Đăng YouTube chế độ *Không công khai* trước, gửi 1–2 người xem thử, rồi mới chia sẻ.
