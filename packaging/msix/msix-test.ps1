<#
  BejView MSIX test (step 5, 2026-11-13). Run in PowerShell from the folder that holds this script, the
  *-test.msix package and BejView-test.cer. Steps, in order:

    1. trust      (admin window, once per machine)  trusts the test certificate, so Windows installs the package
    2. install    (your normal window)              installs the test package
    3. check      (your normal window)              runs the terminal checks and says where the files went
    4. uninstall  (your normal window)              removes BejView's package
    5. wack       (admin window, optional)          runs the Windows App Certification Kit on the package
    6. untrust    (admin window, at the end)        removes the test certificate again

  Example:  .\msix-test.ps1 install
  Each step appends what it saw to msix-test-results.txt next to this script; hand that file back.
  The package keeps Windows' default file-system virtualization: BejView's NEW settings and log files go to the
  package's private copy of %APPDATA%\BejView, which uninstall removes; files already in %APPDATA%\BejView are used in place.
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('trust', 'install', 'check', 'uninstall', 'wack', 'untrust')]
    [string]$Step
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$results = Join-Path $here 'msix-test-results.txt'
$family = 'RichardBejtlich.BejView_kz4qzpv342p6y'
$name = 'RichardBejtlich.BejView'

function Say([string]$text) {
    Write-Host $text
    Add-Content -Path $results -Value $text
}

function Admin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }

function Package {
    $p = Get-ChildItem -Path $here -Filter 'BejView-*-x64-test.msix' | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $p) { throw "No BejView-*-x64-test.msix in $here." }
    $p.FullName
}

Say ''
Say "=== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  step $Step  user $env:USERNAME  $([Environment]::OSVersion.VersionString)"

switch ($Step) {
    'trust' {
        if (-not (Admin)) { throw 'Run "trust" in an admin PowerShell window (right-click > Run as administrator).' }
        $cer = Join-Path $here 'BejView-test.cer'
        $c = Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
        Say "Trusted the test certificate $($c.Thumbprint) ($($c.Subject)) in Local Machine > Trusted People."
    }
    'install' {
        if (Admin) { Say 'Note: this window is an admin window. Use your normal window, so BejView installs for your own account.' }
        if (Get-AppxPackage -Name $name) { throw "BejView's package is already installed. Run: .\msix-test.ps1 uninstall" }
        $msix = Package
        try { Add-AppxPackage -Path $msix }
        catch {
            if ("$_" -match '0x800B0109') { throw 'Windows does not trust the test certificate yet. Run ".\msix-test.ps1 trust" in an admin window first.' }
            throw
        }
        $p = Get-AppxPackage -Name $name
        Say "Installed $([IO.Path]::GetFileName($msix)): $($p.PackageFullName)"
        Say "Install folder: $($p.InstallLocation)"
        Say 'Next: open a NEW terminal window (so it sees the "bejview" command), then run: .\msix-test.ps1 check'
    }
    'check' {
        $p = Get-AppxPackage -Name $name
        if (-not $p) { throw 'BejView is not installed. Run: .\msix-test.ps1 install' }
        Say "Package: $($p.PackageFullName)"
        $cmd = Get-Command bejview -ErrorAction SilentlyContinue
        if (-not $cmd) { Say 'FAIL  the "bejview" command is not found. Open a new terminal window and run check again.'; break }
        Say ($(if ($cmd.Source -like '*\Microsoft\WindowsApps\*') { 'OK  ' } else { 'FAIL' }) + "  bejview is $($cmd.Source) (expected the Store alias in ...\Microsoft\WindowsApps)")

        $v = & bejview --version
        Say ($(if ($LASTEXITCODE -eq 0 -and "$v" -like 'BejView 2.0.1*') { 'OK  ' } else { 'FAIL' }) + "  bejview --version -> '$v' (exit $LASTEXITCODE)")

        $sample = Join-Path $here 'msix-test-sample.log'
        Set-Content -Path $sample -Value @('one', 'two ERROR here', 'three', 'ERROR again') -Encoding utf8
        # Waits like .\BejView: the output arrives before the next line runs, and the exit code comes back.
        $info = & bejview info $sample
        Say ($(if ($LASTEXITCODE -eq 0 -and ($info -join "`n") -match 'Lines:\s+4') { 'OK  ' } else { 'FAIL' }) + "  bejview info (exit $LASTEXITCODE): $(($info | Select-String 'Lines:') -replace '\s+', ' ')")
        $find = & bejview find $sample ERROR
        Say ($(if ($LASTEXITCODE -eq 0 -and @($find).Count -eq 2) { 'OK  ' } else { 'FAIL' }) + "  bejview find ERROR (exit $LASTEXITCODE, $(@($find).Count) lines; expected 0 and 2)")
        $null = & bejview find $sample NOSUCHWORD
        Say ($(if ($LASTEXITCODE -eq 1) { 'OK  ' } else { 'FAIL' }) + "  bejview find with no match returns 1 (got $LASTEXITCODE)")
        $null = & bejview --no-such-option 2>&1
        Say ($(if ($LASTEXITCODE -eq 2) { 'OK  ' } else { 'FAIL' }) + "  a bad option returns 2 (got $LASTEXITCODE)")

        # Where the log really is, as BejView reports it and as Explorer sees it.
        $null = & bejview info $sample --verbose 2>&1   # writes the log
        $paths = & bejview paths
        $paths | ForEach-Object { Say "      paths: $_" }
        # Windows puts NEW files in the package's private copy but changes files that already exist in %APPDATA% in place
        # (1903+), so either place can be right: on a PC that ran a zip build of BejView before, the log stays in
        # %APPDATA%\BejView. What matters is that "bejview paths" names the place where the log really is.
        $copy = Join-Path $env:LOCALAPPDATA "Packages\$family\LocalCache\Roaming\BejView\bejview.log"
        $real = Join-Path $env:APPDATA 'BejView\bejview.log'
        $shown = (($paths | Select-String 'Log file:').ToString() -replace '^\s*Log file:\s*', '').Trim()
        $where = if (Test-Path $copy) { "the package's private copy ($copy)" } elseif (Test-Path $real) { "the real %APPDATA%\BejView, because the file was already there" } else { 'nowhere' }
        Say "      the log is in $where"
        Say ($(if ((Test-Path $shown) -and (($shown -eq $copy) -or (-not (Test-Path $copy)))) { 'OK  ' } else { 'FAIL' }) + "  ""bejview paths"" names the log where it really is: $shown")
        Remove-Item $sample -ErrorAction SilentlyContinue
        Say 'Now the window checks by hand (the test plan''s Store package group): Start menu > BejView; File > Open; drag a file in;'
        Say 'follow mode on a log outside the package; Help > Open the log file, Help > Open the settings folder, Help > About.'
    }
    'uninstall' {
        $p = Get-AppxPackage -Name $name
        if (-not $p) { Say 'BejView''s package is not installed.'; break }
        Remove-AppxPackage -Package $p.PackageFullName
        $gone = -not (Test-Path (Join-Path $env:LOCALAPPDATA "Packages\$family"))
        Say "Removed $($p.PackageFullName). Its private copy is gone: $gone. %APPDATA%\BejView (the zip's) is untouched."
    }
    'wack' {
        if (-not (Admin)) { throw 'Run "wack" in an admin PowerShell window.' }
        $kit = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'
        if (-not (Test-Path $kit)) { throw "The Windows App Certification Kit isn't installed ($kit). It comes with the Windows SDK." }
        if (Get-AppxPackage -Name $name -AllUsers) { throw 'Uninstall BejView first (.\msix-test.ps1 uninstall in your normal window): the kit installs the package itself.' }
        $msix = Package
        $report = Join-Path $here 'wack.xml'
        & $kit reset | Out-Null
        Say "Running the certification kit on $([IO.Path]::GetFileName($msix)) (several minutes)..."
        & $kit test -appxpackagepath $msix -reportoutputpath $report | Out-Null
        if (Test-Path $report) {
            [xml]$x = Get-Content -Raw $report
            Say "      overall: $($x.DocumentElement.GetAttribute('OVERALL_RESULT'))  (report: $report)"
            foreach ($t in $x.SelectNodes('//TEST')) {
                $r = $t.SelectSingleNode('RESULT').InnerText.Trim()
                # Optional tests don't count towards the overall result (the kit's OPTIONAL="TRUE").
                $opt = if ($t.GetAttribute('OPTIONAL') -eq 'TRUE') { ' (optional: does not count)' } else { '' }
                if ($r -ne 'PASS') { Say "      not passed: $($t.GetAttribute('NAME')) -> $r$opt" }
            }
        } else {
            Say 'FAIL  no report'
        }
    }
    'untrust' {
        if (-not (Admin)) { throw 'Run "untrust" in an admin PowerShell window.' }
        $cer = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (Join-Path $here 'BejView-test.cer')
        $item = Join-Path 'Cert:\LocalMachine\TrustedPeople' $cer.Thumbprint
        if (Test-Path $item) { Remove-Item $item; Say "Removed the test certificate $($cer.Thumbprint)." }
        else { Say "The test certificate $($cer.Thumbprint) wasn't trusted on this machine." }
    }
}
