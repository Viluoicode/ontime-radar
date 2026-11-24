# Architecture Decision Records (ADR)

Mỗi file ghi **một** quyết định kiến trúc: bối cảnh lúc quyết định, lựa chọn, các phương án đã cân nhắc, hệ quả
(cả đánh đổi) và bằng chứng đo được trong dự án. Quyết định thay đổi thì viết ADR mới và đổi trạng thái ADR cũ thành
"Thay thế bởi ADR-xxxx" – không sửa lịch sử.

| # | Quyết định | Trạng thái |
| --- | --- | --- |
| [0001](0001-sqs-fifo-per-shipment.md) | Hàng đợi SQS FIFO, mỗi vận đơn một message group | Đã áp dụng |
| [0002](0002-dynamodb-single-table.md) | DynamoDB single-table cho trạng thái + lịch sử, ghi bằng 1 transaction | Đã áp dụng |
| [0003](0003-serverless-no-vpc.md) | Serverless hoàn toàn (Lambda arm64), không dùng VPC | Đã áp dụng |
| [0004](0004-github-oidc-deploy.md) | Deploy qua GitHub Actions bằng OIDC, có bước duyệt + permissions boundary | Đã áp dụng |

Các quyết định nhỏ hơn (cache Athena, cảnh báo theo tỉ lệ trễ, kiểm tra mã vận đơn…) nằm ở
[README – 7. Thiết kế đáng chú ý](../../README.md#7-thiết-kế-đáng-chú-ý-để-trình-bày). Giới hạn đã biết:
[README – 7b](../../README.md#7b-giới-hạn-đã-biết).
