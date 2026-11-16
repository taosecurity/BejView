# Changelog

## 2.0.1 (2026-11-16)

Bug fixes from a code review of the 2.0.0 source.

- Regex searches with `$` (end of line) now find matches in Windows (CRLF) files, such as most game and Vortex logs.
- Follow mode: a log that is rotated but starts with the same long header every time is now followed to the new file; a file opened empty and later rewritten in place is read again; the Filter pane is cleared when the file is read again, instead of listing the old file's line numbers.
- A search, or Save selection, that runs while a followed log is rotated reads the file it started on, not the new one at the old offsets.
- Follow mode no longer can stop drawing when a line over 64 KB is cut short under it.
- `--encoding` applies to the file given on the command line only; files opened later are detected again.
- Closing a tab, or changing its encoding, while follow mode is reading no longer logs an "Unexpected background error".
- A Go to that was waiting for indexing is no longer dropped silently when indexing stops: BejView goes to the last line read and says so.
- Faster column numbers when one long line has many search hits.

## 2.0.0 (2026-11-14)

A rewrite of BejView on .NET 10 and Avalonia, for Windows and Linux. It replaces 1.0 and takes over 1.0's recent-files list the first time it starts on Windows.

**Faster, smaller, never stuck**
- Opens files of any size at once (tested at 49.5 GB, over a billion lines): the first screen shows straight away while the lines are counted in the background.
- On a 30 GB file, indexing takes 4.7 s instead of 29.4 s, memory is 88 MB instead of 15.4 GB, and a search takes 2.8 s instead of 28.1 s (README, "Benchmark").
- Reads only: files are opened read-only and shared, so a log another program is writing stays open in both.
- A hostile or odd file (huge lines, mixed encodings, a file cut short or growing while open) can slow BejView down but not crash it.

**New in the window**
- Tabs, drag and drop, and File > Open recent.
- Search with match case, whole words and regular expressions; every match is counted and the first 10,000 are listed (adjustable). The match itself is highlighted.
- Filter: show only the lines that match, or only those that don't.
- Follow mode (Ctrl+L) for live logs, like `tail -f`, which keeps up when the log is rotated.
- Highlight rules that color lines by pattern, with presets.
- Go to a line, a percentage or a byte offset (Ctrl+G); bookmarks (Ctrl+F2, F2, Shift+F2).
- Copy with line numbers (Ctrl+Shift+C); save a selection, or the filtered lines, to a new file byte for byte (Ctrl+S).
- UTF-8, UTF-16 and Latin-1, detected automatically or chosen from the Encoding menu.
- Dark mode, font, text size and word wrap, all remembered. 1.0's shortcuts (Ctrl+B, Ctrl+E, Ctrl+N, Ctrl+P, Ctrl+D) still work.

**New: terminal commands**
- `bejview info`, `bejview find` (grep-style `LINE:COLUMN: text`, with `--case`, `--word`, `--regex`, `--count`, `--max`), `bejview paths`, and options to open the window at a line, with a search, or following.
- Exit codes for scripts (README, "From a terminal").
- On Windows, the helper `BejView.com` makes the terminal wait for the answer.
- `find` prints control characters from the file as `\xNN` codes, so a file's escape sequences can't change the terminal.

**New: Linux and WSL**
- A Linux version (x64 and arm64): native Wayland, made for KDE Plasma 6, with X11 as the fallback. `install.sh` adds a menu entry and icons without sudo.
- Under WSL, files on Windows drives open in the Windows BejView, which reads them much faster.

**New: packages**
- Windows zip, a WinGet package, and a Microsoft Store version with the `bejview` command.
- Every release has SHA-256 hashes and a GitHub build attestation.

**Troubleshooting**
- Every problem is logged; `--verbose` logs every step and `--log FILE` picks the file. Help > Open the log file and `bejview paths` show where it is.

## 1.0.0 (2025-04-09)

The first version: a Windows Forms viewer for large text files on .NET 9, with search, line numbers, dark mode, font size, recent files and a RAM meter. Its code is kept unchanged in `legacy/winforms-1.0`, and tag `v1.0.0` has it at its original paths.
