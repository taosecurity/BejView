# Security

## What BejView promises

- **It only reads.** Files are opened read-only and shared, so BejView never changes, truncates or deletes the file it shows. "Save selection" writes only to a new file you pick, and it refuses the file being shown.
- **No network.** BejView sends and downloads nothing and has no self-update. The only web link it opens is the README, from the Help menu, when you choose it.
- **Hostile files can't run anything.** A file's contents are only displayed and searched, never run or opened with another program.
- **Terminal output is safe.** `bejview find` and `bejview info` print control characters from the file (escape sequences, carriage returns) as `\xNN` codes, so a file can't change your terminal's title, colors or clipboard.
- **The WSL bridge never uses a shell.** Paths go to `wslpath` and to the Windows `BejView.exe` as separate arguments, after `--`, so a file name can't become an option or a command.
- **The terminal helper starts only its own BejView.exe**, by full path, from its install folder. It never searches the current folder.
- **Releases can be checked.** Every release lists SHA-256 hashes in `SHA256SUMS` and carries a GitHub build attestation (`gh attestation verify <file> -R taosecurity/BejView`). The Microsoft Store version is signed by Microsoft.

A very unusual file (huge lines, odd encodings) can make BejView slow, but it shouldn't crash BejView or make it write anything. If it does, that's a bug worth reporting.

## Supported versions

Only the latest release gets fixes. BejView 1.0 is no longer supported: it ran on .NET 9, and 2.0 replaces it.

## Reporting a problem

Please report a security problem privately through GitHub: on the repository's **Security** tab, choose **Report a vulnerability**. Include the BejView version (`bejview --version`), your system, and, if you can, a small file that shows the problem and the log from `--verbose` (README, "If something goes wrong").

Ordinary bugs go in [Issues](https://github.com/taosecurity/BejView/issues).
