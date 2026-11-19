# Kiểm tra sức khoẻ hệ thống hằng ngày (~1 phút: mỗi lệnh aws mất 1–2 giây). CHỈ ĐỌC – không thay đổi gì trên AWS.
#
#   .\tools\daily-check.ps1                              # in bảng ĐẠT / CẢNH BÁO / LỖI
#   .\tools\daily-check.ps1 -SaveTo docs\evidence        # + lưu kết quả thành file .txt có ngày giờ
#   .\tools\daily-check.ps1 -AuthMatrix -CskhEmail a@x -ManagerEmail b@x   # + bảng phân quyền 401/403/200
#
# Mã thoát: 0 = không có LỖI, 1 = có ít nhất 1 LỖI (dùng được với Task Scheduler).
# Không gọi Cost Explorer (0,01 USD/lần) – xem chi phí trên Console.
param(
    [string]$StackName = "shipment-sla",
    [string]$Region = "ap-southeast-1",
    [string]$SaveTo,
    [switch]$AuthMatrix,
    [string]$CskhEmail,
    [string]$ManagerEmail
)
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:AWS_DEFAULT_REGION = $Region
$results = New-Object System.Collections.Generic.List[object]
$report = New-Object System.Collections.Generic.List[string]

function Add-Result($check, $status, $detail, $hint = "") {
    $results.Add([pscustomobject]@{ Check = $check; Status = $status; Detail = $detail; Hint = $hint })
}
function Try-Check($name, [scriptblock]$body) {
    try { & $body } catch { Add-Result $name "LỖI" "Không kiểm tra được: $($_.Exception.Message)" }
}
function Http-Status($url, $headers = @{}) {
    try { (Invoke-WebRequest $url -Headers $headers -UseBasicParsing -TimeoutSec 20).StatusCode }
    catch { if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 } }
}
function Metric-Sum($ns, $metric, $dimName, $dimValue, $hours = 24) {
    $start = (Get-Date).ToUniversalTime().AddHours(-$hours).ToString("s") + "Z"
    $end = (Get-Date).ToUniversalTime().ToString("s") + "Z"
    $v = aws cloudwatch get-metric-statistics --namespace $ns --metric-name $metric --dimensions "Name=$dimName,Value=$dimValue" `
        --start-time $start --end-time $end --period ($hours * 3600) --statistics Sum --query "Datapoints[0].Sum" --output text
    if ($v -eq "None" -or -not $v) { 0 } else { [double]$v }
}
$nf = [Globalization.CultureInfo]::GetCultureInfo("vi-VN")

# ---------------- 1. stack ----------------
$stack = aws cloudformation describe-stacks --stack-name $StackName --output json | ConvertFrom-Json
$o = @{}; $stack.Stacks[0].Outputs | ForEach-Object { $o[$_.OutputKey] = $_.OutputValue }
$st = $stack.Stacks[0].StackStatus
Add-Result "1. Stack CloudFormation" $(if ($st -like "*_COMPLETE" -and $st -notlike "*ROLLBACK*") { "ĐẠT" } else { "LỖI" }) $st "Xem tab Events của stack trên Console."
$resource = { param($id) aws cloudformation describe-stack-resource --stack-name $StackName --logical-resource-id $id --query StackResourceDetail.PhysicalResourceId --output text }

# ---------------- 2. schedule ----------------
Try-Check "2. Lịch Replayer" {
    $sch = aws scheduler list-schedules --query "Schedules[?contains(Name,'Every15Minutes')].State | [0]" --output text
    Add-Result "2. Lịch Replayer" $(if ($sch -eq "ENABLED") { "ĐẠT" } else { "CẢNH BÁO" }) "State = $sch" "Bật lại: ReplayState=ENABLED trong samconfig.toml rồi sam deploy."
}

# ---------------- 3. replayer last hour + dataset progress ----------------
Try-Check "3. Replayer (1 giờ qua)" {
    $fn = $o.ReplayerFunctionName
    $since = [DateTimeOffset]::UtcNow.AddHours(-1).ToUnixTimeMilliseconds()
    $msgs = aws logs filter-log-events --log-group-name "/aws/lambda/$fn" --start-time $since --filter-pattern '"Replay ReplayResult"' --query "events[].message" --output text
    $runs = [regex]::Matches(($msgs -join " "), "CursorAfter = (\d+), Orders = (\d+), Sent = (\d+), Failed = (\d+)")
    $failed = ($runs | ForEach-Object { [int]$_.Groups[4].Value } | Measure-Object -Sum).Sum
    $sent = ($runs | ForEach-Object { [int]$_.Groups[3].Value } | Measure-Object -Sum).Sum
    $status = if ($runs.Count -eq 0) { "LỖI" } elseif ($failed -gt 0 -or $runs.Count -lt 3) { "CẢNH BÁO" } else { "ĐẠT" }
    Add-Result "3. Replayer (1 giờ qua)" $status "$($runs.Count) lượt, gửi $sent sự kiện, lỗi $failed" "Xem log: aws logs tail /aws/lambda/$fn --since 1h"

    if ($runs.Count -gt 0) {
        $cursor = [int]$runs[$runs.Count - 1].Groups[1].Value
        $chunks = @(aws s3 ls "s3://$($o.DataBucketName)/dataset/chunks/" | Where-Object { $_ -match '\.jsonl$' })
        $envVars = aws lambda get-function-configuration --function-name $fn --query "Environment.Variables" --output json | ConvertFrom-Json
        $chunkSize = [int]$envVars.CHUNK_SIZE
        $perRun = [int]$envVars.ORDERS_PER_RUN
        $lastKey = ($chunks[-1] -split '\s+')[-1]
        $lastLines = @(aws s3 cp "s3://$($o.DataBucketName)/dataset/chunks/$lastKey" - | Where-Object { $_ }).Count
        $total = ($chunks.Count - 1) * $chunkSize + $lastLines
        $daysLeft = [math]::Ceiling(($total - $cursor) / ($perRun * 96))
        Add-Result "   Tiến độ dữ liệu Olist" "ĐẠT" ("{0}/{1} đơn ({2} %), còn khoảng {3} ngày" -f $cursor.ToString("N0", $nf), $total.ToString("N0", $nf), (100 * $cursor / $total).ToString("N1", $nf), $daysLeft)
    }
}

# ---------------- 4. lambda errors / throttles ----------------
Try-Check "4. Lambda (24 giờ qua)" {
    $parts = foreach ($id in "ProcessorFunction", "ApiFunction") {
        $name = & $resource $id
        $e = Metric-Sum "AWS/Lambda" "Errors" "FunctionName" $name
        $t = Metric-Sum "AWS/Lambda" "Throttles" "FunctionName" $name
        [pscustomobject]@{ Id = $id; Errors = $e; Throttles = $t }
    }
    $bad = ($parts | Measure-Object -Property Errors -Sum).Sum + ($parts | Measure-Object -Property Throttles -Sum).Sum
    Add-Result "4. Lambda (24 giờ qua)" $(if ($bad -eq 0) { "ĐẠT" } else { "CẢNH BÁO" }) (($parts | ForEach-Object { "$($_.Id): lỗi $($_.Errors), bị giới hạn $($_.Throttles)" }) -join " · ") "Throttles > 0: tài khoản chỉ chạy được 10 Lambda cùng lúc (Service Quotas)."
}

# ---------------- 5. queues ----------------
Try-Check "5. SQS / DLQ" {
    $q = aws sqs get-queue-attributes --queue-url $o.EventsQueueUrl --attribute-names ApproximateNumberOfMessages ApproximateNumberOfMessagesNotVisible --query Attributes --output json | ConvertFrom-Json
    $dlq = [int](aws sqs get-queue-attributes --queue-url $o.EventsDlqUrl --attribute-names ApproximateNumberOfMessages --query Attributes.ApproximateNumberOfMessages --output text)
    $backlog = [int]$q.ApproximateNumberOfMessages
    $status = if ($dlq -gt 0) { "LỖI" } elseif ($backlog -gt 500) { "CẢNH BÁO" } else { "ĐẠT" }
    Add-Result "5. SQS / DLQ" $status "Hàng đợi: $backlog chờ, $($q.ApproximateNumberOfMessagesNotVisible) đang xử lý · DLQ: $dlq" "DLQ > 0: xem log Processor để biết sự kiện nào lỗi."
}

# ---------------- 6. event lake today ----------------
Try-Check "6. Event lake hôm nay" {
    $today = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    $files = @(aws s3 ls "s3://$($o.DataBucketName)/events/dt=$today/" | Where-Object { $_ }).Count
    Add-Result "6. Event lake hôm nay" $(if ($files -gt 0) { "ĐẠT" } else { "CẢNH BÁO" }) "events/dt=$today/: $files file" "Không có file: Replayer hoặc Processor chưa chạy hôm nay (ngày theo UTC)."
}

# ---------------- 7. alarms ----------------
Try-Check "7. Alarm CloudWatch" {
    $al = aws cloudwatch describe-alarms --alarm-name-prefix $StackName --query "MetricAlarms[].[AlarmName,StateValue]" --output json | ConvertFrom-Json
    $inAlarm = @($al | Where-Object { $_[1] -eq "ALARM" })
    $detail = ($al | ForEach-Object { "$(($_[0] -split '-')[2]): $($_[1])" }) -join " · "
    Add-Result "7. Alarm CloudWatch" $(if ($inAlarm.Count -eq 0) { "ĐẠT" } else { "CẢNH BÁO" }) $detail "LateDeliveryAlarm = ALARM: tỉ lệ trễ ≥ ngưỡng trong 2/3 khung 15 phút (thật, hoặc do demo cảnh 4). Xem trang Cần xử lý; tự về OK khi tỉ lệ giảm."
}

# ---------------- 8. API ----------------
Try-Check "8. API" {
    $h = Http-Status "$($o.ApiUrl)/health"
    $n = Http-Status "$($o.ApiUrl)/sla/summary"
    Add-Result "8. API" $(if ($h -eq 200 -and $n -eq 401) { "ĐẠT" } else { "LỖI" }) "/health = $h · /sla/summary không token = $n (cần 401)" "401 ≠ thực tế: kiểm tra JWT authorizer trong template."
}

# ---------------- 9. website ----------------
Try-Check "9. Website CloudFront" {
    $r = Invoke-WebRequest $o.WebUrl -UseBasicParsing -TimeoutSec 20
    $need = "Content-Security-Policy", "Strict-Transport-Security", "X-Frame-Options", "X-Content-Type-Options"
    $missing = @($need | Where-Object { -not $r.Headers[$_] })
    Add-Result "9. Website CloudFront" $(if ($r.StatusCode -eq 200 -and $missing.Count -eq 0) { "ĐẠT" } else { "LỖI" }) `
        "HTTP $($r.StatusCode) · header bảo mật: $(if ($missing) { 'thiếu ' + ($missing -join ', ') } else { 'đủ 4/4' })" "Deploy lại web: .\tools\deploy-web.ps1"
}

# ---------------- 10. alert email ----------------
Try-Check "10. Email cảnh báo" {
    $topic = aws sns list-topics --query "Topics[?contains(TopicArn,'$StackName-AlertTopic')].TopicArn | [0]" --output text
    $subs = @(aws sns list-subscriptions-by-topic --topic-arn $topic --query "Subscriptions[].SubscriptionArn" --output text) -split "\s+" | Where-Object { $_ }
    $ok = @($subs | Where-Object { $_ -like "arn:*" }).Count
    Add-Result "10. Email cảnh báo" $(if ($ok -gt 0) { "ĐẠT" } else { "CẢNH BÁO" }) "$ok đăng ký đã xác nhận, $(@($subs | Where-Object { $_ -eq 'PendingConfirmation' }).Count) đang chờ" "Mở email 'AWS Notification - Subscription Confirmation' và bấm Confirm."
}

# ---------------- optional: role matrix ----------------
if ($AuthMatrix) {
    if (-not $CskhEmail) { $CskhEmail = Read-Host "Email tài khoản CSKH" }
    if (-not $ManagerEmail) { $ManagerEmail = Read-Host "Email tài khoản Quản lý" }
    $tok = @{
        "CSKH" = & "$PSScriptRoot\get-token.ps1" -Email $CskhEmail -StackName $StackName
        "Quản lý" = & "$PSScriptRoot\get-token.ps1" -Email $ManagerEmail -StackName $StackName
    }
    $paths = "/health", "/shipments?limit=1", "/sla/summary", "/sla/trend"
    $matrix = foreach ($p in $paths) {
        [pscustomobject]@{
            "Đường dẫn" = $p
            "Không token" = Http-Status "$($o.ApiUrl)$p"
            "Token giả" = Http-Status "$($o.ApiUrl)$p" @{ Authorization = "Bearer eyJhbGciOiJIUzI1NiJ9.e30.x" }
            "CSKH" = Http-Status "$($o.ApiUrl)$p" @{ Authorization = "Bearer $($tok['CSKH'])" }
            "Quản lý" = Http-Status "$($o.ApiUrl)$p" @{ Authorization = "Bearer $($tok['Quản lý'])" }
        }
    }
    $expected = @{ "/health" = "200,200,200,200"; "/shipments?limit=1" = "401,401,200,200"; "/sla/summary" = "401,401,403,200"; "/sla/trend" = "401,401,403,200" }
    $allOk = -not ($matrix | Where-Object { "$($_.'Không token'),$($_.'Token giả'),$($_.CSKH),$($_.'Quản lý')" -ne $expected[$_.'Đường dẫn'] })
    Add-Result "11. Ma trận phân quyền" $(if ($allOk) { "ĐẠT" } else { "LỖI" }) $(if ($allOk) { "Đúng thiết kế (xem bảng bên dưới)" } else { "Sai khác thiết kế (xem bảng bên dưới)" })
}

# ---------------- output ----------------
$report.Add("OnTime Radar – kiểm tra hệ thống lúc $((Get-Date).ToString('dd/MM/yyyy HH:mm')) (giờ máy) · stack $StackName · $Region")
$report.Add("")
foreach ($r in $results) {
    $icon = switch ($r.Status) { "ĐẠT" { "[ĐẠT]     " } "CẢNH BÁO" { "[CẢNH BÁO]" } default { "[LỖI]     " } }
    $report.Add("$icon $($r.Check): $($r.Detail)")
    if ($r.Status -ne "ĐẠT" -and $r.Hint) { $report.Add("            → $($r.Hint)") }
}
if ($AuthMatrix) { $report.Add(""); $report.Add(($matrix | Format-Table -AutoSize | Out-String).TrimEnd()) }
$fails = @($results | Where-Object Status -eq "LỖI").Count
$warns = @($results | Where-Object Status -eq "CẢNH BÁO").Count
$report.Add("")
$report.Add("Tổng: $(@($results | Where-Object Status -eq 'ĐẠT').Count) đạt · $warns cảnh báo · $fails lỗi")

foreach ($line in $report) {
    # .Contains, not -like: in -like, "[...]" is a character class, not literal brackets.
    $color = if ($line.Contains("[LỖI]")) { "Red" } elseif ($line.Contains("[CẢNH BÁO]")) { "Yellow" } elseif ($line.Contains("[ĐẠT]")) { "Green" } else { "Gray" }
    Write-Host $line -ForegroundColor $color
}

if ($SaveTo) {
    New-Item -ItemType Directory -Force -Path $SaveTo | Out-Null
    $file = Join-Path $SaveTo ("daily-check-{0}.txt" -f (Get-Date -Format "yyyy-MM-dd_HHmm"))
    # Mask the AWS account number before saving evidence.
    $text = ($report -join "`r`n") -replace '\b\d{12}\b', '************'
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($file), $text, (New-Object Text.UTF8Encoding $true))
    Write-Host "Đã lưu: $file" -ForegroundColor Cyan
}
exit $(if ($fails -gt 0) { 1 } else { 0 })
