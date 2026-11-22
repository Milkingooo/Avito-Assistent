@echo off
chcp 65001 >nul
echo Building self-contained single-file Linux x64 binary...
dotnet publish "%~dp0src\BuyerAssistant.Cli\BuyerAssistant.Cli.csproj" ^
    -c Release ^
    -r linux-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none ^
    -o "%~dp0bin-linux"
echo Done! Binary saved to bin-linux\BuyerAssistant.Cli
