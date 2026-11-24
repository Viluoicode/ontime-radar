# ADR-0002 – DynamoDB single-table cho trạng thái + lịch sử vận đơn

- **Trạng thái:** Đã áp dụng (từ 16/11/2026)
- **Liên quan:** [ADR-0001](0001-sqs-fifo-per-shipment.md), [ADR-0003](0003-serverless-no-vpc.md)

## Bối cảnh

Mỗi vận đơn cần:

1. **trạng thái hiện tại** (để tra cứu và để state machine quyết định sự kiện mới),
2. **toàn bộ lịch sử** sự kiện, kể cả sự kiện bị bỏ qua (`Stale`, `Rejected`) – CSKH cần thấy "hãng đã báo gì, lúc nào",
3. **ghi đúng 1 lần** dù cùng sự kiện tới nhiều lần (sau cửa sổ dedup 5 phút của SQS – ADR-0001),
4. **không ghi đè nhau** nếu 2 lần xử lý chạy song song.

Truy cập chính: "lấy 1 vận đơn theo hãng + mã" (CSKH) và "ghi 1 sự kiện" (Processor). Thống kê (tỉ lệ đúng hạn theo
hãng / bang / tuần) là một nhu cầu **khác**, tần suất thấp.

## Quyết định

**Một bảng DynamoDB** (`ShipmentsTable`, on-demand), khoá `PK` / `SK`:

| PK | SK | Nội dung |
| --- | --- | --- |
| `SHIPMENT#{carrier}#{tracking}` | `STATE` | Trạng thái hiện tại + `version` |
| `SHIPMENT#{carrier}#{tracking}` | `EVENT#{occurredAt UTC}#{eventId}` | 1 sự kiện trong lịch sử (kèm quyết định `Applied/Stale/Rejected`) |
| `REPLAY` | `CURSOR` | Vị trí Replayer trong dataset |

Mỗi sự kiện được ghi bằng **một `TransactWriteItems`** (`ShipmentStore.ProcessAsync`):

- Put lịch sử có điều kiện `attribute_not_exists(PK)` → `eventId` đã có thì **cả transaction huỷ** → kết quả
  `Duplicate`, coi như thành công, không ghi gì (**lớp chống trùng thứ 2**, không giới hạn thời gian).
- Nếu được áp dụng: Put `STATE` có điều kiện `version = <version đã đọc>` (**optimistic concurrency**) → hai lần xử lý
  song song không thể ghi đè nhau; bên thua bị lỗi → SQS thử lại.

Tra cứu = **1 `Query` theo PK** → trả cả trạng thái lẫn lịch sử (đã sắp theo thời gian nhờ SK).

Thống kê **không** chạy trên DynamoDB: Processor ghi thêm mỗi sự kiện vào S3 (`events/dt=…`, JSONL) và báo cáo SLA
dùng **Athena** trên S3.

## Phương án đã cân nhắc

| Phương án | Vì sao không chọn |
| --- | --- |
| **2 bảng** (`shipment_events` + `shipment_current_status`) | Làm được (transaction DynamoDB ghi được nhiều bảng), nhưng tra cứu cần 2 lệnh đọc; thêm 1 bảng để cấp quyền, theo dõi, xoá. Không có lợi ích gì cho kiểu truy cập "theo 1 vận đơn" |
| **Aurora / RDS PostgreSQL** | SQL và JOIN tiện cho thống kê, nhưng phải chạy trong VPC (Lambda cần VPC, NAT hoặc endpoint – ADR-0003), có chi phí nền kể cả khi không dùng, Lambda cần RDS Proxy để không cạn kết nối. Quá nặng cho tải hiện tại |
| **Chỉ lưu trạng thái hiện tại** (không lịch sử) | Mất giá trị "timeline 1 chỗ" cho CSKH; không truy vết được vì sao trạng thái như vậy |
| **Chống trùng bằng bảng idempotency riêng / TTL** | Lịch sử đã có `eventId` trong SK → dùng luôn item lịch sử làm "dấu đã xử lý", không cần bảng thứ 3 |

## Hệ quả

**Tốt**

- Tra cứu 1 lần `Query`, độ trễ thấp, không phụ thuộc số vận đơn trong bảng.
- Chống trùng vĩnh viễn + chống ghi đè mà không cần khoá hay bảng phụ; lịch sử và trạng thái **luôn khớp** (cùng 1 transaction).
- On-demand: không phải dự đoán capacity; partition theo vận đơn nên tải tự phân tán.

**Đánh đổi / giới hạn**

- **Không truy vấn tuỳ ý được** (vd. "mọi đơn đang giao ở bang SP"): không có index phụ; Scan thì đắt → mọi danh sách /
  thống kê đi qua Athena trên S3 (chậm hơn, ~ 1–2 giây lần đầu, có cache).
- **Hai nguồn dữ liệu:** DynamoDB ghi trước, S3 ghi sau, không cùng transaction → Lambda dừng đúng giữa 2 bước thì sự
  kiện có trong DynamoDB nhưng thiếu trong Athena (hiếm; có test `Known_gap_…`; sửa triệt để = DynamoDB Streams).
- Transaction tốn gấp đôi đơn vị ghi so với Put thường (chấp nhận được ở tải hiện tại).
- Thiết kế khoá gắn chặt với kiểu truy cập: thêm kiểu truy cập mới thường cần GSI hoặc nguồn khác.
- Bảng không có TTL → dữ liệu demo / chaos (`DEMO…`) ở lại mãi.

## Bằng chứng

- Test với DynamoDB giả trong bộ nhớ (ngữ nghĩa transaction thật): `Same_event_twice_is_a_Duplicate_and_writes_nothing`,
  `Version_conflict_is_not_mistaken_for_a_Duplicate` (`ShipmentStoreTests`).
- Chaos test trên AWS (dữ liệu giả lập), 24/11/2026: 50 bản trùng gửi lại **sau** cửa sổ dedup 5 phút của SQS →
  **50/50 `Duplicate`** ở DynamoDB, số item lịch sử không tăng (518 = 518 sự kiện), 0 sai lệch.
- Đối chiếu số liệu 20/11/2026: Athena 7.404 đơn đã giao / 339 trễ khớp từng đơn với dataset; 0 đơn có 2 lần *Đã giao* được áp dụng.
