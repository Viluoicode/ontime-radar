# Chaos test trên hệ thống ĐÃ DEPLOY: gửi ~2.500 webhook "lộn xộn" (trùng, sai thứ tự, gửi trễ, hỏng) giống hãng
# vận chuyển gửi sai, chờ Processor xử lý hết rồi đối chiếu DynamoDB với đáp án → docs\evidence\chaos-*.md (+ ground truth).
#
#   .\tools\chaos.ps1                  # 2.500 webhook, seed ngẫu nhiên (in ra để chạy lại y hệt)
#   .\tools\chaos.ps1 -Seed 1123       # chạy lại đúng kịch bản đó (run id mới → mã vận đơn mới)
#   .\tools\chaos.ps1 -DryRun          # chỉ xem kịch bản, không gửi gì, không cần AWS
#   .\tools\chaos.ps1 -LateDuplicates 0   # bỏ đợt 3 (gửi lại bản trùng sau > 5 phút)
#
# Chống trùng 2 lớp: bản trùng trong 5 phút bị SQS FIFO loại; đợt 3 gửi lại 50 bản trùng sau > 5 phút để chúng
# tới DynamoDB → phải ra Duplicate (đếm trong log Processor) và không thêm history.
#
# Dữ liệu GIẢ LẬP: mã vận đơn DEMOCX… (báo cáo SLA đã loại DEMO*), KHÔNG đơn nào giao trễ → alarm tỉ lệ trễ không bật.
# Metric "Deliveries" vẫn cộng thêm các đơn đã giao của lượt chạy. Mỗi lượt để lại ~90 shipment (~600 item)
# trong bảng DynamoDB (bảng không có TTL).
# Tự chờ để không trùng lượt Replayer (:10/:25/:40/:55 UTC): hai bên dùng chung giới hạn 10 req/s của route webhook.
# Secret webhook đọc từ cấu hình Lambda, truyền qua biến môi trường (không in ra, không nằm trên dòng lệnh).
param(
    [int]$Webhooks = 2500,
    [int]$Seed,
    [int]$LateDuplicates = 50,
    [string]$StackName = "shipment-sla",
    [string]$Region = "ap-southeast-1",
    [switch]$DryRun,
    [switch]$NoWait
)
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot "ShipmentSla.Chaos"
$toolArgs = @("--webhooks", $Webhooks, "--late-duplicates", $LateDuplicates, "--region", $Region, "--out", (Join-Path $root "docs\evidence"))
if ($PSBoundParameters.ContainsKey("Seed")) { $toolArgs += @("--seed", $Seed) }
if ($NoWait) { $toolArgs += "--no-wait" }

if ($DryRun) {
    dotnet run -c Release --project $project -- @toolArgs --dry-run
    exit $LASTEXITCODE
}

# ---------- Thông tin stack (không in secret) ----------
$env:AWS_DEFAULT_REGION = $Region
function Get-StackOutput($key) {
    $v = aws cloudformation describe-stacks --stack-name $StackName --query "Stacks[0].Outputs[?OutputKey=='$key'].OutputValue" --output text
    if (-not $v -or $v -eq "None") { throw "Không đọc được output '$key' của stack $StackName" }
    return $v
}
$toolArgs += @(
    "--api", (Get-StackOutput "ApiUrl"),
    "--table", (Get-StackOutput "TableName"),
    "--queue-url", (Get-StackOutput "EventsQueueUrl"),
    "--dlq-url", (Get-StackOutput "EventsDlqUrl"))
$fn = aws cloudformation describe-stack-resource --stack-name $StackName --logical-resource-id ApiFunction --query StackResourceDetail.PhysicalResourceId --output text
$processor = aws cloudformation describe-stack-resource --stack-name $StackName --logical-resource-id ProcessorFunction --query StackResourceDetail.PhysicalResourceId --output text
$toolArgs += @("--processor-log-group", "/aws/lambda/$processor")

try {
    $env:CHAOS_WEBHOOK_SECRET = aws lambda get-function-configuration --function-name $fn --query Environment.Variables.WEBHOOK_SECRET --output text
    if (-not $env:CHAOS_WEBHOOK_SECRET) { throw "Không đọc được secret webhook" }
    dotnet run -c Release --project $project -- @toolArgs
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:CHAOS_WEBHOOK_SECRET -ErrorAction SilentlyContinue
}
exit $code
