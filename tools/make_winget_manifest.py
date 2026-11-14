"""Writes the WinGet manifests for taosecurity.BejView (step 5, 2026-11-13): a DRAFT for microsoft/winget-pkgs.

Run (after tools\\make_beta_zip.py):  python -I tools\\make_winget_manifest.py [--check-url]

It installs the GitHub release's BejView-<version>-win-x64-winget.zip as a portable app with the command "bejview".
That zip holds BejView.exe and the terminal helper as cli\\bejview.exe. The helper's file name equals the command on
purpose: WinGet makes a hard link named after the command next to the file it points at, so a command "bejview"
pointing at BejView.com would be a link called bejview.exe, which on Windows is BejView.exe, and WinGet would replace
the window program with the helper without a warning (winget-cli PortableFlow.cpp, read 2026-11-13).

The version comes from Directory.Build.props and the SHA-256 from dist\\SHA256SUMS. Output:
dist\\winget\\manifests\\t\\taosecurity\\BejView\\<version>\\ (version, installer and en-US locale files), then it runs
`winget validate` on them. --check-url also downloads the release asset and checks its SHA-256 matches (only works
once the GitHub release is published).
Sending the manifests to microsoft/winget-pkgs (wingetcreate submit, a pull request) needs the user's OK.
"""
import datetime
import hashlib
import os
import re
import shutil
import subprocess
import sys
import urllib.request

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
ID = "taosecurity.BejView"
REPO = "https://github.com/taosecurity/BejView"
SCHEMA = "1.28.0"  # the current winget-pkgs manifest schema (wingetcreate 1.28.1.0 writes it, 2026-11-11)


def version() -> str:
    with open(os.path.join(ROOT, "Directory.Build.props"), encoding="utf-8") as f:
        m = re.search(r"<Version>([^<]+)</Version>", f.read())
    if not m:
        raise SystemExit("Directory.Build.props has no <Version>.")
    return m.group(1)


def header(kind: str) -> str:
    return (f"# Draft made by BejView's tools/make_winget_manifest.py; check it with winget validate before a pull request.\n"
            f"# yaml-language-server: $schema=https://aka.ms/winget-manifest.{kind}.{SCHEMA}.schema.json\n\n")


def files(ver: str, sha: str, today: str) -> dict[str, str]:
    url = f"{REPO}/releases/download/v{ver}/BejView-{ver}-win-x64-winget.zip"
    return {
        f"{ID}.yaml": header("version") + f"""PackageIdentifier: {ID}
PackageVersion: {ver}
DefaultLocale: en-US
ManifestType: version
ManifestVersion: {SCHEMA}
""",
        f"{ID}.installer.yaml": header("installer") + f"""PackageIdentifier: {ID}
PackageVersion: {ver}
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: cli\\bejview.exe
  PortableCommandAlias: bejview
MinimumOSVersion: 10.0.19041.0
ReleaseDate: {today}
Installers:
- Architecture: x64
  InstallerUrl: {url}
  InstallerSha256: {sha.upper()}
ManifestType: installer
ManifestVersion: {SCHEMA}
""",
        f"{ID}.locale.en-US.yaml": header("defaultLocale") + f"""PackageIdentifier: {ID}
PackageVersion: {ver}
PackageLocale: en-US
Publisher: Richard Bejtlich
PublisherUrl: https://github.com/taosecurity
PublisherSupportUrl: {REPO}/issues
Author: Richard Bejtlich
PackageName: BejView
PackageUrl: {REPO}
License: MIT
LicenseUrl: {REPO}/blob/main/LICENSE
Copyright: Copyright (c) Richard Bejtlich
ShortDescription: View and search very large text files. Read-only.
Description: |-
  BejView opens text files of any size (tested with a 49.5 GB, billion-line file) without loading them into memory.
  It searches with plain text, whole words or regular expressions, follows growing logs like tail -f, filters
  lines, colours lines by pattern and keeps bookmarks. It never changes a file. The "bejview" command also prints
  file details (bejview info) and matching lines (bejview find) in a terminal.
Moniker: bejview
Tags:
- log-viewer
- text-viewer
- large-files
- logs
- search
- tail
ReleaseNotesUrl: {REPO}/releases/tag/v{ver}
ManifestType: defaultLocale
ManifestVersion: {SCHEMA}
""",
    }


def main() -> int:
    check_url = "--check-url" in sys.argv[1:]
    if any(a != "--check-url" for a in sys.argv[1:]):
        print(__doc__)
        return 2
    ver = version()
    zip_name = f"BejView-{ver}-win-x64-winget.zip"
    sums = os.path.join(ROOT, "dist", "SHA256SUMS")
    sha = None
    if os.path.isfile(sums):
        for line in open(sums, encoding="ascii"):
            h, _, name = line.strip().partition("  ")
            if name == zip_name:
                sha = h
    if not sha:
        raise SystemExit(f"{zip_name} isn't in {sums}. Run python -I tools\\make_beta_zip.py first.")
    if check_url:
        url = f"{REPO}/releases/download/v{ver}/{zip_name}"
        print(f"downloading {url}")
        with urllib.request.urlopen(url) as r:
            got = hashlib.sha256(r.read()).hexdigest()
        if got != sha:
            raise SystemExit(f"The release asset's SHA-256 is {got}, but dist\\SHA256SUMS says {sha}.")
        print("the release asset matches dist\\SHA256SUMS")
    out = os.path.join(ROOT, "dist", "winget", "manifests", "t", "taosecurity", "BejView", ver)
    shutil.rmtree(out, ignore_errors=True)
    os.makedirs(out)
    for name, text in files(ver, sha, datetime.date.today().isoformat()).items():
        with open(os.path.join(out, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print(f"wrote {os.path.join(out, name)}")
    r = subprocess.run(["winget", "validate", "--manifest", out], capture_output=True, text=True)
    print((r.stdout + r.stderr).strip())
    return 0 if r.returncode == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
