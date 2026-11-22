@echo off
chcp 65001 >nul
set PORT=5000
set DOTNET_ROLL_FORWARD=LatestMajor
echo Starting Smart Buyer Assistant Web UI on http://localhost:%PORT% ...
dotnet run --project "%~dp0BuyerAssistant.Web.csproj" -c Release
