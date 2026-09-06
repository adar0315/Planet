@echo off
chcp 65001 >nul
:: uninstall-service.ps1 的雙擊執行版本。移除Windows服務需要系統管理員權限，
:: 這裡用UAC自動重新以管理員身分啟動自己（跳出UAC提示視窗，按「是」即可）。
>nul 2>&1 net session
if '%errorlevel%' NEQ '0' (
    echo 需要系統管理員權限，正在請求UAC提權...
    goto UACPrompt
) else ( goto gotAdmin )

:UACPrompt
    echo Set UAC = CreateObject^("Shell.Application"^) > "%temp%\getadmin.vbs"
    echo UAC.ShellExecute "%~s0", "%*", "", "runas", 1 >> "%temp%\getadmin.vbs"
    "%temp%\getadmin.vbs"
    del "%temp%\getadmin.vbs" >nul 2>&1
    exit /B

:gotAdmin
    if exist "%temp%\getadmin.vbs" ( del "%temp%\getadmin.vbs" )
    pushd "%CD%"
    CD /D "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-service.ps1" %*

echo.
pause
