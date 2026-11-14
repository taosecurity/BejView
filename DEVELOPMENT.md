# Building and changing BejView

BejView 2.0 is C# on .NET 10 with [Avalonia](https://avaloniaui.net/) 12.1.4 for the window, so one code base runs on Windows and Linux.

## What you need

- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- Python 3.12 or later for the packaging scripts in `tools/`.
- For the Windows packages: Visual Studio 2022 Build Tools with "Desktop development with C++" (the terminal helper is built with Native AOT, which needs its linker).
- For the Store package only: the Windows SDK (`makeappx`, `signtool`) and Pillow (`pip install pillow`).
- On Linux, for the UI tests: `fontconfig` and a font such as `fonts-dejavu-core`.

## Layout

| Folder | What it holds |
|---|---|
| `src/BejView.Core` | Everything that isn't UI: read-only file access, encoding detection, the sparse line index, the parallel search, settings, logging, the command line, platform paths and the WSL bridge. |
| `src/BejView.App` | The Avalonia window (exe `BejView`): the virtual text view, tabs, search bar, Results and Filter panes, follow mode, bookmarks, dialogs, and the terminal commands. |
| `src/BejView.Launcher` | The small terminal helper, shipped as `BejView.com` (and as `cli\bejview.exe` in the WinGet zip). It starts `BejView.exe` and waits when there is text to show. |
| `tests/BejView.Tests` | xUnit v3 tests, including headless UI tests. The index and search are checked against a slow, plain model on random text in every encoding. |
| `tests/BejView.LegacyProof` | Compiles the unchanged 1.0 file handler and demonstrates each problem 2.0 fixes. |
| `bench/` | `BejView.Bench` makes log-like files and times 1.0 against 2.0; `run-bench.sh` writes the results. |
| `packaging/` | The Linux `.desktop` file, icons and `install.sh`; the MSIX manifest and its test script. |
| `tools/` | Packaging scripts (below) and the WSL test helpers. |
| `legacy/winforms-1.0` | The 1.0 WinForms app, kept unchanged and not built. Tag `v1.0.0` has it at its original paths. |

## Build and test

From the repository root:

```sh
dotnet test tests/BejView.Tests/BejView.Tests.csproj
dotnet test tests/BejView.LegacyProof/BejView.LegacyProof.csproj
dotnet run --project src/BejView.App -- --version
```

On Linux, set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` first (BejView builds with invariant globalization).

Packages are pinned with lock files (`packages.lock.json`); CI restores with `--locked-mode`. Warnings are errors.

## Packages

```powershell
python -I tools\make_beta_zip.py               # dist\: the Windows zip, the WinGet zip, the Linux .tar.gz files, SHA256SUMS
python -I tools\make_msix.py [--test-sign]     # dist\msix\: the Store package (after make_beta_zip.py)
python -I tools\make_winget_manifest.py        # dist\winget\: the WinGet manifests, checked with winget validate
```

The version comes from `<Version>` in `Directory.Build.props`. `make_beta_zip.py` builds on Windows (it runs the helper to check the pair); `--no-build` packages what is already in `dist\publish-*`.

## Releases

`.github/workflows/ci.yml` tests on Windows and Linux on every push. Pushing a tag `v<version>` (matching `Directory.Build.props`) runs `release.yml`: it tests, builds the packages, attests their build provenance, and makes a **draft** GitHub release with the packages and `SHA256SUMS`. A person publishes the draft. The unsigned Store package is kept as a workflow artifact for Partner Center.

## Rules the code keeps

- BejView never writes to a file it shows: files are opened read-only and shared for reading, writing and deleting.
- Memory stays flat on huge files: a sparse line index, and only the visible lines are read and laid out.
- The WSL bridge passes paths to `BejView.exe` as an argument list, never through a shell. Text printed by `find` has control characters made visible, so a file can't send escape sequences to the terminal.
- Every failure is logged; `--verbose` logs every step.
