$cert = Get-ChildItem -Path Cert:\CurrentUser\My -CodeSigningCert | Select-Object -First 1
if (-not $cert) {
    throw "No code signing certificate found"
}

$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe"
$exe = Join-Path $PSScriptRoot "publish\fleetmate.exe"

Write-Host "Signing $exe with certificate: $($cert.Subject)"
& $signtool sign /fd SHA256 /t http://timestamp.digicert.com /sha1 $cert.Thumbprint $exe

if ($LASTEXITCODE -eq 0) {
    Write-Host "Signing successful!"
} else {
    throw "Signing failed with exit code $LASTEXITCODE"
}
