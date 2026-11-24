# ADR-0004 – Deploy qua GitHub Actions bằng OIDC, có bước duyệt

- **Trạng thái:** Đã áp dụng (chạy thành công lần đầu 20/11/2026)
- **Liên quan:** [ADR-0003](0003-serverless-no-vpc.md)

## Bối cảnh

- Repo **public**: mọi thứ trong repo và log GitHub Actions đều ai cũng đọc được.
- Deploy bằng `sam deploy` từ máy cá nhân dễ quên bước, khó chứng minh "ai deploy gì, lúc nào", và phụ thuộc vào máy.
- Cách phổ biến – tạo IAM user, lưu **access key dài hạn** vào GitHub Secrets – có rủi ro: key không tự hết hạn, lộ một
  lần là dùng được mãi tới khi bị thu hồi, quyền thường rộng.
- Stack ứng dụng tạo IAM role cho Lambda → ai sửa được template là có thể tạo role mạnh hơn mức cần.

## Quyết định

1. **OIDC thay cho access key.** Stack riêng `github-oidc-deploy` (`infra/github-oidc.yaml`) tạo OIDC provider của
   GitHub và role `github-deploy-shipment-sla`. Job deploy xin token OIDC → đổi lấy **credential tạm ≤ 1 giờ**
   (`sts:AssumeRoleWithWebIdentity`). Không có secret AWS dài hạn nào ở GitHub (chỉ lưu ARN của role).
2. **Role chỉ tin đúng repo + đúng environment:** điều kiện `sub = repo:<owner@id>/<repo@id>:environment:production`
   – dùng **ID bất biến** của owner / repo, nên repo khác (kể cả tạo lại trùng tên) không assume được.
3. **Bước duyệt:** environment `production` có *required reviewers* → job deploy dừng chờ bấm **Approve**; token OIDC
   chỉ được cấp cho job đã qua bước này.
4. **Workflow:** push `main` (đụng `src/`, `template.yaml`, `web/`, `global.json`) → test → chờ duyệt → `sam build` →
   `sam deploy` → upload website → kiểm tra `/health`.
5. **Không đưa secret vào workflow:** `sam deploy` không truyền `--parameter-overrides` → CloudFormation **giữ giá trị
   cũ** của tham số (secret webhook, email cảnh báo…). Log che số tài khoản (`mask-aws-account-id`).
6. **Permissions boundary:** role deploy chỉ được tạo / sửa role của stack **khi role đó gắn boundary**
   `shipment-sla-boundary`. Boundary nằm ở stack `infra/` nên stack ứng dụng không tự nới được; tên role deploy không bắt
   đầu bằng `shipment-sla-` nên nó không tự sửa quyền của chính mình.

## Phương án đã cân nhắc

| Phương án | Vì sao không chọn |
| --- | --- |
| **IAM user + access key trong GitHub Secrets** | Key dài hạn; lộ (vd. qua log của repo public, fork, dependency độc) là dùng được tới khi thu hồi; phải xoay key thủ công |
| **Chỉ deploy từ máy cá nhân** | Không có dấu vết / bước duyệt; dễ deploy code chưa qua test; phụ thuộc cấu hình máy. Vẫn giữ làm đường dự phòng (và bắt buộc cho vài trường hợp – xem dưới) |
| **AWS CodePipeline / CodeBuild** | Làm được, nhưng code đã ở GitHub, CI đã chạy bằng GitHub Actions; thêm dịch vụ và chi phí mà không thêm giá trị cho dự án 1 người |
| **OIDC nhưng role tin mọi branch / không có environment** | Bất kỳ workflow nào trong repo (kể cả branch thử) cũng deploy được production |

## Hệ quả

**Tốt**

- Không có credential AWS dài hạn nào để lộ; credential tạm tự hết hạn.
- Mỗi lần deploy có người duyệt và có lịch sử trên GitHub (ai, commit nào, lúc nào, kết quả).
- Kể cả khi token GitHub bị lạm dụng, role deploy **không tạo được role không có boundary** → không trao quyền admin cho Lambda được.

**Đánh đổi / giới hạn**

- **Tham số mới / đổi secret phải deploy 1 lần từ máy** (vì workflow giữ giá trị cũ, không truyền tham số).
- **Đổi quyền IAM có thứ tự bắt buộc:** cập nhật stack `github-oidc-deploy` (boundary / quyền role deploy) **trước**, rồi
  mới deploy ứng dụng; quyền mới cho Lambda phải thêm ở **cả** `Policies` trong template **và** boundary.
- Role deploy vẫn có quyền rộng trên chính stack ứng dụng (CloudFormation, Lambda, S3, …) – cần thiết để deploy; giới hạn
  bằng tên tài nguyên (`shipment-sla-*`) khi AWS cho phép.
- Repo public → log Actions public: không bao giờ `echo` secret; số tài khoản đã được che.
- Bước duyệt làm chậm deploy (cần người bấm) – chấp nhận vì đây là môi trường thật duy nhất.

## Bằng chứng

- Deploy qua OIDC + Approve chạy thành công từ 20/11/2026; các lần deploy 23/11 và 24/11/2026 đều qua test → chờ duyệt →
  deploy → `/health` 200 (lịch sử run trong tab *Actions* của repo).
- Mẫu điều kiện tin cậy và boundary: `infra/github-oidc.yaml`; workflow: `.github/workflows/deploy.yml`.
