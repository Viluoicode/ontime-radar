# Đăng nhập Cognito và trả về access token để gọi API.
#
#   $token = .\tools\get-token.ps1 -Email ban@example.com
#   Invoke-RestMethod "$api/sla/summary" -Headers @{ Authorization = "Bearer $token" }
#
# - Mật khẩu được hỏi ẩn, không nằm trong lịch sử lệnh hay danh sách tiến trình.
# - Lần đăng nhập đầu (mật khẩu tạm trong email mời) sẽ yêu cầu đặt mật khẩu mới.
# - Nếu tài khoản đã bật MFA, script hỏi mã 6 số từ app Authenticator.
# - Gọi thẳng API công khai của Cognito: người dùng không cần AWS CLI nếu truyền -ClientId.
param(
    [Parameter(Mandatory = $true)][string]$Email,
    [string]$ClientId,
    [string]$StackName = "shipment-sla",
    [string]$Region = "ap-southeast-1"
)
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not $ClientId) {
    $ClientId = aws cloudformation describe-stacks --stack-name $StackName --region $Region `
        --query "Stacks[0].Outputs[?OutputKey=='UserPoolClientId'].OutputValue" --output text
}

function Plain([Security.SecureString]$s) {
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}

function Cognito($action, $body) {
    try {
        Invoke-RestMethod -Method Post "https://cognito-idp.$Region.amazonaws.com/" `
            -Headers @{ "X-Amz-Target" = "AWSCognitoIdentityProviderService.$action" } `
            -ContentType "application/x-amz-json-1.1" -Body ($body | ConvertTo-Json -Compress -Depth 5)
    } catch {
        $msg = $_.ErrorDetails.Message
        if ($msg) { $msg = ($msg | ConvertFrom-Json).message }
        throw "Đăng nhập thất bại: $msg"
    }
}

$password = Plain (Read-Host "Mật khẩu của $Email" -AsSecureString)
$r = Cognito "InitiateAuth" @{
    AuthFlow = "USER_PASSWORD_AUTH"; ClientId = $ClientId
    AuthParameters = @{ USERNAME = $Email; PASSWORD = $password }
}
$password = $null

while ($r.ChallengeName) {
    switch ($r.ChallengeName) {
        "NEW_PASSWORD_REQUIRED" {
            Write-Host "Lần đăng nhập đầu: hãy đặt mật khẩu mới (≥ 8 ký tự, có chữ hoa, chữ thường và số)." -ForegroundColor Yellow
            $new = Plain (Read-Host "Mật khẩu mới" -AsSecureString)
            $again = Plain (Read-Host "Nhập lại mật khẩu mới" -AsSecureString)
            if ($new -ne $again) { throw "Hai lần nhập mật khẩu mới không khớp." }
            $responses = @{ USERNAME = $Email; NEW_PASSWORD = $new }
        }
        "SOFTWARE_TOKEN_MFA" {
            $code = Read-Host "Mã 6 số từ app Authenticator"
            $responses = @{ USERNAME = $Email; SOFTWARE_TOKEN_MFA_CODE = $code }
        }
        default { throw "Cognito yêu cầu bước '$($r.ChallengeName)' mà script chưa hỗ trợ." }
    }
    $r = Cognito "RespondToAuthChallenge" @{
        ChallengeName = $r.ChallengeName; ClientId = $ClientId; Session = $r.Session; ChallengeResponses = $responses
    }
    $new = $null; $again = $null
}

Write-Host "Đăng nhập thành công – token có hạn 1 giờ." -ForegroundColor Green
$r.AuthenticationResult.AccessToken
