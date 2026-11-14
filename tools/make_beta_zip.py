"""Builds the release (and beta) packages in dist\\:
  - BejView-<version>-win-x64.zip: BejView.exe, BejView.com (the terminal helper), README.md, AGENTS.md, CHANGELOG.md,
    LICENSE.txt (at the top of the zip, no folder inside, since beta 9; the three .md files since 2.0.0)
  - BejView-<version>-win-x64-winget.zip (since beta 10, step 5): the same, but the helper is cli\\bejview.exe, for
    WinGet (see main() for why)
  - BejView-<version>-linux-x64.tar.gz and -linux-arm64.tar.gz (since beta 8, 2026-11-13): bejview (single file,
    self-contained, executable), install.sh, bejview.desktop, icons, the same documents, in a folder BejView-<version>-linux-<arch>/
  - SHA256SUMS (since beta 10): the SHA-256 of each package above, in the sha256sum format

Run (Windows, from anywhere):  python -I tools\\make_beta_zip.py
The version comes from Directory.Build.props. The script publishes every program:
  - src\\BejView.App single-file, self-contained, for win-x64, linux-x64 and linux-arm64
  - src\\BejView.Launcher with Native AOT (BejView.Launcher.exe, shipped as BejView.com)
Native AOT needs Visual Studio 2022 Build Tools with "Desktop development with C++"; the script puts vswhere.exe on PATH.
Before zipping it checks the pair: BejView.com --version must print the version, and a bad option must return 2.
The Linux builds are checked in WSL by hand (NOTES.md, "Building and testing"); arm64 can't run here.
Add --no-build to package what is already in dist\\publish-*.
"""
import hashlib
import io
import os
import re
import shutil
import subprocess
import sys
import tarfile
import time
import zipfile

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
APP_OUT = os.path.join(ROOT, "dist", "publish-win-x64")
LAUNCHER_OUT = os.path.join(ROOT, "dist", "publish-launcher")
LINUX_ARCHES = ("x64", "arm64")
# The documents in every package (step 6, 2026-11-14: README, AGENTS.md and CHANGELOG replace beta\README-beta.txt).
DOCS = [(os.path.join(ROOT, n), n) for n in ("README.md", "AGENTS.md", "CHANGELOG.md")] + \
       [(os.path.join(ROOT, "LICENSE"), "LICENSE.txt")]


def linux_out(arch: str) -> str:
    return os.path.join(ROOT, "dist", f"publish-linux-{arch}")
VS_INSTALLER = os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), "Microsoft Visual Studio", "Installer")


def version() -> str:
    with open(os.path.join(ROOT, "Directory.Build.props"), encoding="utf-8") as f:
        m = re.search(r"<Version>([^<]+)</Version>", f.read())
    if not m:
        raise SystemExit("Directory.Build.props has no <Version>.")
    return m.group(1)


def run(cmd: list[str], env: dict) -> None:
    print(">", " ".join(cmd))
    r = subprocess.run(cmd, cwd=ROOT, env=env)
    if r.returncode:
        raise SystemExit(f"failed (exit {r.returncode}): {' '.join(cmd)}")


def build() -> None:
    env = dict(os.environ)
    if not os.path.isfile(os.path.join(VS_INSTALLER, "vswhere.exe")):
        raise SystemExit(f"vswhere.exe not found in {VS_INSTALLER}. Install Visual Studio 2022 Build Tools "
                         "with 'Desktop development with C++' (Native AOT needs its linker).")
    env["PATH"] = VS_INSTALLER + os.pathsep + env.get("PATH", "")
    for d in (APP_OUT, LAUNCHER_OUT, *[linux_out(a) for a in LINUX_ARCHES]):
        shutil.rmtree(d, ignore_errors=True)
    single_file = ["--self-contained", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                   "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none"]
    run(["dotnet", "publish", r"src\BejView.App\BejView.App.csproj", "-c", "Release", "-r", "win-x64", *single_file,
         "-o", APP_OUT], env)
    run(["dotnet", "publish", r"src\BejView.Launcher\BejView.Launcher.csproj", "-c", "Release", "-r", "win-x64",
         "-p:DebugType=none", "-o", LAUNCHER_OUT], env)
    for arch in LINUX_ARCHES:
        run(["dotnet", "publish", r"src\BejView.App\BejView.App.csproj", "-c", "Release", "-r", f"linux-{arch}",
             *single_file, "-o", linux_out(arch)], env)


def make_tar(ver: str, arch: str) -> str:
    """A .tar.gz whose bejview is executable (mode 755) once unpacked on Linux; files are owned by root:root, 0/0."""
    folder = f"BejView-{ver}-linux-{arch}"
    out = os.path.join(ROOT, "dist", folder + ".tar.gz")
    linux = os.path.join(ROOT, "packaging", "linux")
    files = [
        (os.path.join(linux_out(arch), "BejView"), "bejview", 0o755),
        (os.path.join(linux, "install.sh"), "install.sh", 0o755),
        (os.path.join(linux, "bejview.desktop"), "bejview.desktop", 0o644),
        *[(src, name, 0o644) for src, name in DOCS],
    ]
    icons = os.path.join(linux, "icons")
    for dirpath, _, names in sorted(os.walk(icons)):
        for n in sorted(names):
            src = os.path.join(dirpath, n)
            files.append((src, os.path.relpath(src, linux).replace(os.sep, "/"), 0o644))
    for src, _, _ in files:
        if not os.path.isfile(src):
            raise SystemExit(f"missing: {src}")
    now = int(time.time())
    with tarfile.open(out, "w:gz", compresslevel=9) as t:
        made = set()

        def add_dir(path: str) -> None:
            if path in made:
                return
            parent = os.path.dirname(path)
            if parent:
                add_dir(parent)
            d = tarfile.TarInfo(path)
            d.type, d.mode, d.mtime = tarfile.DIRTYPE, 0o755, now
            t.addfile(d)
            made.add(path)

        add_dir(folder)
        for src, name, mode in files:
            add_dir(os.path.dirname(f"{folder}/{name}"))
            data = open(src, "rb").read()
            if name.endswith((".txt", ".md", ".sh", ".desktop")):
                data = data.replace(b"\r\n", b"\n")  # Linux line endings (a CRLF script won't run)
            info = tarfile.TarInfo(f"{folder}/{name}")
            info.size, info.mode, info.mtime = len(data), mode, now
            t.addfile(info, io.BytesIO(data))
    with tarfile.open(out) as t:
        for m in t.getmembers():
            print(f"  {m.size:>12,}  {oct(m.mode)[2:]}  {m.name}")
    sha = hashlib.sha256(open(out, "rb").read()).hexdigest()
    print(f"wrote {out} ({os.path.getsize(out):,} bytes)\nSHA-256 {sha}")
    return out


def check_pair(helper: str, ver: str) -> None:
    """The helper must find BejView.exe, wait for it, and pass its text and exit codes back."""
    name = os.path.basename(helper)
    r = subprocess.run([helper, "--version"], capture_output=True, text=True)
    if r.returncode != 0 or r.stdout.strip() != f"BejView {ver}":
        raise SystemExit(f"{name} --version gave {r.stdout.strip()!r} (exit {r.returncode}).")
    r = subprocess.run([helper, "--no-such-option"], capture_output=True, text=True)
    if r.returncode != 2 or "BejView:" not in r.stderr:
        raise SystemExit(f"{name} --no-such-option returned {r.returncode}, expected 2 with an error line.")
    print(f"checked: BejView {ver} via {os.path.relpath(helper, os.path.dirname(os.path.dirname(helper)))}; a bad option returns 2")


def make_zip(stage: str, files: list[tuple[str, str]], helper: str, ver: str) -> str:
    """Copies the files into a staging folder, checks the helper there, and zips them (no folder inside the zip)."""
    out = stage + ".zip"
    for src, _ in files:
        if not os.path.isfile(src):
            raise SystemExit(f"missing: {src}")
    shutil.rmtree(stage, ignore_errors=True)
    for src, name in files:
        os.makedirs(os.path.dirname(os.path.join(stage, name)), exist_ok=True)
        shutil.copy2(src, os.path.join(stage, name))
    check_pair(os.path.join(stage, helper), ver)
    if os.path.exists(out):
        os.remove(out)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for _, name in files:
            # No folder inside the zip (since beta 9): Explorer's "Extract All" already makes one named after the zip,
            # so an inner folder gave BejView-...-win-x64\BejView-...-win-x64 (the user's beta 8 test, 2026-11-13).
            z.write(os.path.join(stage, name), name.replace(os.sep, "/"))
    with zipfile.ZipFile(out) as z:
        for i in z.infolist():
            print(f"  {i.file_size:>12,}  {i.filename}")
    print(f"wrote {out} ({os.path.getsize(out):,} bytes)\nSHA-256 {sha256(out)}")
    return out


def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def write_sums(outputs: list[str]) -> str:
    """dist\\SHA256SUMS in the sha256sum format (hash, two spaces, name; LF), so `sha256sum -c SHA256SUMS` works."""
    path = os.path.join(ROOT, "dist", "SHA256SUMS")
    with open(path, "w", encoding="ascii", newline="\n") as f:
        for o in outputs:
            f.write(f"{sha256(o)}  {os.path.basename(o)}\n")
    print(f"wrote {path}")
    return path


def main() -> int:
    args = [a for a in sys.argv[1:] if a != "--no-build"]
    if args:
        print("usage: make_beta_zip.py [--no-build]   (the version comes from Directory.Build.props)")
        return 2
    ver = version()
    if "--no-build" not in sys.argv:
        build()
    exe = (os.path.join(APP_OUT, "BejView.exe"), "BejView.exe")
    helper = os.path.join(LAUNCHER_OUT, "BejView.Launcher.exe")
    docs = list(DOCS)
    outputs = [make_zip(os.path.join(ROOT, "dist", f"BejView-{ver}-win-x64"),
                        [exe, (helper, "BejView.com"), *docs], "BejView.com", ver)]
    # The WinGet zip (step 5, 2026-11-13): WinGet's portable install makes a hard link named after the command next to
    # the file it points at. With the command "bejview" pointing at BejView.com that link would be bejview.exe, the
    # same name as BejView.exe on Windows, and WinGet would silently replace the window program with the helper. So
    # here the helper is cli\bejview.exe (file name = command, so no hard link) and finds BejView.exe one folder up.
    outputs.append(make_zip(os.path.join(ROOT, "dist", f"BejView-{ver}-win-x64-winget"),
                            [exe, (helper, os.path.join("cli", "bejview.exe")), *docs],
                            os.path.join("cli", "bejview.exe"), ver))
    for arch in LINUX_ARCHES:
        outputs.append(make_tar(ver, arch))
    write_sums(outputs)
    return 0


if __name__ == "__main__":
    sys.exit(main())
