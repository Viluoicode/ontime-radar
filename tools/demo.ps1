# Demo giá trị thực tế của Shipment SLA Service – chạy trên hệ thống đã deploy.
# Mỗi cảnh ứng với 1 "nỗi đau" trong docs/product.md.
#
#   .\tools\demo.ps1 -Email quanly@example.com              # cả 5 cảnh (cảnh 4 gửi email cảnh báo thật)
#   .\tools\demo.ps1 -Email quanly@example.com -SkipAlarm   # bỏ cảnh 4 (cảnh 4 mất tới ~20 phút)
#   .\tools\demo.ps1 -Email quanly@example.com -NoPause     # chạy liền một mạch, không chờ Enter
#   .\tools\demo.ps1 -Token $token                          # dùng token có sẵn (get-token.ps1)
#
# -Email: tài khoản nhóm "manager" (cảnh 5 cần quyền xem báo cáo SLA). Mật khẩu được hỏi ẩn.
#         Chạy bằng tài khoản CSKH cũng được: cảnh 1–4 chạy bình thường, cảnh 5 cho thấy API trả 403.
# Đơn demo có mã bắt đầu bằng DEMO → không tính vào báo cáo SLA (/sla/summary).
param(
    [string]$Email,                  # tài khoản Quản lý (nhóm manager)
    [string]$Token,                  # hoặc truyền sẵn access token (chạy tự động, không hỏi mật khẩu)
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
$headers = @{ "X-Webhook-Secret" = $secret }            # hãng vận chuyển gửi webhook
if (-not $Token) {
    if (-not $Email) { $Email = Read-Host "Email tài khoản Quản lý (nhóm manager)" }
    $Token = & "$PSScriptRoot\get-token.ps1" -Email $Email -StackName $StackName
}
$auth = @{ Authorization = "Bearer $Token" }            # nhân viên đọc dữ liệu (Cognito)

# Read the Cognito groups from the token payload (display only – the API does the real check).
function Get-TokenGroups($jwt) {
    try {
        $p = $jwt.Split(".")[1].Replace("-", "+").Replace("_", "/")
        switch ($p.Length % 4) { 2 { $p += "==" } 3 { $p += "=" } }
        $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json
        return @($claims."cognito:groups" | Where-Object { $_ })
    } catch { return @() }
}
$groups = Get-TokenGroups $Token
$groupText = if ($groups.Count) { $groups -join ", " } else { "không rõ" }
$run = Get-Date -Format "MMddHHmmss"           # mã riêng cho mỗi lần chạy → chạy lại bao nhiêu lần cũng được
$today = [DateTime]::UtcNow.Date

function Send($carrier, $payload) {
    $json = $payload | ConvertTo-Json -Compress
    # API Gateway allows 10 requests/s per route (burst 20): on 429 wait and resend (same payload = same eventId).
    for ($try = 1; ; $try++) {
        try { Invoke-RestMethod -Method Post "$api/carriers/$carrier/webhook" -Headers $headers -ContentType "application/json" -Body $json | Out-Null; return }
        catch { if ($try -ge 5 -or [int]$_.Exception.Response.StatusCode -ne 429) { throw }; Start-Sleep -Milliseconds (200 * [math]::Pow(2, $try - 1)) }
    }
}
$inv = [Globalization.CultureInfo]::InvariantCulture
function Iso($d)   { $d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $inv) }
function Day($d)   { $d.ToString("yyyy-MM-dd", $inv) }
function Epoch($d) { [DateTimeOffset]::new($d, [TimeSpan]::Zero).ToUnixTimeMilliseconds() }
# Processing is asynchronous (API → SQS → Processor): right after a deploy the processor cold-starts,
# so a shipment may need a few more seconds to appear. Retry 404 for up to ~20 s.
function Lookup($carrier, $code) {
    for ($i = 1; ; $i++) {
        try { return Invoke-RestMethod "$api/shipments/$carrier/$code" -Headers $auth }
        catch { if ($i -ge 10 -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }; Start-Sleep 2 }
    }
}

Write-Host ""
Write-Host "  SHIPMENT SLA SERVICE – DEMO   (API: $api, lượt chạy $run)" -ForegroundColor White
Note "Đăng nhập với nhóm: $groupText"
if ($groups.Count -and $groups -notcontains "manager") {
    Write-Host "  Lưu ý: tài khoản không thuộc nhóm Quản lý → cảnh 5 (báo cáo SLA) sẽ bị API từ chối (403), đúng thiết kế." -ForegroundColor Yellow
}

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
Start-Sleep 4
# 8 webhooks → 7 stored events (the retry is dropped). Wait until all of them are processed.
for ($i = 0; $i -lt 10; $i++) { $r = Lookup "ghn" $code; if (@($r.events).Count -ge 7) { break }; Start-Sleep 2 }
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
    $alarm = aws cloudwatch describe-alarms --alarm-name-prefix "$StackName-LateDeliveryAlarm" --query "MetricAlarms[0].AlarmName" --output text
    $threshold = aws cloudwatch describe-alarms --alarm-names $alarm --query "MetricAlarms[0].Threshold" --output text
    Note "Quy tắc cảnh báo: tỉ lệ trễ ≥ $threshold % trong 2 trên 3 khung 15 phút liên tiếp"
    Note "(một khung xấu lẻ loi không báo – tránh email giả). Mô phỏng J&T giao trễ hàng loạt KÉO DÀI:"
    Note "gửi 2 đợt đơn trễ ở 2 khung 15 phút liên tiếp. Có thể phải chờ tới 15 phút giữa 2 đợt."

    function Send-LateBurst($tag, $count = 15) {
        foreach ($n in 1..$count) {
            $tc = "DEMO$($run)$tag$n"
            Send "jt" @{ billCode = $tc; scanType = "PICKUP"; scanTime = (Epoch $today.AddDays(-5).AddHours(8)); expectedDate = (Day $today.AddDays(-3)); receiverProvince = "HCM" }
            Send "jt" @{ billCode = $tc; scanType = "SIGNED"; scanTime = (Epoch ([DateTime]::UtcNow.AddMinutes(-$n))); receiverProvince = "HCM" }
        }
    }
    Send-LateBurst "A"
    Good ("{0:HH:mm} – đợt 1: 15 đơn J&T giao trễ" -f (Get-Date))

    # CloudWatch windows are aligned to the clock: :00, :15, :30, :45 (UTC). Wait for the next one.
    $nowUtc = [DateTime]::UtcNow
    $nextWindow = $nowUtc.Date.AddMinutes([math]::Floor($nowUtc.TimeOfDay.TotalMinutes / 15) * 15 + 15).AddSeconds(15)
    while ([DateTime]::UtcNow -lt $nextWindow) {
        $left = $nextWindow - [DateTime]::UtcNow
        Write-Host ("`r  Chờ sang khung 15 phút kế tiếp: còn {0:mm\:ss}   " -f $left) -NoNewline
        Start-Sleep 5
    }
    Write-Host ""
    Send-LateBurst "B"
    Good ("{0:HH:mm} – đợt 2: 15 đơn J&T giao trễ (khung thứ 2 liên tiếp)" -f (Get-Date))
    Note "Đang chờ CloudWatch nhận số liệu và đánh giá (thường 1–5 phút)..."
    $state = "OK"; $deadline = (Get-Date).AddMinutes(8)
    while ((Get-Date) -lt $deadline) {
        $state = aws cloudwatch describe-alarms --alarm-names $alarm --query "MetricAlarms[0].StateValue" --output text
        Write-Host ("`r  {0:HH:mm:ss}  trạng thái cảnh báo: {1}     " -f (Get-Date), $state) -NoNewline
        if ($state -eq "ALARM") { break }
        Start-Sleep 20
    }
    Write-Host ""
    if ($state -eq "ALARM") {
        Good "Cảnh báo đã bật → email 'ALARM: LateDeliveryAlarm' (có link trang Cần xử lý) đang tới hộp thư."
        Good "Người vận hành biết ngay hãng nào đang trễ, chủ động báo khách / đổi hãng."
        Good "Khi tỉ lệ trễ trở lại bình thường (~30–45 phút sau), sẽ có thêm email 'OK' báo đã hết sự cố."
    } else {
        Note "Cảnh báo chưa bật sau 8 phút. Kiểm tra lại trong CloudWatch → Alarms sau ít phút."
    }
    Pause-Demo
}

# =====================================================================================
Title "CẢNH 5 · Chọn hãng bằng số liệu, không theo cảm tính"
Note "Vấn đề: không biết hãng nào / khu vực nào hay giao trễ để đàm phán hoặc chuyển hãng."
Note "Truy vấn Athena trên toàn bộ sự kiện thật (đã loại đơn demo)..."

$byCarrier = $null
try { $byCarrier = Invoke-RestMethod "$api/sla/summary?days=30&groupBy=carrier" -Headers $auth }
catch { if ([int]$_.Exception.Response.StatusCode -ne 403) { throw } }

if (-not $byCarrier) {
    Write-Host "  ✖ 403 Forbidden: tài khoản nhóm [$groupText] không có quyền xem báo cáo SLA." -ForegroundColor Yellow
    Good "Đúng thiết kế: CSKH chỉ tra cứu đơn, báo cáo SLA dành cho Quản lý."
    Good "API tự chặn (không chỉ ẩn menu trên web) → gọi thẳng API bằng token CSKH cũng không xem được."
    Note "Muốn xem cảnh này: .\tools\demo.ps1 -Email <email-quản-lý> -SkipAlarm"
} else {
    $byCarrier.rows | ForEach-Object {
        [pscustomobject]@{ "Hãng" = $_.group.ToUpper(); "Đã giao" = $_.delivered; "Trễ" = $_.late; "Tỉ lệ trễ" = ("{0:N1}%" -f $_.latePercent); "Đúng hạn" = ("{0:N1}%" -f (100 - $_.latePercent)) }
    } | Format-Table -AutoSize | Out-String | Write-Host

    $byRegion = Invoke-RestMethod "$api/sla/summary?days=30&groupBy=region" -Headers $auth
    Note "Khu vực có tỉ lệ trễ cao nhất (có ít nhất 3 đơn đã giao):"
    $byRegion.rows | Where-Object { $_.delivered -ge 3 } | Select-Object -First 5 | ForEach-Object {
        [pscustomobject]@{ "Bang" = $_.group; "Đã giao" = $_.delivered; "Trễ" = $_.late; "Tỉ lệ trễ" = ("{0:N1}%" -f $_.latePercent) }
    } | Format-Table -AutoSize | Out-String | Write-Host

    $total = ($byCarrier.rows | Measure-Object delivered -Sum).Sum
    Good "Số liệu từ $total đơn thật đã giao. Dữ liệu tăng ~1.900 đơn/ngày nhờ Replayer chạy mỗi 15 phút."
    Good "Mỗi truy vấn tốn chưa tới 0,001 USD – cả hệ thống chạy khoảng vài USD/tháng."
}
Write-Host ""
