param(
    [switch]$SkipBuild,
    [switch]$SkipUnitTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    if (-not $SkipBuild) {
        & dotnet build FleetMate.WinUI\FleetMate.WinUI.csproj -c Debug -p:Platform=x64 --no-restore
        if ($LASTEXITCODE) { throw "WinUI build failed ($LASTEXITCODE)" }
        & dotnet build FleetMate.GUI\FleetMate.GUI.csproj -c Debug --no-restore
        if ($LASTEXITCODE) { throw "Desktop compatibility build failed ($LASTEXITCODE)" }
    }

    if (-not $SkipUnitTests) {
        & dotnet test FleetMate.Tests\FleetMate.Tests.csproj -c Debug --no-restore --verbosity minimal
        if ($LASTEXITCODE) { throw "Unit tests failed ($LASTEXITCODE)" }
    }

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $guiDir = Resolve-Path 'FleetMate.GUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64'
    $gui = Start-Process dotnet -ArgumentList 'fleetmate-gui.dll' -WorkingDirectory $guiDir -WindowStyle Hidden -PassThru
    try {
        Start-Sleep -Seconds 5
        $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $gui.Id))
        if ($null -eq $root) { throw 'FleetMate window was not exposed to UI Automation' }
        $settings = $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'TabSettings'))
        if ($null -eq $settings) { throw 'Settings navigation control was not found' }
        $selection = [System.Windows.Automation.SelectionItemPattern]$settings.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()
        Start-Sleep -Milliseconds 750
        $content = $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Configure FleetMate'))
        if ($null -eq $content -or -not $selection.Current.IsSelected) { throw 'Settings navigation did not open Settings content' }
        Write-Host '{"guiNavigation":"ok","settings":"open"}'
    }
    finally {
        if ($gui -and -not $gui.HasExited) { Stop-Process -Id $gui.Id }
    }

    & dotnet FleetMate.CLI\bin\Debug\net10.0-windows\win-x64\fleetmate.dll login --check --desktop --strict --defer-browser --json
    $serviceExit = $LASTEXITCODE
    & dotnet FleetMate.GUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\fleetmate-gui.dll --headless-tdx-sso
    $tdxExit = $LASTEXITCODE
    if ($serviceExit -or $tdxExit) {
        throw "Desktop smoke failed (services=$serviceExit, TeamDynamix=$tdxExit)"
    }
}
finally {
    Pop-Location
}
