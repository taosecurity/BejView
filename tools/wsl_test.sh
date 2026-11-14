#!/bin/bash
# Runs the BejView tests in WSL (Linux), on a copy of the source in ~/bjv (bin/obj must not collide with Windows builds).
# Run from Windows PowerShell:  wsl -d Debian -- bash /mnt/c/<path to>/BejView/tools/wsl_test.sh [runs] [filter]
# .NET 10 is in ~/.dotnet (unpacked from a tarball; WSL Debian has no ICU, hence the invariant mode).
set -e
runs="${1:-1}"
filter="${2:-}"
src="$(cd "$(dirname "$0")/.." && pwd)"
rm -rf ~/bjv && mkdir -p ~/bjv
cd "$src"
tar --exclude=bin --exclude=obj --exclude=.git -cf - Directory.Build.props BejView.slnx src tests legacy packaging | tar -xf - -C ~/bjv
cd ~/bjv
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
for i in $(seq 1 "$runs"); do
  echo "=== run $i of $runs ==="
  if [ -n "$filter" ]; then
    ~/.dotnet/dotnet test tests/BejView.Tests/BejView.Tests.csproj --filter "$filter" 2>&1 | grep -E "error|Failed|Passed!|failed" || true
  else
    ~/.dotnet/dotnet test tests/BejView.Tests/BejView.Tests.csproj 2>&1 | grep -E "error|Failed|Passed!|failed" || true
  fi
done
