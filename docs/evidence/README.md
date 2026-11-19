# Evidence – bằng chứng khi nộp bài

Thư mục này **chỉ lưu trên máy** (bị `.gitignore` chặn, trừ file này) vì ảnh chụp có thể lộ email,
số tài khoản AWS, link CloudFront. Khi nộp: nén cả thư mục `docs/evidence/` (zip) hoặc tải lên Drive.

**Quy ước:** `NN-ten-ngan.png|txt` – số đầu cho biết nhóm. Trước khi nộp, **che** email cá nhân và
số tài khoản AWS (12 chữ số) trên ảnh. File `.txt` do script tạo đã tự che số tài khoản.

Tạo nhanh các file `.txt`:

```powershell
.\tools\daily-check.ps1 -SaveTo docs\evidence                     # 10 mục sức khoẻ hệ thống
.\tools\daily-check.ps1 -SaveTo docs\evidence -AuthMatrix         # + bảng phân quyền 401/403/200 (hỏi 2 tài khoản)
```

## Danh sách

### 0x – Kiến trúc
| ✓ | File | Chứng minh | Cách chụp |
| --- | --- | --- | --- |
| ☐ | `01-architecture.png` | Thiết kế tổng thể | Mở `docs/diagrams/architecture.drawio` bằng extension draw.io → Export PNG |
| ☐ | `02-cloudformation-stack.png` | Toàn bộ hạ tầng là code (IaC) | Console → CloudFormation → `shipment-sla` → tab **Resources** |

### 1x – Chức năng (website)
| ✓ | File | Chứng minh | Cách chụp |
| --- | --- | --- | --- |
| ☐ | `10-overview.png` | Báo cáo SLA theo hãng / bang / tuần | Website → đăng nhập Quản lý → **Tổng quan SLA** |
| ☐ | `11-lookup-timeline.png` | Tra 1 đơn, dòng thời gian, sự kiện bị chặn có giải thích | **Cần xử lý** → bấm 1 đơn |
| ☐ | `12-attention.png` | Danh sách đơn trễ / giao thất bại | **Cần xử lý** → chọn "Từng giao thất bại" |
| ☐ | `13-dark-mode.png` | Giao diện tối | Nút **Giao diện tối** ở góc dưới trái |
| ☐ | `14-mobile.png` | Hiển thị trên điện thoại | Mở link CloudFront trên điện thoại, hoặc F12 → Ctrl+Shift+M |
| ☐ | `15-cskh-menu.png` | Phân quyền trên giao diện (CSKH không thấy Tổng quan) | Đăng xuất → đăng nhập tài khoản CSKH |
| ☐ | `16-login.png` | Đăng nhập Cognito | Trang đăng nhập (không chụp khi đang gõ mật khẩu) |
| ☐ | `17-demo-run.png` | 5 cảnh giá trị thực tế | `.\tools\demo.ps1 -Email <quản lý>` → chụp terminal |

### 2x – Bảo mật
| ✓ | File | Chứng minh | Cách tạo |
| --- | --- | --- | --- |
| ☐ | `daily-check-*.txt` (có `-AuthMatrix`) | Không token 401 · CSKH bị 403 ở SLA · Quản lý 200 | Lệnh ở đầu trang |
| ☐ | `21-security-headers.png` | CSP, HSTS, X-Frame-Options… | F12 → Network → bấm request trang chủ → **Response Headers** |
| ☐ | `22-s3-private.png` | Bucket web khoá kín | Console → S3 → bucket `…webbucket…` → **Permissions** (Block all public access = On) |

### 3x – Vận hành
| ✓ | File | Chứng minh | Cách chụp |
| --- | --- | --- | --- |
| ☐ | `daily-check-*.txt` | Hệ thống khoẻ: Replayer, DLQ, event lake, alarm, API, web | Lệnh ở đầu trang (nên lưu vài ngày khác nhau) |
| ☐ | `30-replayer-logs.png` | Chạy mỗi 15 phút, `Failed = 0` | CloudWatch → Log groups → `/aws/lambda/…ReplayerFunction…` |
| ☐ | `31-alarms.png` | 2 alarm (đơn trễ, DLQ) | CloudWatch → **Alarms** |
| ☐ | `32-alarm-email.png` | Cảnh báo tới email thật | Email "ALARM: … LateDeliveryAlarm …" (che địa chỉ email) |
| ☐ | `33-sqs-fifo.png` | Hàng đợi FIFO + DLQ | Console → SQS |

### 4x – Phân tích dữ liệu
| ✓ | File | Chứng minh | Cách chụp |
| --- | --- | --- | --- |
| ☐ | `40-athena-query.png` | SQL trên event lake | Athena → workgroup `shipment-sla-wg` → chạy câu SQL mẫu trong README mục 5b |
| ☐ | `41-s3-event-lake.png` | Dữ liệu chia theo ngày `dt=` | S3 → bucket `…databucket…` → `events/` |

### 5x – Chi phí (mỗi tuần 1 ảnh)
| ✓ | File | Chứng minh | Cách chụp |
| --- | --- | --- | --- |
| ☐ | `50-cost-week1.png` … | Chi phí thật theo dịch vụ | Billing → **Cost Explorer** → Group by **Service**, khoảng 7 ngày |
| ☐ | `55-credits.png` | Credit còn lại | Billing → **Credits** |

### 6x – CI/CD
| ✓ | File | Chứng minh | Cách chụp |
| --- | --- | --- | --- |
| ☐ | `60-github-actions.png` | Build + 67 test + kiểm tra template tự động | GitHub → repo → tab **Actions** |
