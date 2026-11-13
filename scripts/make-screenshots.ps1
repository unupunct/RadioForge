# Renders every RadioForge page to Documentation\screenshots using generated demo audio and a local test Icecast server.
param([string]$Config = "Debug")
$root = Split-Path $PSScriptRoot -Parent
$tmp = Join-Path $env:TEMP "rf-shots"; Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $tmp | Out-Null
dotnet build "$root\Tools\RadioForge.DevServer" -c $Config | Out-Null
$dev = Start-Process dotnet -ArgumentList "run --no-build -c $Config --project `"$root\Tools\RadioForge.DevServer`" -- `"$tmp\port.txt`"" -PassThru -WindowStyle Hidden
for ($i = 0; $i -lt 40 -and -not (Test-Path "$tmp\port.txt"); $i++) { Start-Sleep -Milliseconds 500 }
$port = Get-Content "$tmp\port.txt"
$exe = Get-ChildItem "$root\Source\RadioForge.App\bin\$Config" -Recurse -Filter RadioForge.exe | Select-Object -First 1
$app = Start-Process $exe.FullName -ArgumentList "--screenshots","--out","$root\Documentation\screenshots","--data-dir","$tmp\data","--demo-port",$port -PassThru
if (-not $app.WaitForExit(150000)) { $app.Kill() }
Get-Process -Name RadioForge.DevServer -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Process -Id $dev.Id -Force -ErrorAction SilentlyContinue
Get-ChildItem "$root\Documentation\screenshots" | Select-Object Name, Length
