#!/bin/bash
# End-to-end check of the Linux build and the WSL bridge, run in WSL from Windows PowerShell:
#   wsl -d Debian -- bash /mnt/c/<path to>/BejView/tools/wsl_bridge_check.sh <version> <test-folder-on-mnt>
# It unpacks dist/BejView-<version>-linux-x64.tar.gz into ~/bjv-check, then runs the terminal commands and each bridge
# decision with --verbose. Windows exes it starts are left running; the caller lists and stops them (their command
# lines prove what the bridge passed). Needs dist/BejView-<version>-win-x64/ (made by make_beta_zip.py).
set -u
ver="$1"
testdir="$2"   # a /mnt/... folder, ideally with a space in its name
repo="$(cd "$(dirname "$0")/.." && pwd)"
tarball="$repo/dist/BejView-$ver-linux-x64.tar.gz"
winexe="$repo/dist/BejView-$ver-win-x64/BejView.exe"

rm -rf ~/bjv-check && mkdir -p ~/bjv-check && tar -xzf "$tarball" -C ~/bjv-check
bv=~/bjv-check/BejView-$ver-linux-x64/bejview
ls -l "$bv"
mkdir -p "$testdir"
printf 'line one\nline two has ERROR in it\nline three\n' > "$testdir/sample log.txt"
printf 'linux side\nERROR here too\n' > ~/bjv-check/linux.log
unset BEJVIEW_WINDOWS_EXE
step() { echo; echo "### $*"; }

step "version and help (last lines)"
"$bv" --version; echo "exit $?"
"$bv" --help | sed -n '/WSL:/,/--wslg/p'

step "info and find on a /mnt file run in Linux"
"$bv" info "$testdir/sample log.txt" --verbose 2>&1 | grep -E "WSL bridge|Lines|Encoding|lines"; echo "exit ${PIPESTATUS[0]}"
"$bv" find "$testdir/sample log.txt" ERROR; echo "exit $?"
"$bv" find "$testdir/sample log.txt" NOPE; echo "exit $? (1 expected)"
"$bv" paths

step "no BejView.exe anywhere: warning, falls back to the Linux window (killed after 6 s)"
PATH=/usr/local/bin:/usr/bin:/bin timeout 6 "$bv" "$testdir/sample log.txt" 2>&1 | head -5; echo "exit ${PIPESTATUS[0]} (124 = still running at 6 s, i.e. the window was up)"

step "a wrong BEJVIEW_WINDOWS_EXE is reported"
BEJVIEW_WINDOWS_EXE=/mnt/c/nope/BejView.exe PATH=/usr/bin:/bin timeout 6 "$bv" --verbose "$testdir/sample log.txt" 2>&1 | grep -E "WSL bridge" | head -5

step "bridge: /mnt file with options goes to the Windows exe, terminal free at once"
start=$(date +%s%N)
BEJVIEW_WINDOWS_EXE="$winexe" "$bv" "$testdir/sample log.txt" --line 2 --find ERROR --follow; code=$?
end=$(date +%s%N)
echo "exit $code after $(( (end - start) / 1000000 )) ms"

step "bridge decisions with --verbose (the Windows window is closed by the caller; killed here after 8 s)"
cd "$testdir"
BEJVIEW_WINDOWS_EXE="$winexe" timeout 8 "$bv" --verbose "sample log.txt" --log bridge.log 2>&1 | grep -E "WSL bridge|starting" | head -8

step "--wslg forces the Linux window for a /mnt file (killed after 8 s)"
BEJVIEW_WINDOWS_EXE="$winexe" timeout 8 "$bv" --wslg --verbose "$testdir/sample log.txt" 2>&1 | grep -E "WSL bridge|Opened|Indexed|rror" | head -8; echo "exit ${PIPESTATUS[0]} (124 = window was up)"

step "a Linux file stays in Linux (killed after 8 s)"
BEJVIEW_WINDOWS_EXE="$winexe" timeout 8 "$bv" --verbose ~/bjv-check/linux.log 2>&1 | grep -E "WSL bridge|Opened|Indexed|rror" | head -8; echo "exit ${PIPESTATUS[0]}"

step "Linux log file"
tail -n 5 ~/.config/BejView/bejview.log
