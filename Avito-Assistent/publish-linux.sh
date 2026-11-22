#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

dotnet publish "$ROOT_DIR/src/BuyerAssistant.Cli/BuyerAssistant.Cli.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -o "$ROOT_DIR/bin-linux"

chmod +x "$ROOT_DIR/bin-linux/BuyerAssistant.Cli"
echo "Готово: автономный Linux-бинарник сохранён в $ROOT_DIR/bin-linux/BuyerAssistant.Cli"
