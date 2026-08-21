@echo off
title Comfee Remote Publish

echo === Comfee Remote - Single EXE Publish ===
echo.

if exist "bin\Release\net8.0-windows\win-x64\publish" (
    echo Alter Publish-Ordner wird geloescht...
    rmdir /S /Q "bin\Release\net8.0-windows\win-x64\publish"
)

echo.
echo Projekt wird veroeffentlicht...
dotnet publish "ComfeeRemote.csproj" -c Release -r win-x64 --self-contained true

echo.
echo Fertig:
echo bin\Release\net8.0-windows\win-x64\publish\
echo.

pause