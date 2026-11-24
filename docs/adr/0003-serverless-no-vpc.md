# ADR-0003 – Serverless hoàn toàn, không dùng VPC

- **Trạng thái:** Đã áp dụng (từ 16/11/2026)
- **Liên quan:** [ADR-0001](0001-sqs-fifo-per-shipment.md), [ADR-0002](0002-dynamodb-single-table.md), [ADR-0004](0004-github-oidc-deploy.md)

## Bối cảnh

- Tải nhỏ và theo đợt: Replayer phát ~ 100 webhook mỗi 15 phút (~ 9.500/ngày), người dùng dashboard vài người.
- Dự án cá nhân trong thực tập: **chi phí phải thấp và dự đoán được**, không có người trực vận hành server.
- Mọi dịch vụ cần dùng (DynamoDB, SQS, S3, Athena, CloudWatch, Cognito) đều là dịch vụ có endpoint công khai của AWS,
  truy cập bằng IAM – **không có tài nguyên nào bắt buộc phải nằm trong mạng riêng** (không có database tự quản).

## Quyết định

- **Tính toán:** 3 Lambda .NET (API – Minimal API qua HTTP API Gateway; Processor – đọc SQS; Replayer – EventBridge
  Scheduler 15 phút), **arm64** (Graviton, rẻ hơn ~ 20 % so với x86 cùng thời lượng), 512 MB.
- **Không VPC:** Lambda chạy ở mạng do AWS quản lý, gọi dịch vụ AWS qua endpoint công khai, xác thực bằng IAM role.
- **Lưu trữ / phân tích:** DynamoDB on-demand, S3, Athena (trả theo dữ liệu quét, chặn 1 GB/truy vấn).
- **Website:** S3 riêng, khoá kín, phía sau CloudFront (OAC); đăng nhập Cognito.
- **Chi phí phụ được kiểm soát:** log giữ 14 ngày, throttling API 10 req/s (burst 20), Processor tối đa 2 luồng.

Bảo vệ thay cho "cách ly mạng": IAM least privilege theo từng Lambda + **permissions boundary** (ADR-0004), API Gateway
chặn token sai (401) trước khi Lambda chạy, secret cho webhook.

## Phương án đã cân nhắc

| Phương án | Vì sao không chọn |
| --- | --- |
| **Lambda trong VPC + NAT Gateway** | NAT Gateway tính tiền theo giờ chạy + theo GB, chỉ riêng phí giờ đã cao hơn nhiều so với toàn bộ hệ thống hiện tại (ước tính 3–5 USD/tháng). Không có tài nguyên private nào cần bảo vệ bằng mạng |
| **Lambda trong VPC + VPC endpoint** (không NAT) | Gateway endpoint cho S3 / DynamoDB miễn phí, nhưng SQS, CloudWatch, Athena, … cần interface endpoint – tính tiền theo giờ × số AZ × số dịch vụ. Thêm cấu hình subnet, security group mà không thêm giá trị |
| **Container (ECS Fargate) / EC2 chạy ASP.NET** | Chạy liên tục kể cả khi không có webhook → trả tiền 24/7; phải lo scale, vá OS (EC2), load balancer (có phí giờ). Hợp hơn khi tải đều và lớn |
| **Lambda x86_64** | Chạy được như nhau, đắt hơn arm64; .NET chạy tốt trên arm64 |

## Hệ quả

**Tốt**

- Trả tiền theo request; không có chi phí nền theo giờ cho compute / mạng. Không có server để vá.
- Hạ tầng ít thành phần: không subnet, route table, security group, NAT.
- Scale tự động theo tải (trong giới hạn concurrency).

**Đánh đổi / giới hạn**

- **Giới hạn đồng thời của tài khoản: 10 Lambda** (quota mặc định). Phép thử 20/11/2026: dồn 80 request → 47 lỗi 503.
  Đã giảm thiểu bằng throttling + giới hạn Processor 2 luồng; cần xin quota 1.000 khi dùng thật.
- **Cold start** của .NET khi Lambda khởi động lạnh (lần đầu / sau khi nhàn rỗi) – chưa đo riêng; độ trễ xử lý tối đa
  quan sát trong chaos test 24/11 là 519 ms (gồm cả chờ hàng đợi).
- **API Gateway HTTP API timeout 30 giây** → truy vấn Athena phải xong trong 29 s (có cache 2 lớp để ít phải chạy lại).
- **Không có lớp cách ly mạng:** nếu một Lambda bị chiếm quyền, giới hạn duy nhất là IAM. Hiện IAM còn rộng hơn cần ở
  vài chỗ (README 7b – S2); permissions boundary chặn việc leo quyền ra ngoài stack.
- Gắn với AWS (Lambda, API Gateway, DynamoDB): chuyển sang nền tảng khác phải viết lại phần hosting và truy cập dữ liệu.
  Phần nghiệp vụ (`ShipmentSla.Core`) không phụ thuộc AWS nên giữ được.
- Phát triển ở máy không có AWS thật → test dùng AWS giả trong bộ nhớ (không Docker); bằng chứng end-to-end phải chạy
  trên AWS (chaos test, `tools/daily-check.ps1`).

**Khi nào xem lại:** cần database quan hệ / tài nguyên private (→ VPC), tải đều và lớn (→ container có thể rẻ hơn),
hoặc yêu cầu bảo mật bắt buộc cách ly mạng.

## Bằng chứng

- Hệ thống chạy liên tục từ 16/11/2026 (Replayer mỗi 15 phút); kiểm tra 23/11: 11/11 mục đạt, Lambda 0 lỗi / 0 throttle trong 24 giờ
  (`tools/daily-check.ps1`).
- Chi phí: ước tính 3–5 USD/tháng – số thật lấy từ Cost Explorer (chụp trên console, không gọi API).
