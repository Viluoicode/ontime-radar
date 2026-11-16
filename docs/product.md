# Shipment SLA Service – Định nghĩa sản phẩm

> Dự án portfolio trong khuôn khổ AWS FCAJ. Dữ liệu là **đơn hàng thật lịch sử (Olist, 2016–2018)**
> được phát lại (replay) như sự kiện realtime; định dạng webhook của các hãng là **mô phỏng**,
> không phải đặc tả chính thức của GHN / GHTK / J&T.

## 1. Vấn đề

Shop bán hàng online ở Việt Nam thường giao qua **2–4 hãng vận chuyển** cùng lúc
(GHN, GHTK, J&T, Viettel Post...). Mỗi hãng:

- có **mã trạng thái riêng** (`delivering`, `status_id = 45`, `scanType = DELIVERY`...),
- có **trang tra cứu riêng**, webhook riêng.

Hậu quả:

| Nỗi đau | Ảnh hưởng |
| --- | --- |
| Nhân viên mở nhiều trang để tra 1 đơn | Mất thời gian, CSKH trả lời chậm |
| Biết đơn giao trễ khi **khách phàn nàn** | Mất uy tín, đánh giá xấu, hoàn hàng |
| Không có số liệu đúng hạn theo hãng / khu vực | Chọn hãng theo cảm tính, không đàm phán được giá/SLA |
| Webhook đến trùng, sai thứ tự | Trạng thái trên hệ thống bị nhảy lùi, sai lệch |

## 2. Giải pháp (value)

Một backend nhận sự kiện từ mọi hãng và trả về **một sự thật duy nhất** cho mỗi vận đơn.

1. **Chuẩn hóa đa hãng** – mỗi hãng một adapter, quy về 8 trạng thái chung.
2. **Timeline 1 chỗ** – `GET /shipments/{id}` trả trạng thái hiện tại + toàn bộ lịch sử.
3. **Chống sai lệch** – loại sự kiện trùng (idempotent), bỏ qua sự kiện cũ đến muộn,
   chặn chuyển trạng thái vô lý (DELIVERED → IN_TRANSIT).
4. **Cảnh báo giao trễ gần realtime** – đơn giao sau ngày cam kết → metric CloudWatch → email.
5. **Phân tích SLA** – lưu toàn bộ sự kiện vào S3, truy vấn Athena: tỉ lệ đúng hạn
   theo hãng, theo tỉnh/bang, thời gian vận chuyển trung bình.

**Câu pitch:** *Một backend theo dõi vận đơn đa hãng: chuẩn hóa trạng thái, phát hiện đơn trễ
gần realtime và đo tỉ lệ giao đúng hạn theo hãng & khu vực – chạy serverless, chi phí vài USD/tháng.*

## 3. Ai sử dụng (users)

| Người dùng | Họ cần gì | Dùng tính năng nào |
| --- | --- | --- |
| **Nhân viên vận hành (Ops)** | Biết ngay đơn nào có vấn đề | Email cảnh báo trễ, tra cứu timeline |
| **CSKH** | Trả lời "đơn tôi đâu?" trong vài giây | `GET /shipments/{id}` |
| **Quản lý vận hành / chủ shop** | Chọn hãng tốt cho từng khu vực | Báo cáo SLA (Athena) |
| **Hãng vận chuyển** (hệ thống) | Đẩy trạng thái qua webhook | `POST /carriers/{carrier}/webhook` |
| **Hệ thống khác** (OMS, website shop) | Lấy trạng thái chuẩn hóa qua API | REST API |

## 4. Khách hàng mục tiêu (ai trả tiền – giả thuyết)

1. **Nhà bán hàng SME đa kênh** (Shopee, TikTok Shop, Lazada + website riêng),
   khoảng 50–2.000 đơn/ngày, dùng từ 2 hãng trở lên. → Khách hàng chính.
2. **Đơn vị fulfillment / 3PL nhỏ** – giao hộ nhiều shop, cần báo cáo SLA để báo cáo lại cho shop.
3. **Nền tảng quản lý bán hàng (OMS/POS)** – tích hợp như một module tracking.

Đây là giả thuyết để định hướng thiết kế; dự án **không** khảo sát thị trường thật.

## 5. Chỉ số đo được (KPI của hệ thống)

| KPI | Cách đo |
| --- | --- |
| Số sự kiện xử lý / ngày | CloudWatch Lambda Invocations, số dòng S3 |
| Tỉ lệ sự kiện bị loại (trùng / cũ / vô lý) | Trường `outcome` trong lịch sử |
| Độ trễ từ webhook → trạng thái cập nhật | `processedAt - receivedAt` |
| Tỉ lệ giao đúng hạn theo hãng / khu vực | Athena |
| Chi phí AWS / tháng | Cost Explorer (số thật) |

## 6. Phạm vi

**MVP (tuần 1–2):** webhook 3 hãng, chuẩn hóa, xử lý theo thứ tự (SQS FIFO), DynamoDB,
lịch sử, S3 event lake, cảnh báo trễ, Replayer dữ liệu Olist, Athena SLA.

**Ngoài phạm vi:** kết nối API thật của hãng, giao diện web, tính phí vận chuyển,
tối ưu tuyến đường, đăng nhập người dùng (Cognito là tuỳ chọn nếu dư thời gian).

## 7. Mô hình trạng thái

```
CREATED → PICKED_UP → IN_TRANSIT (lặp lại khi qua kho) → OUT_FOR_DELIVERY → DELIVERED
                                                    ↕
                                              FAILED_ATTEMPT → RETURNED
CREATED → CANCELLED
```

| Quy tắc | Kết quả (`outcome`) |
| --- | --- |
| Sự kiện đã nhận trước đó (cùng `eventId`) | `Duplicate` – bỏ qua |
| Sự kiện xảy ra **trước** sự kiện mới nhất đã áp dụng | `Stale` – lưu lịch sử, không đổi trạng thái |
| Đơn đã ở trạng thái kết thúc (DELIVERED / RETURNED / CANCELLED) | `Rejected` |
| Đi lùi (vd. IN_TRANSIT sau OUT_FOR_DELIVERY) | `Rejected` |
| CANCELLED khi hàng đã được lấy | `Rejected` |
| Còn lại | `Applied` – cập nhật trạng thái |

**Giao trễ** = ngày giao thực tế > ngày giao dự kiến (so theo ngày, giờ UTC).
