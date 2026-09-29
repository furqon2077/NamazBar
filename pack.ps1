# Packs pkg\ into a signed dist\NamazBar.msix (+ dist\NamazBar.cer). build.bat /pack then embeds both
# into release\NamazBar.exe - the single file that installs/updates and starts NamazBar.
# The signing certificate "CN=NamazBar" is created once in CurrentUser\My and reused:
# updates must be signed with the same certificate, so do not delete it.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pkg  = Join-Path $root 'pkg'
$dist = Join-Path $root 'dist'
$publisher = 'CN=NamazBar'

$bin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" | Sort-Object FullName | Select-Object -Last 1
if (-not $bin) { throw 'makeappx.exe not found - install the Windows SDK' }
$makeappx = $bin.FullName
$signtool = Join-Path $bin.DirectoryName 'signtool.exe'

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter | Select-Object -Last 1
if (-not $cert) {
    Write-Host 'Creating signing certificate CN=NamazBar (CurrentUser\My)'
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'NamazBar package signing' `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null
$msix = Join-Path $dist 'NamazBar.msix'

& $makeappx pack /o /d $pkg /p $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }
Export-Certificate -Cert $cert -FilePath (Join-Path $dist 'NamazBar.cer') | Out-Null
Write-Host "Package OK: $msix"
