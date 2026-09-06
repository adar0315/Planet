param(
    [string]$NssmPath = "C:/DCIM/nssm.exe",
    [string]$ServiceName = "PlanetService",
    [string]$PublishDir = "C:/PlanetPublish"
)

# 把後端重新發布到部署目錄並重啟服務。
# 嫌打字麻煩的話可以直接雙擊同目錄下的 publish.bat，它會自動跳UAC提權後呼叫這支腳本。
#
# 為什麼一定要先停服務：服務執行中會鎖住 Planet.Service.dll，publish 會失敗，
# 錯誤訊息是「由於另一個處理程序正在使用檔案...」（2026-09-06 實際踩過）。
#
# 路徑刻意用正斜線：.NET 與 PowerShell 在 Windows 上都吃，而且不會被各層跳脫吃掉反斜線。

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "需要系統管理員權限才能停止/啟動服務。請以系統管理員身分重新執行。" -ForegroundColor Red
    exit 1
}

# 專案路徑由這支腳本的位置推導，整個 repo 搬家也不用改
$projectDir = Resolve-Path (Join-Path $PSScriptRoot "../../src/Planet.Service")

Write-Host "1/4 停止服務 $ServiceName ..." -ForegroundColor Cyan
& $NssmPath stop $ServiceName | Out-Null

# nssm stop 回來時行程可能還沒真的結束，等到檔案鎖真的放開為止
$dll = Join-Path $PublishDir "Planet.Service.dll"
$waited = 0
while ($waited -lt 30) {
    try {
        $fs = [IO.File]::Open($dll, "Open", "Write", "None")
        $fs.Close()
        break
    } catch {
        Start-Sleep -Seconds 1
        $waited++
    }
}
if ($waited -ge 30) {
    Write-Host "等了30秒，$dll 仍被鎖住。請確認服務已經停止。" -ForegroundColor Red
    exit 1
}
Write-Host "    服務已停止（等待 $waited 秒）"

Write-Host "2/4 發布到 $PublishDir ..." -ForegroundColor Cyan
# appsettings.Production.json 不會被覆蓋（來源專案裡沒有同名檔案），連線字串與管理金鑰安全
dotnet publish $projectDir -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "publish 失敗，服務維持停止狀態，修好之後再跑一次。" -ForegroundColor Red
    exit 1
}

Write-Host "3/4 啟動服務 ..." -ForegroundColor Cyan
& $NssmPath start $ServiceName | Out-Null
Start-Sleep -Seconds 3

Write-Host "4/4 驗證 ..." -ForegroundColor Cyan
try {
    $status = Invoke-RestMethod "http://localhost:5050/api/simulation/status" -TimeoutSec 15
    Write-Host "    完成：$($status.cellCount) 格、$($status.plateCount) 個板塊、$($status.plumeCount) 支熱柱" -ForegroundColor Green
} catch {
    Write-Host "    服務起來了但模擬端點還沒回應：$($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "    資料庫還沒好的話這是正常的，PlanetSeeder 會自己重試（見 service-stderr.log）。" -ForegroundColor Yellow
}
