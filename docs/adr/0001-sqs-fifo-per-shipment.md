# ADR-0001 – Hàng đợi SQS FIFO, mỗi vận đơn một message group

- **Trạng thái:** Đã áp dụng (từ 16/11/2026)
- **Liên quan:** [ADR-0002](0002-dynamodb-single-table.md) (lớp chống trùng thứ 2), [ADR-0003](0003-serverless-no-vpc.md)

## Bối cảnh

Hãng vận chuyển đẩy trạng thái qua webhook (`POST /carriers/{carrier}/webhook`). Thực tế webhook:

- **đến dồn từng đợt** (hãng gom lại gửi) và **bị gửi lại** khi hãng không nhận được phản hồi kịp;
- **cần xử lý đúng thứ tự trong từng vận đơn**: nếu *Đã giao* được ghi trước *Đang giao* của cùng đơn, trạng thái
  có thể bị "nhảy lùi";
- hãng chỉ cần biết "đã nhận" – nếu API xử lý nặng (DynamoDB transaction, ghi S3, metric) ngay trong request thì
  hãng chờ lâu và gửi lại nhiều hơn.

Ràng buộc của tài khoản: **chỉ 10 Lambda chạy cùng lúc** (quota mặc định, chưa được nâng) – API, Processor và
Replayer dùng chung con số này.

## Quyết định

API chỉ **xác thực → chuẩn hoá (adapter của hãng) → đẩy vào SQS FIFO → trả `202`**. Processor (Lambda) đọc hàng đợi
và làm phần nặng.

| Thiết lập | Giá trị | Vì sao |
| --- | --- | --- |
| Loại hàng đợi | **FIFO** | Giữ thứ tự trong từng group |
| `MessageGroupId` | `carrier#tracking` (= vận đơn) | Sự kiện của **một** vận đơn xử lý lần lượt; các vận đơn khác nhau vẫn song song |
| `MessageDeduplicationId` | `eventId` (hash của hãng + mã vận đơn + mã trạng thái gốc + thời điểm) | Cùng webhook gửi lại trong 5 phút → SQS bỏ, không tới Processor |
| Batch / lỗi | 10 message, `ReportBatchItemFailures` | Chỉ message lỗi quay lại hàng đợi. **Luật FIFO:** lỗi ở message *k* → báo lỗi cả các message sau nó trong batch, để sự kiện sau không vượt sự kiện đang chờ thử lại |
| Thử lại / DLQ | 5 lần → `EventsDlq` (giữ 14 ngày) + `DlqAlarm` | Message hỏng không chặn group mãi; có email báo |
| Song song tối đa | `MaximumConcurrency: 2` | Processor không chiếm hết 10 Lambda của tài khoản → API vẫn nhận webhook |

Payload sai định dạng **không** vào hàng đợi: API trả `400` ngay để hãng biết đừng gửi lại. DLQ chỉ dành cho lỗi
phía mình (DynamoDB lỗi, bug) – nếu không, alarm DLQ sẽ kêu mỗi lần hãng gửi sai.

## Phương án đã cân nhắc

| Phương án | Vì sao không chọn |
| --- | --- |
| **Xử lý đồng bộ trong API** (không hàng đợi) | Hãng chờ cả DynamoDB + S3 + metric; một đợt dồn chiếm hết 10 Lambda → 503; không có chỗ giữ sự kiện lỗi để thử lại |
| **SQS Standard** | Không giữ thứ tự, có thể giao 1 message nhiều lần. Vẫn chạy được nhờ state machine + DynamoDB, nhưng nhiều sự kiện bị đánh `Stale` vô ích và khó giải thích khi debug |
| **Kinesis Data Streams** (partition key = vận đơn) | Cũng giữ thứ tự theo key, nhưng tính tiền theo giờ shard kể cả khi không có dữ liệu; phải tự quản lý shard; không có DLQ / dedup sẵn như SQS |
| **EventBridge** | Không đảm bảo thứ tự; hợp với fan-out nhiều đích hơn là xử lý tuần tự |
| **Kafka / MSK** | Mạnh nhất về thứ tự + replay, nhưng chạy trong VPC, có chi phí nền theo giờ – quá nặng cho vài nghìn sự kiện/ngày (xem ADR-0003) |

## Hệ quả

**Tốt**

- Thứ tự theo vận đơn + song song giữa các vận đơn; API trả `202` nhanh.
- Chống trùng lớp 1 miễn phí (dedup 5 phút); DLQ + alarm sẵn có; trả tiền theo request.

**Đánh đổi / giới hạn**

- **FIFO giữ thứ tự *đến*, không phải thứ tự *xảy ra*.** Hãng gửi sai thứ tự thì hàng đợi vẫn giữ nguyên sai thứ tự
  đó → **vẫn cần** state machine (`StatusRules`: sự kiện cũ hơn → `Stale`).
- **Dedup chỉ 5 phút** → bản gửi lại sau đó tới Processor; cần lớp chống trùng thứ 2 ở DynamoDB (ADR-0002).
- **Chặn đầu hàng theo group:** một message lỗi giữ cả vận đơn đó lại tới khi thử lại xong / sang DLQ (các vận đơn
  khác không bị ảnh hưởng).
- Giới hạn thông lượng FIFO (khoảng 300 lệnh API/giây khi không gom batch) – thừa nhiều cho tải hiện tại (~ 9.500 webhook/ngày).
- Xử lý là **bất đồng bộ**: ngay sau `202`, tra cứu có thể chưa thấy sự kiện (thường < 1 giây, xem số đo dưới).

## Bằng chứng

- Chaos test trên AWS (dữ liệu giả lập), 24/11/2026: 2.533 webhook (mỗi sự kiện trùng ~ 5 bản, xáo thứ tự, 25 payload
  hỏng) → 518 sự kiện được lưu đúng 1 lần, **0 sai lệch** so với đáp án, DLQ 0 → 0; độ trễ nhận → xử lý p50 / p95 =
  17 / 42 ms. Báo cáo: `tools/chaos.ps1` → `docs/evidence/chaos-*.md`.
- Test `ProcessorTests.Failure_mid_batch_fails_it_and_every_later_message_without_processing_them` (luật FIFO trong batch).
