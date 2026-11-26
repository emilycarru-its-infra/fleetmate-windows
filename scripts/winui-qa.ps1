#Requires -Version 7.0
<#
.SYNOPSIS
    Per-panel UI QA for the WinUI app: screenshots of every tab and Settings,
    with clipped-text and accessible-name findings, for both editions.

.DESCRIPTION
    Publishes FleetMate.WinUI for FleetMate and TicketsMate, signs it when a
    certificate is given, and runs each edition with --theme light and
    --theme dark plus --capture. The app renders its own XAML root to PNG at a
    narrow and a wide window, at 100% and 150%, so this works on a locked or
    headless desktop where a screen capture comes back blank.

    The output folder gets one subfolder per edition, each holding the PNGs,
    qa-light.md and qa-dark.md, plus an index.md that totals the findings.
    Attach index.md and the PNGs to the pull request.

    What it cannot show: the system caption buttons and Mica (drawn as Mica's
    base colour) and the Windows high contrast themes, which only Windows can
    turn on. Run it again with a high contrast theme set to cover those.

.PARAMETER OutDir
    Where to write the captures. Defaults to artifacts\winui-qa.

.PARAMETER Thumbprint
    A code-signing certificate to sign the app with before it runs, for
    machines where unsigned executables are blocked (an attack surface
    reduction rule can refuse to start, or even read, a freshly built exe).
    Falls back to CERT_THUMBPRINT; with neither, the app runs unsigned.

.PARAMETER Edition
    FleetMate, TicketsMate, or both (the default).

.PARAMETER Runtime
    win-x64 or win-arm64. Defaults to this machine's architecture.

.PARAMETER TimeoutMinutes
    How long one capture (an edition in a theme) may take before it is
    stopped and reported as timed out. The run grows with every panel a lane
    adds, and software rendering on a CI runner is slow; defaults to 30.

.EXAMPLE
    .\scripts\winui-qa.ps1

.EXAMPLE
    .\scripts\winui-qa.ps1 -Edition TicketsMate -OutDir $env:TEMP\qa
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\artifacts\winui-qa'),
    [string]$Thumbprint = $env:CERT_THUMBPRINT,
    [ValidateSet('FleetMate', 'TicketsMate')]
    [string[]]$Edition = @('FleetMate', 'TicketsMate'),
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime,
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not $Runtime) {
    $Runtime = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
}
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }

function Find-SignTool {
    $onPath = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if ($onPath) { return $onPath }
    Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add('# WinUI UI QA')
$summary.Add('')
$summary.Add("Runtime $Runtime, captured $(Get-Date -Format 'yyyy-MM-dd HH:mm').")
$summary.Add('')
$summary.Add('| Edition | Theme | Findings | Report |')
$summary.Add('| --- | --- | --- | --- |')
$failed = $false
$runId = "{0}-{1}" -f (Get-Date -Format 'yyyyMMddHHmmss'), $PID

foreach ($ed in $Edition) {
    # A folder per run, so two lanes running this at once never publish over
    # each other's build, or over an app still running from it.
    $build = Join-Path ([IO.Path]::GetTempPath()) "fleetmate-winui-qa\$runId\$ed"
    Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Publishing $ed ($Runtime)..." -ForegroundColor Cyan
    # A unique file version gives the executable a hash of its own, so a
    # reputation block on an earlier unsigned build does not follow it.
    $fileVersion = Get-Date -Format 'yy.MM.dd.HHmm'
    & dotnet publish "$root\FleetMate.WinUI\FleetMate.WinUI.csproj" -c Release -r $Runtime --self-contained true `
        -p:Platform=$platform -p:Edition=$ed -p:SignDevBuild=false -p:FileVersion=$fileVersion -nodeReuse:false `
        -o $build | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$ed publish failed" }

    $exeName = if ($ed -eq 'TicketsMate') { 'TicketsMate' } else { 'FleetMate.WinUI' }
    if ($Thumbprint) {
        $signtool = Find-SignTool
        if (-not $signtool) { throw 'signtool.exe not found; install the Windows SDK signing tools.' }
        $store = if (Test-Path "Cert:\CurrentUser\My\$Thumbprint") { @('/s', 'My') } else { @('/s', 'My', '/sm') }
        & $signtool sign /q @store /sha1 $Thumbprint /fd SHA256 `
            (Join-Path $build "$exeName.exe") (Join-Path $build "$exeName.dll") (Join-Path $build 'FleetMate.Core.dll')
        if ($LASTEXITCODE -ne 0) { throw "Signing $ed failed" }
    }

    $target = Join-Path $OutDir $ed
    New-Item -ItemType Directory -Force $target | Out-Null
    foreach ($theme in 'light', 'dark') {
        Write-Host "  Capturing $ed in $theme..." -ForegroundColor Cyan
        $process = Start-Process (Join-Path $build "$exeName.exe") -ArgumentList '--theme', $theme, '--capture', $target -PassThru
        if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
            $process.Kill()
            $failed = $true
            $summary.Add("| $ed | $theme | timed out | |")
            continue
        }
        $report = Join-Path $target "qa-$theme.md"
        if ($process.ExitCode -ne 0 -or -not (Test-Path $report)) {
            $failed = $true
            $summary.Add("| $ed | $theme | capture failed (exit $($process.ExitCode)) | |")
            continue
        }
        $findings = if ((Get-Content $report -TotalCount 1) -match 'findings: (\d+)') { $Matches[1] } else { '?' }
        $summary.Add("| $ed | $theme | $findings | [$ed/qa-$theme.md]($ed/qa-$theme.md) |")
    }
}

$summary | Set-Content (Join-Path $OutDir 'index.md') -Encoding utf8
Remove-Item (Join-Path ([IO.Path]::GetTempPath()) "fleetmate-winui-qa\$runId") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Wrote $(Join-Path $OutDir 'index.md')" -ForegroundColor Green
if ($failed) { exit 1 }
