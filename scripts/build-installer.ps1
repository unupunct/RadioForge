# Publishes RadioForge (self-contained win-x64) and builds Installer\RadioForge-Setup.exe with Inno Setup 6.
# Optional code signing: set RF_SIGN_PFX (path to a .pfx) and RF_SIGN_PFX_PASSWORD; optionally RF_SIGN_TIMESTAMP (default http://timestamp.digicert.com).
# Without a certificate the output is unsigned (Windows SmartScreen will warn).
$root = Split-Path $PSScriptRoot -Parent
dotnet publish "$root\Source\RadioForge.App" -c Release -r win-x64 --self-contained true -o "$root\Release\RadioForge" -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

function Sign($files) {
    if (-not $env:RF_SIGN_PFX) { return }
    $st = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $st) { throw "RF_SIGN_PFX is set but signtool.exe (Windows SDK) was not found" }
    $ts = if ($env:RF_SIGN_TIMESTAMP) { $env:RF_SIGN_TIMESTAMP } else { "http://timestamp.digicert.com" }
    & $st.FullName sign /fd SHA256 /f $env:RF_SIGN_PFX /p $env:RF_SIGN_PFX_PASSWORD $(if ($env:RF_SIGN_NO_TIMESTAMP) { @() } else { '/tr',$ts,'/td','SHA256' }) /d "RadioForge" @files
    if ($LASTEXITCODE -ne 0) { throw "signing failed" }
}
Sign @("$root\Release\RadioForge\RadioForge.exe", "$root\Release\RadioForge\RadioForge.dll", "$root\Release\RadioForge\RadioForge.Core.dll")

$iscc = @("$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc "$root\Installer\RadioForge.iss"
Sign @("$root\Installer\RadioForge-Setup.exe")
Get-Item "$root\Installer\RadioForge-Setup.exe" | Select-Object Name, Length
if ($env:RF_SIGN_PFX) { Get-AuthenticodeSignature "$root\Installer\RadioForge-Setup.exe" | Select-Object Status, @{n='Signer';e={$_.SignerCertificate.Subject}} }
