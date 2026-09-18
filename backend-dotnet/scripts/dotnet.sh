#!/usr/bin/env bash
# 本机 WorkBuddy 的 bash / PowerShell 会话会剥掉部分 Windows 环境变量，
# 导致 NuGet 的 NuGetEnvironment.CalculateFolderPath 取到 null，抛出
# "Value cannot be null. (Parameter 'path1')"。
# 根因见 NuGet.Common/PathUtil/NuGetEnvironment.cs 的 MachineWideSettingsBaseDirectory 分支：
# 它读取 PROGRAMFILES(X86) / PROGRAMFILES，两者都为空时 Path.Combine(null, "NuGet") 直接抛错。
#
# 本脚本补齐这些变量后转发给 dotnet，保证还原与编译可用。
#
# 用法：scripts/dotnet.sh build OpenAICanvas.sln
set -euo pipefail

user="${USERNAME:-Administrator}"

exec env \
  "PROGRAMFILES(X86)=C:\\Program Files (x86)" \
  "PROGRAMFILES=C:\\Program Files" \
  "PROGRAMDATA=C:\\ProgramData" \
  "APPDATA=C:\\Users\\${user}\\AppData\\Roaming" \
  "LOCALAPPDATA=C:\\Users\\${user}\\AppData\\Local" \
  "USERPROFILE=C:\\Users\\${user}" \
  "HOMEDRIVE=C:" \
  "HOMEPATH=\\Users\\${user}" \
  "DOTNET_CLI_HOME=C:\\Users\\${user}" \
  dotnet "$@"
