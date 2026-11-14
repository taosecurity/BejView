"""Builds BejView's Microsoft Store package (MSIX) from what tools\\make_beta_zip.py published (step 5, 2026-11-13).

Run (Windows, after make_beta_zip.py):  python -I tools\\make_msix.py [--test-sign]

File-system write virtualization stays at Windows' default (the user's decision, 2026-11-13): BejView's settings and
log go to the package's private copy of %APPDATA%\\BejView, and BejView shows and opens that copy.

  --test-sign    also writes a copy signed with a self-signed test certificate whose subject is the Store Publisher,
                 so it can be installed for testing (the Store signs the real one). The certificate is made once and
                 kept outside the repository (never in git): in $BEJVIEW_TEST_CERT_DIR if set, else
                 %LOCALAPPDATA%\\BejView-dev\\msix-test-cert. It holds BejView-test.pfx, its password in password.txt,
                 and BejView-test.cer, the public part a tester trusts.
  --cert-dir DIR another place for the test certificate.

Writes dist\\msix\\BejView-<version>-x64.msix (unsigned, for the Store upload) and, with --test-sign,
dist\\msix\\BejView-<version>-x64-test.msix. Package version: 2.0.0 -> 2.0.0.0; a beta 2.0.0-beta.N ->
1.99.N.0, so the real 2.0.0 always installs over a test beta (the Store wants the last number 0).
Needs the Windows 10/11 SDK (makeappx, makepri, signtool) and Pillow (the tile images).
Identity values come from packaging\\store-identity.json, the manifest from packaging\\msix\\AppxManifest.xml.
"""
import glob
import json
import os
import re
import secrets
import shutil
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_icon  # noqa: E402  (tools\make_icon.py: the BejView icon drawing)
from PIL import Image  # noqa: E402

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
APP_OUT = os.path.join(ROOT, "dist", "publish-win-x64")
LAUNCHER_OUT = os.path.join(ROOT, "dist", "publish-launcher")
OUT = os.path.join(ROOT, "dist", "msix")
DEFAULT_CERT_DIR = os.environ.get("BEJVIEW_TEST_CERT_DIR") or os.path.join(
    os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "BejView-dev", "msix-test-cert")
SDK_BIN = os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), "Windows Kits", "10", "bin")


def version() -> str:
    with open(os.path.join(ROOT, "Directory.Build.props"), encoding="utf-8") as f:
        m = re.search(r"<Version>([^<]+)</Version>", f.read())
    if not m:
        raise SystemExit("Directory.Build.props has no <Version>.")
    return m.group(1)


def package_version(ver: str) -> str:
    m = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)(?:-beta\.(\d+))?", ver)
    if not m:
        raise SystemExit(f"Can't turn version {ver!r} into a package version (expected 2.0.0 or 2.0.0-beta.N).")
    major, minor, patch, beta = m.groups()
    if beta is None:
        return f"{major}.{minor}.{patch}.0"
    if (major, minor, patch) != ("2", "0", "0"):
        raise SystemExit(f"Beta package versions are only worked out for 2.0.0 betas, not {ver}.")
    return f"1.99.{int(beta)}.0"


def sdk_tool(name: str) -> str:
    found = sorted(glob.glob(os.path.join(SDK_BIN, "10.*", "x64", name)),
                   key=lambda p: [int(x) for x in os.path.basename(os.path.dirname(os.path.dirname(p))).split(".")])
    if not found:
        raise SystemExit(f"{name} not found under {SDK_BIN}. Install the Windows 10/11 SDK.")
    return found[-1]


def run(cmd: list[str], quiet: bool = False) -> str:
    print(">", " ".join(f'"{c}"' if " " in c else c for c in cmd))
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode:
        print(r.stdout, r.stderr)
        raise SystemExit(f"failed (exit {r.returncode}): {os.path.basename(cmd[0])}")
    if not quiet:
        tail = [ln for ln in r.stdout.splitlines() if ln.strip()][-2:]
        for ln in tail:
            print("  " + ln)
    return r.stdout


def tile(size: tuple[int, int], icon: int) -> Image.Image:
    img = Image.new("RGBA", size, (0, 0, 0, 0))
    i = make_icon.draw(icon)
    img.paste(i, ((size[0] - icon) // 2, (size[1] - icon) // 2), i)
    return img


def write_assets(folder: str) -> None:
    """Tile images in the qualified names makepri understands (scale-100/200, targetsize for the taskbar)."""
    os.makedirs(folder)
    for scale in (100, 200):
        k = scale / 100
        tile((int(44 * k),) * 2, int(44 * k)).save(os.path.join(folder, f"Square44x44Logo.scale-{scale}.png"))
        tile((int(150 * k),) * 2, int(100 * k)).save(os.path.join(folder, f"Square150x150Logo.scale-{scale}.png"))
        tile((int(310 * k), int(150 * k)), int(100 * k)).save(os.path.join(folder, f"Wide310x150Logo.scale-{scale}.png"))
        tile((int(50 * k),) * 2, int(50 * k)).save(os.path.join(folder, f"StoreLogo.scale-{scale}.png"))
    for t in (16, 24, 32, 48, 256):
        icon = make_icon.draw(t)
        icon.save(os.path.join(folder, f"Square44x44Logo.targetsize-{t}.png"))
        icon.save(os.path.join(folder, f"Square44x44Logo.targetsize-{t}_altform-unplated.png"))


def manifest(identity: dict, pkg_ver: str) -> str:
    with open(os.path.join(ROOT, "packaging", "msix", "AppxManifest.xml"), encoding="utf-8") as f:
        text = f.read()
    for key in ("IdentityName", "Publisher", "PublisherDisplayName"):
        text = text.replace("{{" + key + "}}", identity[key])
    text = text.replace("{{Version}}", pkg_ver)
    if m := re.search(r"\{\{\w+\}\}", text):
        raise SystemExit(f"AppxManifest.xml still has an unfilled value: {m.group(0)}.")
    return text


def stage(identity: dict, ver: str, pkg_ver: str) -> str:
    folder = os.path.join(OUT, "stage")
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    for src, name in ((os.path.join(APP_OUT, "BejView.exe"), "BejView.exe"),
                      (os.path.join(LAUNCHER_OUT, "BejView.Launcher.exe"), "BejView.Launcher.exe"),
                      (os.path.join(ROOT, "LICENSE"), "LICENSE.txt")):
        if not os.path.isfile(src):
            raise SystemExit(f"missing: {src}. Run python -I tools\\make_beta_zip.py first.")
        shutil.copy2(src, os.path.join(folder, name))
    # The same pair check as the zip: the helper finds BejView.exe next to it and reports this version.
    r = subprocess.run([os.path.join(folder, "BejView.Launcher.exe"), "--version"], capture_output=True, text=True)
    if r.returncode != 0 or r.stdout.strip() != f"BejView {ver}":
        raise SystemExit(f"The published build is {r.stdout.strip()!r}, not BejView {ver}. Run make_beta_zip.py again.")
    write_assets(os.path.join(folder, "Assets"))
    with open(os.path.join(folder, "AppxManifest.xml"), "w", encoding="utf-8", newline="\r\n") as f:
        f.write(manifest(identity, pkg_ver))
    # resources.pri lets Windows pick the scale and taskbar (targetsize, unplated) images.
    cfg = os.path.join(OUT, "priconfig.xml")
    run([sdk_tool("makepri.exe"), "createconfig", "/cf", cfg, "/dq", "en-US", "/pv", "10.0.0", "/o"], quiet=True)
    run([sdk_tool("makepri.exe"), "new", "/pr", folder, "/cf", cfg, "/of", os.path.join(folder, "resources.pri"), "/o"])
    os.remove(cfg)
    return folder


def test_cert(cert_dir: str, publisher: str) -> tuple[str, str]:
    pfx, cer, pw_file = (os.path.join(cert_dir, n) for n in ("BejView-test.pfx", "BejView-test.cer", "password.txt"))
    if not os.path.isfile(pfx):
        os.makedirs(cert_dir, exist_ok=True)
        password = secrets.token_urlsafe(24)
        with open(pw_file, "w", encoding="ascii") as f:
            f.write(password)
        # Code signing (EKU 1.3.6.1.5.5.7.3.3), not a CA; the subject must equal the manifest's Publisher.
        ps = (f"$c = New-SelfSignedCertificate -Type Custom -Subject '{publisher}' -KeyUsage DigitalSignature "
              f"-FriendlyName 'BejView MSIX test (not for release)' -CertStoreLocation Cert:\\CurrentUser\\My "
              f"-NotAfter (Get-Date).AddYears(2) "
              f"-TextExtension @('2.5.29.37={{text}}1.3.6.1.5.5.7.3.3', '2.5.29.19={{text}}'); "
              f"$p = ConvertTo-SecureString -String '{password}' -Force -AsPlainText; "
              f"Export-PfxCertificate -Cert $c -FilePath '{pfx}' -Password $p | Out-Null; "
              f"Export-Certificate -Cert $c -FilePath '{cer}' | Out-Null; "
              f"Remove-Item \"Cert:\\CurrentUser\\My\\$($c.Thumbprint)\"; $c.Thumbprint")
        print("> making the test certificate")
        out = run(["pwsh", "-NoProfile", "-NonInteractive", "-Command", ps], quiet=True)
        print(f"  thumbprint {out.strip()}; written to {cert_dir}")
    with open(pw_file, encoding="ascii") as f:
        return pfx, f.read().strip()


def main() -> int:
    args = sys.argv[1:]
    sign, cert_dir = False, DEFAULT_CERT_DIR
    while args:
        a = args.pop(0)
        if a == "--test-sign":
            sign = True
        elif a == "--cert-dir" and args:
            cert_dir = args.pop(0)
        else:
            print(__doc__)
            return 2
    with open(os.path.join(ROOT, "packaging", "store-identity.json"), encoding="utf-8") as f:
        identity = json.load(f)
    ver = version()
    pkg_ver = package_version(ver)
    print(f"BejView {ver} -> package version {pkg_ver}, identity {identity['IdentityName']}")
    os.makedirs(OUT, exist_ok=True)
    folder = stage(identity, ver, pkg_ver)
    msix = os.path.join(OUT, f"BejView-{ver}-x64.msix")
    run([sdk_tool("makeappx.exe"), "pack", "/d", folder, "/p", msix, "/o"])
    print(f"wrote {msix} ({os.path.getsize(msix):,} bytes, unsigned: the Store signs it)")
    if sign:
        pfx, password = test_cert(cert_dir, identity["Publisher"])
        signed = msix[:-5] + "-test.msix"
        shutil.copy2(msix, signed)
        run([sdk_tool("signtool.exe"), "sign", "/fd", "SHA256", "/f", pfx, "/p", password, signed])
        print(f"wrote {signed} (signed with the test certificate)")
    shutil.rmtree(folder)
    return 0


if __name__ == "__main__":
    sys.exit(main())
