# Đưa website (web/) lên S3 + CloudFront.
#   .\tools\deploy-web.ps1
# 1) Chép web/ ra thư mục tạm, sinh config.js cho domain CloudFront (config.js ở máy giữ nguyên localhost)
# 2) Đồng bộ lên bucket web (xoá file cũ không còn dùng)
# 3) Xoá cache CloudFront để người dùng thấy bản mới (1 đường dẫn "/*" – trong 1.000 lần miễn phí/tháng)
param(
    [string]$StackName = "shipment-sla",
    [string]$Region = "ap-southeast-1"
)
$ErrorActionPreference = "Stop"

$outputs = aws cloudformation describe-stacks --stack-name $StackName --region $Region --query "Stacks[0].Outputs" --output json | ConvertFrom-Json
$o = @{}; $outputs | ForEach-Object { $o[$_.OutputKey] = $_.OutputValue }
if (-not $o.WebBucketName) { throw "Stack chưa có WebBucketName – hãy chạy 'sam build; sam deploy' trước." }

$stage = Join-Path ([IO.Path]::GetTempPath()) ("ontime-web-" + [guid]::NewGuid().ToString("N"))
try {
    # Paths are joined one segment at a time so the script also runs on Linux (GitHub Actions, pwsh).
    $js = Join-Path $stage "js"
    Copy-Item -Path (Join-Path (Split-Path $PSScriptRoot -Parent) "web") -Destination $stage -Recurse
    Remove-Item (Join-Path $js "config.example.js") -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $js "config.js") -ErrorAction SilentlyContinue   # local copy (localhost) – replaced below
    & (Join-Path $PSScriptRoot "make-web-config.ps1") -StackName $StackName -Region $Region `
        -RedirectUri $o.WebUrl -OutFile (Join-Path $js "config.js")

    # Browsers re-check after 5 minutes; CloudFront is invalidated right after the upload.
    aws s3 sync $stage "s3://$($o.WebBucketName)/" --delete --region $Region --only-show-errors `
        --cache-control "public, max-age=300"
    if ($LASTEXITCODE -ne 0) { throw "Tải lên S3 thất bại." }

    $inv = aws cloudfront create-invalidation --distribution-id $o.WebDistributionId --paths "/*" `
        --query "Invalidation.Id" --output text
    Write-Host "Đã tải lên s3://$($o.WebBucketName)/ và xoá cache CloudFront ($inv)." -ForegroundColor Green
    Write-Host "Website: $($o.WebUrl)" -ForegroundColor Cyan
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
