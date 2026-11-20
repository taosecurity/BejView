# BejView: instructions for AI assistants

These steps let an assistant install BejView and use it for a person. Every command below was run once before the 2.0.0 release and checked again for 2.0.1.

## 1. What it is for

BejView views and searches very large text files (logs, dumps; tested at 49.5 GB) on Windows and Linux without loading them into memory. Use it when a file is too big for an ordinary editor, when you need `grep`-style matches with line and column numbers on Windows, or to follow a growing log. It has a window and terminal commands. It only reads: it never changes, uploads or sends anything.

Don't use it to edit files, for binary files (it shows text in UTF-8, UTF-16 or Latin-1), or when the file is small and an ordinary tool will do.

## 2. Prerequisites

- Windows 10 version 2004 (build 19041) or later, x64; or Linux x64 or arm64 with a desktop session for the window (the terminal commands work without one).
- Nothing else: each download carries its own .NET 10 runtime.
- To build from source instead: the .NET 10 SDK (see DEVELOPMENT.md).

## 3. Install

Windows, any one of these:

```powershell
# a) The release zip, extracted to a folder of your choice
Invoke-WebRequest https://github.com/taosecurity/BejView/releases/download/v2.0.1/BejView-2.0.1-win-x64.zip -OutFile BejView.zip
Expand-Archive BejView.zip -DestinationPath "$env:LOCALAPPDATA\BejView-app"
# b) WinGet: the taosecurity.BejView package is still waiting for review (checked 2026-11-20); once accepted it adds the "bejview" command to PATH
winget install taosecurity.BejView
# c) The Microsoft Store version (2.0.1, live in the Store since 2026-11-16)
winget install --id 9PMXP946XKN3 --source msstore
```

Linux:

```sh
curl -LO https://github.com/taosecurity/BejView/releases/download/v2.0.1/BejView-2.0.1-linux-x64.tar.gz
tar -xzf BejView-2.0.1-linux-x64.tar.gz
cd BejView-2.0.1-linux-x64
./install.sh            # optional: copies bejview to ~/.local/bin and adds a menu entry (no sudo)
```

On Windows, call the zip's terminal helper as `BejView` (in PowerShell, `.\BejView` or the full path to `BejView.com`), never `BejView.exe`: the helper waits for the answer and passes the exit code back; `BejView.exe` returns at once and its text lands after the prompt. WinGet and the Store add `bejview`, which does the same.

## 4. Prove the install works

```powershell
& "$env:LOCALAPPDATA\BejView-app\BejView.com" --version     # Windows zip: prints "BejView 2.0.1", exit 0
```

```sh
./bejview --version                                          # Linux: prints "BejView 2.0.1", exit 0
```

## 5. Common tasks

Below, `bejview` stands for the command from step 3 (`.\BejView` in the zip folder on Windows, `./bejview` on Linux).

```text
bejview info big.log                          size, encoding, line count, indexing speed
bejview find big.log error                    every matching line as LINE:COLUMN: text
bejview find big.log ERROR --case --word      exact case, whole words
bejview find big.log "warn|error" --regex     a .NET regular expression
bejview find big.log error --count            only the number of matches
bejview find big.log error --max 100          list at most 100 (all are still counted)
bejview find big.log error > hits.txt         save the matches to a file
bejview big.log --line 5000 --find error      open the window at line 5000 and search
bejview big.log --follow                      open the window and follow the end (like tail -f)
bejview paths                                 where the settings and the log are
bejview --help                                every option
```

`--encoding utf8|utf16le|utf16be|latin1` overrides the automatic encoding choice. In `find` output, control characters in the file (escape sequences, carriage returns) are printed as `\xNN` codes, so a file can't change the terminal.

## 6. Inputs and outputs

- Input: one text file path. Any size; it may be open and growing in another program.
- `info` and `find` print to standard output; errors go to standard error and start with `BejView:`, then say what to do.
- Opening the window prints nothing (unless `--verbose`) and, on Windows, returns the prompt at once.
- BejView writes only its own settings file and log (below). It never writes to the file it reads. Ctrl+S in the window saves a selection to a new file, and refuses the file being shown.

## 7. Exit codes

| Code | Meaning |
|---|---|
| 0 | OK (`find`: at least one match) |
| 1 | `find`: no match |
| 2 | Bad arguments (the message names the option) |
| 3 | The file couldn't be opened or read |
| 4 | The search text can't be used (for example, a broken regular expression) |
| 5 | Windows: `BejView.com` couldn't find or start `BejView.exe` (keep them in one folder). Linux: a system library is missing (the message names the package) |

## 8. The log

- Windows zip and WinGet: `%APPDATA%\BejView\bejview.log`. Store version: under `%LOCALAPPDATA%\Packages\RichardBejtlich.BejView_kz4qzpv342p6y\LocalCache\Roaming\BejView`. Linux: `~/.config/BejView/bejview.log`.
- `bejview paths` prints the exact location.
- `--verbose` logs every step (what was read, what was decided, why something failed) and also prints it to the terminal. `--log FILE` writes the log to FILE instead.

## 9. Limits

- Read-only. It never changes, deletes, uploads or sends a file, and it has no network access and no self-update.
- Search results list the first 10,000 matches by default (`--max N` changes that); the count is always complete.
- Lines longer than 64 KB are cut short: the window shows their start with a "line continues" mark, and `find` prints their first 64 KB. Matches further along are still found and counted.
- The window needs a desktop session (Windows, or Linux with Wayland or X11). Under WSL, a file on `/mnt/<drive>` opens in the Windows `BejView.exe` if one can be found (`$BEJVIEW_WINDOWS_EXE`, then next to `bejview`, then the Windows PATH); `--wslg` uses the Linux window instead.
