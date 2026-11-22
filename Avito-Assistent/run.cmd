@echo off
chcp 65001 >nul
set DOTNET_ROLL_FORWARD=Major
set ROOT_DIR=%~dp0
if not exist "%ROOT_DIR%bin-cli\BuyerAssistant.Cli.dll" (
    dotnet build "%ROOT_DIR%src\BuyerAssistant.Cli\BuyerAssistant.Cli.csproj" -c Release -o "%ROOT_DIR%bin-cli" --nologo -v quiet 1>&2
)
dotnet "%ROOT_DIR%bin-cli\BuyerAssistant.Cli.dll" %*
