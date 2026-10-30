$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

dotnet build 'src/Neko.Headless/Neko.Headless.csproj' -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build 'src/Neko.Transport/Neko.Transport.csproj' -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()) {
    'X64' {'win-x64'}
    'X86' {'win-x86'}
    'Arm64' {'win-arm64'}
    default { throw 'Unsupported runtime architecture.' }
}
$native = Join-Path $root "src/Neko.Transport/bin/Release/net10.0-windows/runtimes/$architecture/native/WebView2Loader.dll"
$destination = Join-Path $root 'src/Neko.Headless/bin/Release/net10.0-windows/WebView2Loader.dll'
if (-not (Test-Path $native)) { throw 'Transport runtime dependency unavailable.' }
Copy-Item $native $destination -Force

Write-Output 'Neko adapter installed. Starting headless API...'
dotnet run --no-build -c Release --project src/Neko.Headless -- serve --config secrets.json
exit $LASTEXITCODE
