@echo off

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrative privileges...
    powershell -Command "Start-Process '%~f0' -Verb runAs"
    exit /b
)

echo [Uninstaller] Running with administrative privileges...
echo Stopping TinyCam service...
sc stop TinyCam >nul 2>&1
sc delete TinyCam >nul 2>&1

echo Removing program files...
rd /s /q "C:\Program Files\TinyCam"

echo TinyCam has been completely removed.
pause