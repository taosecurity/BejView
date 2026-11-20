# BejView

BejView lets you view and search very large text files on Windows and Linux. It opens logs and dumps of many gigabytes at once, searches them in seconds, follows a log while a program is still writing it, and never changes the file.

It has been tested with a 49.5 GB file of more than a billion lines. On a 30 GB file it uses under 100 MB of memory and shows the first screen in under a second (benchmark below).

What's new: see the [latest release](https://github.com/taosecurity/BejView/releases/latest) and [CHANGELOG.md](CHANGELOG.md).

For AI assistants: step-by-step instructions are in [AGENTS.md](AGENTS.md).

## Try it

Windows: download `BejView-2.0.1-win-x64.zip` from [Releases](https://github.com/taosecurity/BejView/releases), extract it anywhere, and double-click `BejView.exe`. Or, from PowerShell in that folder:

```powershell
.\BejView --version
.\BejView info .\LICENSE.txt
```

Linux:

```sh
tar -xzf BejView-2.0.1-linux-x64.tar.gz
cd BejView-2.0.1-linux-x64
./bejview --version
./bejview /var/log/syslog --follow
```

Nothing else needs installing: each download carries its own .NET runtime.

## Example

Here is how you might check a big log that a program is still writing:

1. Drag the log file onto the BejView window. The first screen shows at once while BejView counts the lines in the background.
2. Press **Ctrl+L** to follow the end of the file. New lines appear as the program writes them, like `tail -f`. (From a terminal, `bejview big.log --follow` opens the file and follows it in one step.)
3. Press **Ctrl+F**, type `error` and press Enter. BejView counts every match and lists them. F3 and Shift+F3 step through them.
4. Press **Ctrl+G** and type `50%` to jump to the middle of the file, a line number such as `12345`, or a byte offset such as `@1048576`.

The terminal commands print their answers instead of opening a window. This is the real output of the 2.0.1 Windows zip, run in its folder on the `LICENSE.txt` file that comes with it:

    > .\BejView --version
    BejView 2.0.1

    > .\BejView find LICENSE.txt software
    6:9: of this software and associated documentation files (the "Software"), to deal
    7:8: in the Software without restriction, including without limitation the rights
    9:15: copies of the Software, and to permit persons to whom the Software is
    13:39: copies or substantial portions of the Software.
    15:5: THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    20:34: OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    21:1: SOFTWARE.

    > .\BejView find LICENSE.txt software --count
    9

Each `find` line is `LINE:COLUMN: text`. The search ignores case unless you add `--case`, and `--count` counts every match, including the two on line 9.

## What it does

- **Opens huge files at once.** The first screen shows straight away while BejView counts the lines in the background. Memory stays small because the file is read from disk, never loaded whole.
- **Never changes a file.** BejView only reads. A log that another program holds open and keeps writing can stay open in BejView at the same time.
- **Search:** plain text, match case, whole words, or .NET regular expressions. Every match is counted; the first 10,000 are listed (Edit > Result limit changes that). F3 and Shift+F3 (or Ctrl+N and Ctrl+P) step through them.
- **Filter:** "Matching lines" or "Other lines" (like `grep -v`) show only those lines in a pane of their own. Click one to jump to it.
- **Follow a live log** (View > Follow the end of the file, Ctrl+L), like `tail -f`. It keeps up when the log is rotated (renamed away and started again under the same name).
- **Colors lines by pattern.** The built-in presets color errors, warnings and stack traces; View > Highlight rules lets you change them or write your own.
- **Go anywhere:** Ctrl+G takes a line number (`12345`), a percentage (`50%`) or a byte offset (`@1048576`). Bookmarks: Ctrl+F2 sets one, F2 and Shift+F2 move between them.
- **Copy and save out:** Ctrl+C copies (the current line if nothing is selected), Ctrl+Shift+C copies with line numbers, Ctrl+S saves the selection to a new file byte for byte. It refuses to save over the file it is showing.
- **Tabs, dark mode (Ctrl+D), text size (Ctrl++ and Ctrl+-), word wrap (Alt+Z), font and encoding choices** (UTF-8, UTF-16 and Latin-1, detected automatically), all remembered.
- **Terminal commands** that print file details and matching lines (below).

## Install

| Where | How | Notes |
|---|---|---|
| Windows zip | Extract `BejView-<version>-win-x64.zip` anywhere and run `BejView.exe`. | Windows 10 (2004) or later, x64. The zip isn't signed, so the first start may show "Windows protected your PC": click **More info**, then **Run anyway**. |
| Microsoft Store | [BejView in the Microsoft Store](https://apps.microsoft.com/detail/9PMXP946XKN3). Version 2.0.1 has been live there since 2026-11-16. | Signed by Microsoft; updates itself. Adds the `bejview` command to every terminal. |
| WinGet | `winget install --id 9PMXP946XKN3 --source msstore` installs the Store version. The `taosecurity.BejView` package is still waiting for review by the WinGet team (checked 2026-11-20), so `winget install taosecurity.BejView` works only after it is accepted. | The WinGet package uses `BejView-<version>-win-x64-winget.zip` and adds the `bejview` command. |
| Linux | Unpack `BejView-<version>-linux-x64.tar.gz` (or `-linux-arm64`) and run `./bejview`. `./install.sh` adds it to your app menu with its icon (into `~/.local`, no sudo); `./install.sh --uninstall` removes it. | Needs a desktop session. It draws natively on Wayland (made for KDE Plasma 6) and falls back to X11. `BEJVIEW_X11=1 ./bejview` forces X11. |

Every release lists SHA-256 hashes in `SHA256SUMS` and carries a GitHub build attestation. To check a download:

```sh
sha256sum -c SHA256SUMS --ignore-missing         # Linux
gh attestation verify BejView-2.0.1-linux-x64.tar.gz -R taosecurity/BejView
```

On Windows, compare `Get-FileHash BejView-2.0.1-win-x64.zip` with the line in `SHA256SUMS`.

BejView 2.0 replaces 1.0. The first time it starts on Windows it takes over 1.0's recent-files list.

## From a terminal

On Windows, type `BejView` without `.exe` (in PowerShell from its folder: `.\BejView`). That runs `BejView.com`, a small helper that waits for the answer and passes the text and exit code back to the terminal. Keep `BejView.com` next to `BejView.exe`. The Store and WinGet versions give you `bejview` everywhere. On Linux, it's `./bejview` (or `bejview` after `install.sh`).

```text
bejview big.log --follow                   open the window and follow the end
bejview big.log --line 5000 --find error   open at line 5000 and search
bejview info big.log                       size, encoding, line count, indexing speed
bejview find big.log error                 matching lines as LINE:COLUMN: text
bejview find big.log "warn|error" --regex --count
bejview paths                              where the settings and the log are
bejview --help                             every option
```

Search options: `-c`/`--case`, `-w`/`--word`, `-e`/`--regex`, `--max N`, `--count`. File option: `--encoding utf8|utf16le|utf16be|latin1`.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | OK (for `find`: something matched) |
| 1 | `find` matched nothing |
| 2 | Bad arguments |
| 3 | The file couldn't be opened or read |
| 4 | The search text can't be used (for example, a broken regular expression) |
| 5 | `BejView.com` couldn't find or start `BejView.exe` (keep the two files together), or on Linux a system library is missing (the message names the package to install) |

## WSL

Run the Linux build inside WSL and a file on a Windows drive (`/mnt/c/...`, `/mnt/d/...`) opens in the Windows `BejView.exe`, which reads Windows files much faster than Linux can through WSL. A file inside Linux (`/home/...`) opens in the Linux window under WSLg. `--wslg` always uses the Linux window. `info`, `find` and `paths` always run in Linux.

BejView looks for the Windows `BejView.exe` in `$BEJVIEW_WINDOWS_EXE` first, then next to the Linux `bejview`, then on the Windows PATH. For example, in `~/.bashrc`:

```sh
export BEJVIEW_WINDOWS_EXE="/mnt/c/Tools/BejView/BejView.exe"
```

A new WSL distribution is a minimal system, so the Linux window may need these libraries first (Debian or Ubuntu):

```sh
sudo apt install libfontconfig1 libx11-6 libice6 libsm6 libxrandr2 libxi6 libxcursor1
```

Unpack the `.tar.gz` inside WSL with `tar`, not with Windows Explorer, so `bejview` keeps its execute permission.

## Settings and the log

| | Windows (zip and WinGet) | Windows (Store) | Linux |
|---|---|---|---|
| Settings | `%APPDATA%\BejView\settings.json` | the Store's private copy: `%LOCALAPPDATA%\Packages\RichardBejtlich.BejView_kz4qzpv342p6y\LocalCache\Roaming\BejView` | `~/.config/BejView/settings.json` |
| Log | `%APPDATA%\BejView\bejview.log` | the same private folder | `~/.config/BejView/bejview.log` |

`bejview paths` prints the exact places on your computer, and Help > Open the log file opens the log. The Store version also uses files already in `%APPDATA%\BejView` (from a zip install) in place.

## If something goes wrong

Problems are always written to the log. For a full trace of every step (what BejView read, what it decided, and why something failed), start it with `--verbose`:

```powershell
.\BejView --verbose big.log           # Windows: the log also scrolls past in the terminal
./bejview --verbose big.log           # Linux
```

`--log FILE` writes the log somewhere else. Please attach the log when you [report a problem](https://github.com/taosecurity/BejView/issues).

## Benchmark

Measured on a 12-thread Windows PC with 64 GB of RAM and a warm file cache (generated logs of about 137-byte lines; `bench/` has the code and results).

| File | Index 1.0 | Index 2.0 | Memory 1.0 | Memory 2.0 | Search 1.0 | Search 2.0 |
|---|---|---|---|---|---|---|
| 1 GB (7.8 M lines) | 1.4 s | 0.20 s | 2.2 GB | 44 MB | 1.2 s | 0.12 s |
| 10 GB (78 M lines) | 11.3 s | 1.7 s | 4.4 GB | 62 MB | 10.7 s | 1.3 s |
| 30 GB (235 M lines) | 29.4 s | 4.7 s | 15.4 GB | 88 MB | 28.1 s | 2.8 s |

## Building from source

See [DEVELOPMENT.md](DEVELOPMENT.md). In short: .NET 10 SDK, then `dotnet test` and `python -I tools/make_beta_zip.py`.

## Changes

See [CHANGELOG.md](CHANGELOG.md).

## Privacy

BejView collects nothing, sends nothing and connects to nothing: [PRIVACY.md](PRIVACY.md).

## Security

What BejView promises (it only reads, has no network access, and keeps a file's escape codes away from your terminal) and how to report a problem privately: [SECURITY.md](SECURITY.md).

## License

BejView is released under the MIT License; see [LICENSE](LICENSE).

## Credits

I am the developer, and Claude is the programmer. Version 1.0 was programmed by Claude Sonnet 3.7, version 2.0 by Claude Opus 5.5. BejView 2.0 uses [Avalonia](https://avaloniaui.net/) for its window.
