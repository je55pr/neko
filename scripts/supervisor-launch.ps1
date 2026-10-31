# Per-user logon entry. The registration manifest is kept under private
# user app-data, with its content digest pinned in the user's registry.
param([Parameter(Mandatory = $true)][string]$Key)
$ErrorActionPreference = 'Stop'
if ($Key -cnotmatch '^[A-F0-9]{16}$') { throw 'Invalid supervisor registration key.' }
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'Local application-data directory unavailable.'
}
$manifestPath = Join-Path (Join-Path $env:LOCALAPPDATA 'Neko\Supervision') "supervisor-$Key.local.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Supervisor registration manifest is missing.'
}
# HKCU is the installer's trusted per-user anchor; a replaced checkout
# manifest alone cannot redirect the runtime or the private configuration.
$registeredDigest = [string](Get-ItemPropertyValue -Path 'HKCU:\Software\Neko\SupervisorManifests' -Name $Key -ErrorAction Stop)
$actualDigest = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256 -ErrorAction Stop).Hash
if ($registeredDigest -cnotmatch '^[A-F0-9]{64}$' -or $actualDigest -cne $registeredDigest) {
    throw 'Supervisor startup manifest failed registry integrity verification.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($manifest.runtime) -or
    [string]::IsNullOrWhiteSpace($manifest.assembly) -or
    [string]::IsNullOrWhiteSpace($manifest.config) -or
    $manifest.expectedIdentity -cnotmatch '^[A-F0-9]{64}$') {
    throw 'Supervisor registration manifest is invalid.'
}
$runtime = [string]$manifest.runtime
$assembly = [string]$manifest.assembly
$configPath = [string]$manifest.config
$expected = [string]$manifest.expectedIdentity
if ([IO.Path]::GetFileNameWithoutExtension($runtime) -ieq 'dotnet') {
    & $runtime $assembly supervise --config $configPath --expected-instance $expected
} else {
    & $runtime supervise --config $configPath --expected-instance $expected
}
exit $LASTEXITCODE
