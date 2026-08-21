@echo off
setlocal
cd /d "%~dp0"

echo.
echo === Comfee Remote - Debug Build ===
echo.
dotnet build -c Debug

echo.
pause
