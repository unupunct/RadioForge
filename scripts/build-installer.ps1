# Publishes RadioForge (self-contained win-x64) and builds Installer\RadioForge-Setup.exe with Inno Setup 6.
$root = Split-Path $PSScriptRoot -Parent
dotnet publish "$root\Source\RadioForge.App" -c Release -r win-x64 --self-contained true -o "$root\Release\RadioForge" -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$iscc = @("$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc "$root\Installer\RadioForge.iss"
Get-Item "$root\Installer\RadioForge-Setup.exe" | Select-Object Name, Length
