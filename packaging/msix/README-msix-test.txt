BejView: Microsoft Store package (MSIX) test
============================================

This is a TEST package, signed with a self-signed test certificate so Windows will install it. The real Store
package is signed by the Microsoft Store. Use it to check a Store build on your own PC before uploading it.

Like every Store app, BejView writes NEW settings and log files into its own private folder
(...\AppData\Local\Packages\RichardBejtlich.BejView_kz4qzpv342p6y\LocalCache\Roaming\BejView), which uninstalling
removes. Files that already exist in %APPDATA%\BejView (from a zip build) are used in place. Either way, BejView shows
and opens the right place (bejview paths, Help > Open the log file).

Files
  BejView-<version>-x64-test.msix  the test package
  BejView-test.cer                 the test certificate (public part only)
  msix-test.ps1                    the steps below; it writes msix-test-results.txt

In short (PowerShell, in this folder; first run: Set-ExecutionPolicy -Scope Process Bypass)
  1. admin window:   .\msix-test.ps1 trust
  2. normal window:  .\msix-test.ps1 install
  3. NEW window:     .\msix-test.ps1 check        then try the window: Start menu > BejView, open and drag files,
                                                  follow a log, Help > Open the log file, Help > About
  4. normal window:  .\msix-test.ps1 uninstall
  5. admin window:   .\msix-test.ps1 wack         (optional; needs the Windows SDK. "Blocked executables" is
                                                  optional and expected to fail for a desktop app)
  6. admin window:   .\msix-test.ps1 untrust
msix-test-results.txt (and wack.xml from step 5) record what happened.
