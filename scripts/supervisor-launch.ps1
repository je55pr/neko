# Per-user logon entry. The registration manifest is ignored by Git.
# It contains only executable/config paths and a pinned digest, not tokens.
param([Parameter(Mandatory = $true)][string]$Key)
$ErrorActionPreference = 'Stop'
if ($Key -cnotmatch '^[A-F0-9]{16}$') { throw 'Invalid supervisor registration key.' }
$manifestPath = Join-Path $PSScriptRoot "supervisor-$Key.local.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Supervisor registration manifest is missing.'
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
