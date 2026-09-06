param(
    [string]$NssmPath = ".\nssm.exe",
    [string]$ServiceName = "PlanetService"
)

# 嫌打字麻煩的話可以直接雙擊同目錄下的 uninstall-service.bat，它會自動跳UAC提權後幫你呼叫這支腳本。

& $NssmPath stop $ServiceName
& $NssmPath remove $ServiceName confirm

Write-Host "已移除服務 $ServiceName"
