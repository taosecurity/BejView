#!/usr/bin/env bash
# Times BejView 1.0 against 2.0 on generated log files (Git Bash on Windows, or bash on Linux).
# Usage: bash bench/run-bench.sh [DIR] [SIZES_MB...]
#   DIR defaults to C:/temp/bejview-bench; sizes default to 1024 10240 30720.
# The files are deleted at the end. Results go to bench/results-<date>.txt.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
dir="${1:-C:/temp/bejview-bench}"
shift || true
sizes=("$@")
[ ${#sizes[@]} -eq 0 ] && sizes=(1024 10240 30720)
exe="$here/BejView.Bench/bin/Release/net10.0/BejView.Bench"
[ -f "$exe.exe" ] && exe="$exe.exe"
out="$here/results-$(date +%Y-%m-%d).txt"
mkdir -p "$dir"
{
  echo "# BejView benchmark $(date '+%Y-%m-%d %H:%M') on $(hostname), $(nproc 2>/dev/null || echo '?') logical CPUs"
  echo "# warm cache: each file is read once before timing (both versions then read from the OS file cache)"
  for mb in "${sizes[@]}"; do
    f="$dir/bench-${mb}MB.log"
    "$exe" gen "$f" "$mb"
    cat "$f" > /dev/null
    "$exe" new "$f" zqxj
    "$exe" legacy "$f" zqxj || echo "mode=legacy error=exit_$?"
    rm -f "$f"
  done
} 2>&1 | tee "$out"
