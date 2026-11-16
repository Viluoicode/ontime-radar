# Shipment SLA Service

**Multi-carrier shipment tracking & delivery-SLA backend on AWS (serverless, .NET 10).**

Nhận webhook trạng thái từ nhiều hãng vận chuyển (GHN / GHTK / J&T – định dạng mô phỏng),
chuẩn hóa về 1 mô hình trạng thái, xử lý **đúng thứ tự từng vận đơn**, lưu timeline,
phát hiện **đơn giao trễ** gần realtime và lưu toàn bộ sự kiện để phân tích SLA.
Traffic thật hằng ngày đến từ **Replayer** phát lại ~99k đơn hàng thật của bộ dữ liệu Olist.

→ Value, người dùng, khách hàng mục tiêu: [docs/product.md](docs/product.md)

```
EventBridge Scheduler (15') → Lambda Replayer ── reads ──> S3 dataset/chunks/*.jsonl
                                    │ POST /carriers/{carrier}/webhook  (X-Webhook-Secret)
Carriers ──────────────────────────>│
                              API Gateway (HTTP API) → Lambda API (.NET 10 Minimal API)
                                    │ normalize (adapter per carrier) → SQS FIFO (group = shipment)
                                    ▼                                   └─ DLQ (after 5 tries) → alarm
                              Lambda Processor: state machine → DynamoDB (STATE + EVENT history)
                                    ├─ S3 events/dt=YYYY-MM-DD/*.jsonl  (event lake → Glue table → Athena)
                                    └─ CloudWatch metric LateDeliveries → Alarm → SNS email
Ops / CSKH ── GET /shipments/{carrier}/{tracking} ──> Lambda API → DynamoDB (1 Query = state + timeline)
Quản lý   ── GET /sla/summary?days=30&groupBy=carrier|region ──> Lambda API → Athena (workgroup, giới hạn 1 GB/query)
```

## Cấu trúc thư mục

| Đường dẫn | Nội dung |
| --- | --- |
| `src/ShipmentSla.Core` | Nghiệp vụ thuần (không phụ thuộc AWS): model, **state machine**, adapter 3 hãng, simulator |
| `src/ShipmentSla.Infrastructure` | Truy cập DynamoDB (single-table, transaction, idempotency) |
| `src/ShipmentSla.Api` | Minimal API chạy trên Lambda (webhook + tra cứu) |
| `src/ShipmentSla.Processor` | Lambda đọc SQS FIFO → DynamoDB + S3 + CloudWatch metric |
| `src/ShipmentSla.Replayer` | Lambda phát lại đơn Olist thành webhook |
| `tests/ShipmentSla.Core.Tests` | xUnit: state machine, adapter, round-trip simulator |
| `tools/prepare_olist.py` | Chuyển CSV Olist → `chunks/*.jsonl` cho Replayer |
| `tools/sample-data/` | 6 đơn **giả lập** đúng định dạng Olist để test trước khi tải Kaggle |
| `template.yaml` | Toàn bộ hạ tầng (AWS SAM / CloudFormation), gồm Glue database/table + Athena workgroup |
| `.github/workflows/ci.yml` | CI: build + test .NET, `sam validate --lint` |

## 0. Cài đặt (1 lần)

| Công cụ | Kiểm tra |
| --- | --- |
| .NET SDK **10** | `dotnet --list-sdks` |
| AWS CLI v2 (đã `aws configure` / SSO, region `ap-southeast-1`) | `aws sts get-caller-identity` |
| AWS SAM CLI (bản mới – cần hỗ trợ runtime `dotnet10`) | `sam --version` |
| Amazon.Lambda.Tools | `dotnet tool install -g Amazon.Lambda.Tools` |
| Python 3.10+ | `python --version` |

> Vì sao .NET 10: runtime `dotnet8` của Lambda đã **deprecated từ 10/11/2026**; `dotnet10` là bản LTS hiện tại.

## 1. Build & test ở máy (không cần AWS)

```powershell
cd shipment-sla-service
dotnet build
dotnet test        # state machine + 3 adapter + round-trip simulator
```

## 2. Deploy lên AWS

```powershell
Copy-Item samconfig.example.toml samconfig.toml
sam build
sam deploy --guided
#   WebhookSecret      : chuỗi ngẫu nhiên ≥ 16 ký tự (ghi lại)
#   AlertEmail         : email nhận cảnh báo
#   ReplayState        : DISABLED   (bật sau khi test xong)
#   OrdersPerRun       : 20
#   LateAlarmThreshold : 5
#   Save arguments to configuration file: Y
```

Sau khi deploy: **mở email, bấm Confirm subscription** của SNS. Ghi lại các Outputs
(`ApiUrl`, `DataBucketName`, `TableName`, `ReplayerFunctionName`).

## 3. Smoke test API

```powershell
$api    = "<ApiUrl>"
$secret = "<WebhookSecret>"
$h      = @{ "X-Webhook-Secret" = $secret }

Invoke-RestMethod "$api/health"

# Hãng GHN báo đã lấy hàng, rồi giao trễ 1 ngày so với dự kiến (19/11)
Invoke-RestMethod -Method Post "$api/carriers/ghn/webhook" -Headers $h -ContentType "application/json" -InFile tools/sample-webhook-ghn.json
Invoke-RestMethod -Method Post "$api/carriers/ghn/webhook" -Headers $h -ContentType "application/json" -InFile tools/sample-webhook-ghn-delivered.json

Start-Sleep 5
Invoke-RestMethod "$api/shipments/ghn/GHNTEST001" | ConvertTo-Json -Depth 5
# → status Delivered, isLate true, 2 events Applied

# Gửi lại đúng webhook cũ → không tạo trùng (idempotent)
Invoke-RestMethod -Method Post "$api/carriers/ghn/webhook" -Headers $h -ContentType "application/json" -InFile tools/sample-webhook-ghn.json
# Sai secret → 401
Invoke-RestMethod -Method Post "$api/carriers/ghn/webhook" -Headers @{ "X-Webhook-Secret" = "wrong" } -ContentType "application/json" -InFile tools/sample-webhook-ghn.json
```

## 4. Chạy thử Replayer với dữ liệu mẫu

```powershell
$bucket = "<DataBucketName>"
python tools/prepare_olist.py --input tools/sample-data --output data/sample-prepared
aws s3 sync data/sample-prepared/chunks "s3://$bucket/dataset/chunks/"

sam remote invoke ReplayerFunction --stack-name shipment-sla
# → {"Orders":5,"Sent":22,"Failed":0,...}

aws s3 ls "s3://$bucket/events/" --recursive     # file JSONL của event lake
aws logs tail "/aws/lambda/<tên ProcessorFunction>" --since 10m
```

## 5. Dữ liệu thật Olist (≈99k đơn)

1. Tải **Brazilian E-Commerce Public Dataset by Olist** trên Kaggle (cần tài khoản Kaggle),
   giải nén vào `data/olist/` (thư mục `data/` đã nằm trong `.gitignore`).
2. Chuẩn bị và thay dữ liệu mẫu:

```powershell
python tools/prepare_olist.py --input data/olist --output data/prepared
aws s3 rm "s3://$bucket/dataset/chunks/" --recursive
aws s3 sync data/prepared/chunks "s3://$bucket/dataset/chunks/"
aws dynamodb delete-item --table-name <TableName> --key file://tools/replay-cursor-key.json   # về đầu dataset
sam remote invoke ReplayerFunction --stack-name shipment-sla                                  # 1 lượt thử
```

3. **Bật lịch 15 phút:** trong `samconfig.toml` sửa `ReplayState=\"DISABLED\"` → `ReplayState=\"ENABLED\"`, rồi:

```powershell
sam build; sam deploy
```

Với 20 đơn/lượt × 96 lượt/ngày ≈ 1.900 đơn (~9.000 sự kiện)/ngày → dữ liệu đủ chạy ~50 ngày.

## 5b. Báo cáo SLA (Athena)

Bảng Glue `events` dùng **partition projection** theo `dt`, nên dữ liệu mới vào S3 là truy vấn được ngay,
không cần crawler hay `MSCK REPAIR`.

```powershell
Invoke-RestMethod "$api/sla/summary"                          # 30 ngày, theo hãng
Invoke-RestMethod "$api/sla/summary?days=7&groupBy=region"    # 7 ngày, theo bang
# → { days, groupBy, rows: [ { group, delivered, late, latePercent } ] }
```

Muốn truy vấn tay thì mở Athena Console, chọn workgroup `shipment-sla-wg`, database `shipment_sla_lake`:

```sql
SELECT carrier, count(*) AS delivered, count_if(islate) AS late
FROM shipment_sla_lake.events
WHERE dt >= '2026-11-01' AND status = 'Delivered' AND outcome = 'Applied'
GROUP BY carrier;
```

## 5c. Demo giá trị thực tế

```powershell
.\tools\demo.ps1              # 5 cảnh, Enter để chuyển cảnh (cảnh 4 gửi email cảnh báo thật)
.\tools\demo.ps1 -SkipAlarm   # bỏ cảnh cảnh báo email
```

| Cảnh | Nỗi đau | Hệ thống chứng minh |
| --- | --- | --- |
| 1 | Mỗi hãng một định dạng, một mã trạng thái | 3 webhook khác nhau → cùng trạng thái `OutForDelivery` |
| 2 | Webhook trùng, sai thứ tự, quét nhầm | 8 webhook → trạng thái cuối vẫn `Delivered`; trùng bị loại, cũ = `Stale`, đi lùi = `Rejected` |
| 3 | CSKH mở nhiều trang để tra 1 đơn | 1 lần gọi API ra toàn bộ timeline của 1 đơn Olist thật |
| 4 | Biết giao trễ khi khách phàn nàn | 5 đơn trễ / giờ → alarm → email trong vài phút |
| 5 | Chọn hãng theo cảm tính | Tỉ lệ đúng hạn theo hãng & bang từ Athena |

Đơn demo có mã bắt đầu bằng `DEMO` và **không tính vào** `/sla/summary`.

## 6. Kiểm tra hằng ngày (5 phút)

- CloudWatch → Lambda `ReplayerFunction`: có chạy mỗi 15 phút, `Failed = 0`.
- SQS DLQ = 0 message (nếu > 0 sẽ có email cảnh báo).
- `aws s3 ls s3://$bucket/events/` có thư mục `dt=` của hôm nay.
- Mỗi tuần: chụp Cost Explorer (lọc theo service) → lưu vào `docs/evidence/`.

## 7. Thiết kế đáng chú ý (để trình bày)

| Quyết định | Lý do |
| --- | --- |
| **SQS FIFO, MessageGroupId = vận đơn** | Sự kiện của 1 vận đơn xử lý đúng thứ tự; các vận đơn khác vẫn song song |
| **eventId xác định (hash)** + điều kiện DynamoDB | Webhook gửi trùng → `Duplicate`, không xử lý 2 lần (idempotent) |
| **Transaction DynamoDB + version** | Lịch sử và trạng thái luôn khớp; 2 writer không ghi đè nhau |
| **State machine theo rank** | Chặn đi lùi / sau trạng thái kết thúc; sự kiện cũ đến muộn = `Stale` |
| **Adapter mỗi hãng** | Thêm hãng mới = thêm 1 class, không sửa luồng xử lý |
| **Partial batch failure** | Chỉ message lỗi quay lại hàng đợi; sau 5 lần → DLQ + alarm |
| **Serverless, arm64, on-demand** | Không VPC/NAT, trả theo request; log giữ 14 ngày |
| **Athena + partition projection** | Phân tích SLA trên S3, không cần crawler; lọc theo `dt` để chỉ quét phần dữ liệu cần; workgroup chặn ở 1 GB/query |
| **Whitelist `groupBy`, `days` là số nguyên** | Không đưa chuỗi người dùng nhập vào SQL (chống SQL injection) |

## 8. Chi phí & dọn dẹp

Ước tính với lịch 15 phút: **khoảng 3–5 USD/tháng** (DynamoDB write chiếm nhiều nhất).
Đây là ước tính – dùng Cost Explorer để lấy số thật cho báo cáo.

```powershell
# Chỉ khi đã nộp và được chấm xong:
aws s3 rm "s3://$bucket" --recursive
sam delete --stack-name shipment-sla
```

## Lộ trình

- [x] Tuần 1: webhook 3 hãng, SQS FIFO, Processor, DynamoDB, Replayer, alarm trễ + DLQ
- [x] Tuần 2: Glue Data Catalog + Athena (`GET /sla/summary`), GitHub Actions CI
- [ ] (tuỳ chọn) Cognito JWT authorizer cho `/shipments` và `/sla`
- [x] Deploy AWS, chạy dữ liệu Olist thật, bật lịch 15 phút, xác nhận email cảnh báo
- [x] Script demo `tools/demo.ps1`
- [ ] Sơ đồ kiến trúc PNG, evidence, video demo

## Dữ liệu

Olist Brazilian E-Commerce Public Dataset (Kaggle, CC BY-NC-SA 4.0). Thời điểm đặt hàng, lấy hàng,
giao hàng, ngày hẹn giao và bang là **thật**; hãng vận chuyển, mã vận đơn, các mốc
InTransit/OutForDelivery và FailedAttempt là **giả lập** (xem `tools/prepare_olist.py`).
