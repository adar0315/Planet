# 需先自行下載 nssm.exe 並放在此目錄下，或指定其路徑（此repo已內附 backend/deploy/nssm/nssm.exe）
#
# 使用前先執行（注意：-o 一定要指到「路徑本身不含單引號」的絕對路徑！這個repo放在
# D:\Adar's novel stories\Planet\... 底下，路徑裡的單引號會讓 dotnet publish 內建的
# _CopyResolvedFilesToPublishPreserveNewest target 壞掉，噴 MSB3094。詳見 docs/architecture.md
# 「EXE打包與NSSM部署」一節）：
#   dotnet publish ..\..\src\Planet.Service -c Release -o C:\PlanetPublish
#
# 本腳本需要系統管理員權限的PowerShell才能執行（nssm install/set都需要SERVICE_CHANGE_CONFIG權限）。
# 嫌打字麻煩的話可以直接雙擊同目錄下的 install-service.bat，它會自動跳UAC提權後幫你呼叫這支腳本，
# 用法一樣（例如雙擊執行等於是命令列打 install-service.bat -ExePath "C:\PlanetPublish\Planet.Service.exe"）。

param(
    [string]$NssmPath = ".\nssm.exe",
    [string]$ServiceName = "PlanetService",
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [string]$Port = "5202",
    [string]$LogDir = (Split-Path $ExePath),
    # 連線字串與管理金鑰刻意不寫在 appsettings.json（那個檔案會進版控）。有兩種提供方式：
    #
    #   (A) 部署目錄放 appsettings.Production.json（**目前實際採用的做法**）
    #       —— 不需要提權就能修改，重新部署也不會被覆蓋（專案裡沒有同名檔案）。
    #   (B) 用下面這兩個參數，透過 NSSM 的 AppEnvironmentExtra 設成環境變數
    #       —— 改一次就要重跑一次本腳本（需要系統管理員權限）。
    #
    # 兩者都不給的話服務會啟動失敗並在 stderr 說明原因（不會帶著空連線字串硬跑）。
    [string]$ConnectionString = "",
    # 管理端點（/api/admin，會清空整顆星球）的金鑰。沒設定的話管理端點會整個停用（回503），
    # 不會變成不用驗證——見 AdminApiKeyOptions 的說明。
    [string]$AdminApiKey = ""
)

& $NssmPath install $ServiceName $ExePath
& $NssmPath set $ServiceName AppDirectory (Split-Path $ExePath)
& $NssmPath set $ServiceName Start SERVICE_DEMAND_START
# 只把有值的機密放進環境變數：空字串在設定系統裡算是「有設定但為空」，
# 會蓋掉 appsettings.Production.json 裡的正確值（方式A就失效了）。
$envVars = @(
    "ASPNETCORE_URLS=http://localhost:$Port",
    "ASPNETCORE_ENVIRONMENT=Production"
)
if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    $envVars += "ConnectionStrings__PlanetDb=$ConnectionString"
}
if (-not [string]::IsNullOrWhiteSpace($AdminApiKey)) {
    $envVars += "Admin__ApiKey=$AdminApiKey"
}
& $NssmPath set $ServiceName AppEnvironmentExtra $envVars
& $NssmPath set $ServiceName AppStdout "$LogDir\service-stdout.log"
& $NssmPath set $ServiceName AppStderr "$LogDir\service-stderr.log"

Write-Host "已安裝服務 $ServiceName（手動啟動模式）。"
Write-Host ""
Write-Host "DB連線用SQL Server帳號驗證（不是Windows整合驗證），所以服務用預設的 LocalSystem 帳號執行即可。"
if ([string]::IsNullOrWhiteSpace($ConnectionString) -or [string]::IsNullOrWhiteSpace($AdminApiKey)) {
    $target = Join-Path (Split-Path $ExePath) "appsettings.Production.json"
    Write-Host ""
    Write-Host "沒有從參數提供連線字串／管理金鑰，請確認部署目錄有這個檔案：" -ForegroundColor Yellow
    Write-Host "  $target" -ForegroundColor Yellow
    Write-Host '  { "ConnectionStrings": { "PlanetDb": "..." }, "Admin": { "ApiKey": "..." } }' -ForegroundColor Yellow
    Write-Host "  少了連線字串服務會啟動失敗；少了管理金鑰則是 /api/admin 停用（回503）。" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "確認沒問題後，可用 '.\nssm.exe start $ServiceName' 啟動，"
Write-Host "測試穩定後再用 '.\nssm.exe set $ServiceName Start SERVICE_AUTO_START' 改成開機自動啟動。"
