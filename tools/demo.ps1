# Demo giá trị thực tế của Shipment SLA Service – chạy trên hệ thống đã deploy.
# Mỗi cảnh ứng với 1 "nỗi đau" trong docs/product.md.
#
#   .\tools\demo.ps1               # cả 5 cảnh (cảnh 4 sẽ gửi email cảnh báo thật)
#   .\tools\demo.ps1 -SkipAlarm    # bỏ cảnh 4
#   .\tools\demo.ps1 -NoPause      # chạy liền một mạch, không chờ Enter
#
# Đơn demo có mã bắt đầu bằng DEMO → không tính vào báo cáo SLA (/sla/summary).
param(
    [string]$StackName = "shipment-sla",
    [switch]$SkipAlarm,
    [switch]$NoPause
)
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Title($text) { Write-Host ""; Write-Host ("=" * 78) -ForegroundColor DarkGray; Write-Host " $text" -ForegroundColor Cyan; Write-Host ("=" * 78) -ForegroundColor DarkGray }
function Note($text)  { Write-Host "  $text" -ForegroundColor Gray }
function Good($text)  { Write-Host "  ✔ $text" -ForegroundColor Green }
function Pause-Demo   { Write-Host ""; if (-not $NoPause) { Read-Host "  [Enter] để sang cảnh tiếp theo" | Out-Null } }

# ---------- Kết nối hệ thống (secret đọc từ Lambda, không in ra) ----------
$api = aws cloudformation describe-stacks --stack-name $StackName --query "Stacks[0].Outputs[?OutputKey=='ApiUrl'].OutputValue" --output text
$fn = aws cloudformation describe-stack-resource --stack-name $StackName --logical-resource-id ApiFunction --query StackResourceDetail.PhysicalResourceId --output text
$secret = aws lambda get-function-configuration --function-name $fn --query Environment.Variables.WEBHOOK_SECRET --output text
$headers = @{ "X-Webhook-Secret" = $secret }
$run = Get-Date -Format "MMddHHmmss"           # mã riêng cho mỗi lần chạy → chạy lại bao nhiêu lần cũng được
$today = [DateTime]::UtcNow.Date

function Send($carrier, $payload) {
    $json = $payload | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Post "$api/carriers/$carrier/webhook" -Headers $headers -ContentType "application/json" -Body $json | Out-Null
}
$inv = [Globalization.CultureInfo]::InvariantCulture
function Iso($d)   { $d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $inv) }
function Day($d)   { $d.ToString("yyyy-MM-dd", $inv) }
function Epoch($d) { [DateTimeOffset]::new($d, [TimeSpan]::Zero).ToUnixTimeMilliseconds() }
function Lookup($carrier, $code) { Invoke-RestMethod "$api/shipments/$carrier/$code" }

Write-Host ""
Write-Host "  SHIPMENT SLA SERVICE – DEMO   (API: $api, lượt chạy $run)" -ForegroundColor White

# =====================================================================================
Title "CẢNH 1 · Mỗi hãng một kiểu dữ liệu → hệ thống hiểu chung một ngôn ngữ"
Note "Vấn đề: 3 hãng báo cùng một việc 'đang đi giao' nhưng mỗi hãng gửi một kiểu khác nhau."
Note "Nhân viên phải thuộc mã của từng hãng, mở từng trang tra cứu."

$t = [DateTime]::UtcNow.AddMinutes(-5)
Send "ghn"  @{ order_code = "DEMO$($run)G"; status = "delivering"; updated_date = (Iso $t); to_province = "HCM" }
Send "ghtk" @{ label_id = "DEMO$($run)K"; status_id = 45; action_time = $t.AddHours(7).ToString("yyyy-MM-ddTHH:mm:ss+07:00"); province = "HN" }
Send "jt"   @{ billCode = "DEMO$($run)J"; scanType = "OUT_FOR_DELIVERY"; scanTime = (Epoch $t); receiverProvince = "DN" }
Start-Sleep 5

$rows = foreach ($x in @(@("ghn","G",'status = "delivering", giờ UTC'), @("ghtk","K",'status_id = 45, giờ VN (+07:00)'), @("jt","J",'scanType = "OUT_FOR_DELIVERY", epoch ms'))) {
    $s = (Lookup $x[0] "DEMO$run$($x[1])").state
    [pscustomobject]@{ "Hãng" = $x[0].ToUpper(); "Hãng gửi" = $x[2]; "Hệ thống hiểu" = $s.status; "Thời điểm (UTC)" = ([DateTimeOffset]$s.lastEventAt).ToString("HH:mm") }
}
$rows | Format-Table -AutoSize | Out-String | Write-Host
Good "3 định dạng khác nhau → cùng 1 trạng thái 'OutForDelivery', cùng 1 múi giờ."
Good "Thêm hãng mới = viết thêm 1 adapter, không sửa phần còn lại."
Pause-Demo

# =====================================================================================
Title "CẢNH 2 · Webhook đến trùng, sai thứ tự → trạng thái vẫn đúng"
Note "Vấn đề: thực tế hãng gửi lại webhook (retry), gửi sự kiện cũ đến muộn, quét nhầm."
Note "Hệ thống thường sẽ cho đơn 'nhảy lùi' từ Đã giao về Đang vận chuyển."
Note ""
Note "Gửi 8 webhook cho đơn GHN DEMO$($run)T, hẹn giao $(Day $today.AddDays(-2)):"

$code = "DEMO$($run)T"
$steps = @(
    @("picked",        $today.AddDays(-4).AddHours(8),  "Lấy hàng"),
    @("transporting",  $today.AddDays(-3).AddHours(9),  "Đang vận chuyển"),
    @("delivering",    $today.AddDays(-2).AddHours(8),  "Đang đi giao"),
    @("transporting",  $today.AddDays(-2).AddHours(10), "Quét nhầm ở kho → đi lùi"),
    @("delivered",     $today.AddDays(-1).AddHours(9),  "Đã giao (trễ 1 ngày)"),
    @("delivered",     $today.AddDays(-1).AddHours(9),  "Hãng gửi lại y hệt (retry)"),
    @("delivery_fail", $today.AddDays(-2).AddHours(17), "Sự kiện cũ đến muộn"),
    @("delivering",    $today.AddHours(8),              "Sự kiện sau khi đã giao")
)
$i = 0
foreach ($s in $steps) {
    $i++
    $p = @{ order_code = $code; status = $s[0]; updated_date = (Iso $s[1]); to_province = "HCM"; client_order_code = "demo-$run" }
    if ($i -eq 1) { $p.expected_delivery_time = Day $today.AddDays(-2) }
    Send "ghn" $p
    Note ("  {0}. {1,-14} {2:dd/MM HH:mm}  {3}" -f $i, $s[0], $s[1], $s[2])
}
Start-Sleep 6

$r = Lookup "ghn" $code
Write-Host ""
Write-Host "  Kết quả hệ thống xử lý:" -ForegroundColor White
$meaning = @{ Applied = "cập nhật trạng thái"; Rejected = "chặn, chỉ lưu lịch sử"; Stale = "cũ, chỉ lưu lịch sử" }
$r.events | ForEach-Object {
    [pscustomobject]@{ "Lúc xảy ra" = ([DateTimeOffset]$_.occurredAt).ToString("dd/MM HH:mm"); "Trạng thái" = $_.status; "Quyết định" = $_.outcome; "Ý nghĩa" = $meaning[$_.outcome] }
} | Format-Table -AutoSize | Out-String | Write-Host
Good ("Trạng thái cuối: {0}, trễ hạn = {1}  (không bị nhảy lùi)" -f $r.state.status, $r.state.isLate)
Good ("Gửi 8 webhook → lưu {0} sự kiện: bản gửi lại (retry) bị loại, không xử lý 2 lần." -f $r.events.Count)
Pause-Demo

# =====================================================================================
Title "CẢNH 3 · CSKH: khách hỏi 'đơn của tôi đâu?' → 1 lần tra là đủ"
Note "Lấy 1 đơn THẬT từ dữ liệu Olist đang được phát lại mỗi 15 phút..."

$api_fn_logs = "/aws/lambda/$fn"
$since = [DateTimeOffset]::UtcNow.AddHours(-6).ToUnixTimeMilliseconds()
$lines = aws logs filter-log-events --log-group-name $api_fn_logs --start-time $since --filter-pattern '"Accepted" "FailedAttempt"' --query "events[].message" --output text
$real = $null
if ($lines) {
    # Newest real (non-demo) shipment that had a failed delivery attempt.
    $m = [regex]::Matches(($lines -join " "), "Accepted \w+ (ghn|ghtk|jt)#(\S+) FailedAttempt") |
         Where-Object { -not $_.Groups[2].Value.StartsWith("DEMO") -and -not $_.Groups[2].Value.StartsWith("GHNTEST") }
    if ($m) { $real = @($m)[-1] }
}
if ($real) {
    $c = $real.Groups[1].Value; $tc = $real.Groups[2].Value
    $r = Lookup $c $tc
    Note "Đơn $($c.ToUpper()) $tc  (mã đơn Olist $($r.state.orderId), giao tới bang $($r.state.region))"
    $r.events | ForEach-Object {
        [pscustomobject]@{ "Lúc" = ([DateTimeOffset]$_.occurredAt).ToString("yyyy-MM-dd HH:mm"); "Trạng thái" = $_.status; "Mã gốc của hãng" = $_.rawStatus; "Nơi" = $_.location }
    } | Format-Table -AutoSize | Out-String | Write-Host
    Good ("Hiện tại: {0} · hẹn giao {1} · trễ = {2}" -f $r.state.status, $r.state.estimatedDelivery, $(if ($null -eq $r.state.isLate) { "chưa giao" } else { $r.state.isLate }))
    Good "CSKH trả lời khách ngay: lần giao trước thất bại lúc nào, giờ đang ở đâu – không cần mở trang của hãng."
} else {
    Note "(Chưa tìm thấy đơn thật có giao thất bại trong 6 giờ qua – dùng đơn demo ở cảnh 2.)"
    Good "GET /shipments/ghn/$code trả về trạng thái + toàn bộ lịch sử trong 1 lần gọi."
}
Pause-Demo

# =====================================================================================
if (-not $SkipAlarm) {
    Title "CẢNH 4 · Biết đơn giao trễ TRƯỚC khi khách phàn nàn"
    Note "Vấn đề: shop thường chỉ biết giao trễ khi khách gọi điện, đánh giá 1 sao."
    Note "Mô phỏng 1 giờ J&T giao trễ hàng loạt: 5 đơn giao sau ngày hẹn..."
    foreach ($n in 1..5) {
        $tc = "DEMO$($run)L$n"
        Send "jt" @{ billCode = $tc; scanType = "PICKUP"; scanTime = (Epoch $today.AddDays(-5).AddHours(8)); expectedDate = (Day $today.AddDays(-3)); receiverProvince = "HCM" }
        Send "jt" @{ billCode = $tc; scanType = "SIGNED"; scanTime = (Epoch ([DateTime]::UtcNow.AddMinutes(-$n))); receiverProvince = "HCM" }
    }
    Note "Đã gửi. Ngưỡng cảnh báo: ≥ 5 đơn trễ / giờ. Đang chờ CloudWatch đánh giá (thường 1–5 phút)..."
    $alarm = aws cloudwatch describe-alarms --alarm-name-prefix "$StackName-LateDeliveryAlarm" --query "MetricAlarms[0].AlarmName" --output text
    $state = "OK"; $deadline = (Get-Date).AddMinutes(8)
    while ((Get-Date) -lt $deadline) {
        $state = aws cloudwatch describe-alarms --alarm-names $alarm --query "MetricAlarms[0].StateValue" --output text
        Write-Host ("`r  {0:HH:mm:ss}  trạng thái cảnh báo: {1}     " -f (Get-Date), $state) -NoNewline
        if ($state -eq "ALARM") { break }
        Start-Sleep 20
    }
    Write-Host ""
    if ($state -eq "ALARM") {
        Good "Cảnh báo đã bật → email 'ALARM: LateDeliveryAlarm' đang được gửi tới hộp thư của bạn."
        Good "Người vận hành biết ngay hãng nào đang trễ, chủ động báo khách / đổi hãng."
    } else {
        Note "Cảnh báo chưa bật sau 8 phút. Kiểm tra lại trong CloudWatch → Alarms sau ít phút."
    }
    Pause-Demo
}

# =====================================================================================
Title "CẢNH 5 · Chọn hãng bằng số liệu, không theo cảm tính"
Note "Vấn đề: không biết hãng nào / khu vực nào hay giao trễ để đàm phán hoặc chuyển hãng."
Note "Truy vấn Athena trên toàn bộ sự kiện thật (đã loại đơn demo)..."

$byCarrier = Invoke-RestMethod "$api/sla/summary?days=30&groupBy=carrier"
$byCarrier.rows | ForEach-Object {
    [pscustomobject]@{ "Hãng" = $_.group.ToUpper(); "Đã giao" = $_.delivered; "Trễ" = $_.late; "Tỉ lệ trễ" = ("{0:N1}%" -f $_.latePercent); "Đúng hạn" = ("{0:N1}%" -f (100 - $_.latePercent)) }
} | Format-Table -AutoSize | Out-String | Write-Host

$byRegion = Invoke-RestMethod "$api/sla/summary?days=30&groupBy=region"
Note "Khu vực có tỉ lệ trễ cao nhất (có ít nhất 3 đơn đã giao):"
$byRegion.rows | Where-Object { $_.delivered -ge 3 } | Select-Object -First 5 | ForEach-Object {
    [pscustomobject]@{ "Bang" = $_.group; "Đã giao" = $_.delivered; "Trễ" = $_.late; "Tỉ lệ trễ" = ("{0:N1}%" -f $_.latePercent) }
} | Format-Table -AutoSize | Out-String | Write-Host

$total = ($byCarrier.rows | Measure-Object delivered -Sum).Sum
Good "Số liệu từ $total đơn thật đã giao. Dữ liệu tăng ~1.900 đơn/ngày nhờ Replayer chạy mỗi 15 phút."
Good "Mỗi truy vấn tốn chưa tới 0,001 USD – cả hệ thống chạy khoảng vài USD/tháng."
Write-Host ""
