#Requires -Version 7.0
<#
.SYNOPSIS
    Checks a built FleetMate or TicketsMate MSI against the architecture it was
    built for.

.DESCRIPTION
    Reads the MSI itself through the Windows Installer API, not the project
    file: the summary Template (the architecture Windows Installer enforces),
    the ProductName, the Start Menu shortcut and the packaged file names. It
    also reads the PE machine type of each payload executable that went into the
    package, so an ARM64 package carrying an x64 payload, or the reverse, fails.
    Read-only; nothing is installed.

.EXAMPLE
    ./scripts/Test-MsiPackage.ps1 -Msi TicketsMate.Installer/bin/arm64/Release/TicketsMate-arm64.msi `
        -Architecture arm64 -ProductName TicketsMate -Shortcut TicketsMate -ShortcutIcon TicketsMate.ico `
        -Payload publish/msi-staging-ticketsmate/arm64/TicketsMate.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Msi,
    [Parameter(Mandatory)] [ValidateSet('x64', 'arm64')] [string]$Architecture,
    [Parameter(Mandatory)] [string]$ProductName,
    [Parameter(Mandatory)] [string]$Shortcut,
    [string]$ShortcutIcon,
    [Parameter(Mandatory)] [string[]]$Payload
)

$ErrorActionPreference = 'Stop'
# pwsh -File passes a list as one comma-separated string.
$Payload = @($Payload | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$expectedTemplate = @{ x64 = 'x64;1033'; arm64 = 'Arm64;1033' }[$Architecture]
$expectedMachine = @{ x64 = 0x8664; arm64 = 0xAA64 }[$Architecture]
$failures = [System.Collections.Generic.List[string]]::new()

function Get-PeMachine([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "$Path is not a PE file" }
        $stream.Position = 0x3C
        $stream.Position = $reader.ReadInt32()
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "$Path has no PE header" }
        return [int]$reader.ReadUInt16()
    } finally {
        $stream.Dispose()
    }
}

function Invoke-Com($Object, [string]$Member, [System.Reflection.BindingFlags]$Kind, [object[]]$Arguments) {
    $Object.GetType().InvokeMember($Member, $Kind, $null, $Object, $Arguments)
}

function Get-MsiRows($Database, [string]$Query, [int]$Columns) {
    $view = Invoke-Com $Database 'OpenView' 'InvokeMethod' @($Query)
    try {
        Invoke-Com $view 'Execute' 'InvokeMethod' $null | Out-Null
        while ($record = Invoke-Com $view 'Fetch' 'InvokeMethod' $null) {
            $row = for ($i = 1; $i -le $Columns; $i++) { [string](Invoke-Com $record 'StringData' 'GetProperty' @([int]$i)) }
            , @($row)
        }
    } finally {
        Invoke-Com $view 'Close' 'InvokeMethod' $null | Out-Null
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    }
}

# MSI file and shortcut names are "short|long"; the long one is what users see.
function Get-LongName([string]$Name) { ($Name -split '\|')[-1] }

$msiPath = (Resolve-Path -LiteralPath $Msi).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
try {
    $summary = Invoke-Com $installer 'SummaryInformation' 'GetProperty' @($msiPath, 0)
    $template = Invoke-Com $summary 'Property' 'GetProperty' @(7)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($summary)
    Write-Host "Template: $template"
    if ($template -cne $expectedTemplate) { $failures.Add("summary Template is '$template', expected '$expectedTemplate'") }

    $database = Invoke-Com $installer 'OpenDatabase' 'InvokeMethod' @($msiPath, 0)

    $rows = @(Get-MsiRows $database "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ProductName'" 1)
    $name = if ($rows.Count -gt 0) { $rows[0][0] } else { '' }
    Write-Host "ProductName: $name"
    if ($name -cne $ProductName) { $failures.Add("ProductName is '$name', expected '$ProductName'") }

    $shortcuts = @(Get-MsiRows $database "SELECT ``Name``, ``Directory_``, ``Icon_`` FROM ``Shortcut``" 3)
    $match = $shortcuts | Where-Object { (Get-LongName $_[0]) -ceq $Shortcut -and $_[1] -eq 'ProgramMenuFolder' } | Select-Object -First 1
    if (-not $match) {
        $failures.Add("no Start Menu shortcut named '$Shortcut'")
    } else {
        Write-Host "Shortcut: $(Get-LongName $match[0]) in $($match[1]), icon $($match[2])"
        if ($ShortcutIcon -and $match[2] -ne $ShortcutIcon) { $failures.Add("shortcut icon is '$($match[2])', expected '$ShortcutIcon'") }
    }

    $files = @(Get-MsiRows $database "SELECT ``FileName`` FROM ``File``" 1 | ForEach-Object { Get-LongName $_[0] })
    foreach ($exe in $Payload) {
        $leaf = Split-Path $exe -Leaf
        if ($files -notcontains $leaf) { $failures.Add("the package has no file named '$leaf'") }
        $machine = Get-PeMachine (Resolve-Path -LiteralPath $exe).Path
        Write-Host ("Payload {0}: PE machine 0x{1:X4}" -f $leaf, $machine)
        if ($machine -ne $expectedMachine) {
            $failures.Add(("payload '{0}' is PE machine 0x{1:X4}, expected 0x{2:X4}" -f $leaf, $machine, $expectedMachine))
        }
    }
} finally {
    if ($database) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
}

if ($failures.Count -gt 0) {
    throw "$(Split-Path $msiPath -Leaf) does not match $Architecture`n  - $($failures -join "`n  - ")"
}
Write-Host "$(Split-Path $msiPath -Leaf): $Architecture package checks passed"
