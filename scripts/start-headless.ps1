$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

& (Join-Path $PSScriptRoot 'prepare-headless.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Output 'Neko adapter installed. Starting headless API...'
dotnet run --no-build -c Release --project src/Neko.Headless -- serve --config secrets.json
exit $LASTEXITCODE
