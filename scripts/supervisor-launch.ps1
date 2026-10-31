# Per-user logon entry. The registration manifest is kept under private
# user app-data, with its content digest pinned in the user's registry.
param([Parameter(Mandatory = $true)][string]$Key)
$ErrorActionPreference = 'Stop'
# Hidden logon startup has no console. Record only fixed lifecycle markers,
# never an exception string, configuration path, token or provider output.
function Write-LauncherFailure([string]$Marker) {
    try {
        if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
            $logDir = Join-Path $env:LOCALAPPDATA 'Neko\Supervision'
            if (-not (Test-Path -LiteralPath $logDir -PathType Container)) {
                $null = New-Item -ItemType Directory -Path $logDir -Force -ErrorAction Stop
            }
            Add-Content -LiteralPath (Join-Path $logDir 'launcher-failures.log') -Value (
                [DateTimeOffset]::UtcNow.ToString('o') + ' ' + $Marker
            ) -ErrorAction SilentlyContinue
        }
    } catch { }
}
trap {
    Write-LauncherFailure 'startup_integrity_or_launch_failed'
    exit 1
}
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
# Read-only verification of the entire pinned execution image, including
# dependencies and configured provider bytes, BEFORE launching any binary.
# The manifest itself is authenticated by the HKCU digest above.
if ($null -eq $manifest.pinnedFiles) { throw 'Missing pinned execution manifest.' }
$entries = @($manifest.pinnedFiles.PSObject.Properties)
if ($entries.Count -eq 0) { throw 'Empty pinned execution manifest.' }
$pinned = @{}
foreach ($file in $entries) {
    $path = [string]$file.Name
    $digest = [string]$file.Value
    # Windows PowerShell 5.1 runs on .NET Framework, which does not
    # expose Path.IsPathFullyQualified. Reject drive-relative (C:foo) and
    # rooted-relative (\foo) forms using the fully qualified root length.
    $pathRoot = [IO.Path]::GetPathRoot($path)
    if (-not [IO.Path]::IsPathRooted($path) -or
        $pathRoot.Length -lt 3 -or
        $digest -cnotmatch '^[A-F0-9]{64}$') {
        throw 'Malformed pinned execution manifest.'
    }
    $pinned[$path] = $digest
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
    if ($actual -cne $digest) { throw 'Pinned executable dependency changed.' }
}
if (-not $pinned.ContainsKey($runtime) -or -not $pinned.ContainsKey($assembly)) {
    throw 'Pinned runtime or assembly entry missing.'
}
# The set of loadable dependencies must be closed, not only the hashes of
# files present at installation. Newly added DLLs/native modules fail closed.
$directories = @($manifest.pinnedDirectories)
if ($directories.Count -eq 0) { throw 'Pinned dependency directories missing.' }
# Keep this check in one function so Windows PowerShell tests can execute
# the real startup inventory logic without touching the user's registry.
function Assert-PinnedDirectoryInventory(
    [string[]]$Directories, [hashtable]$Pinned
) {
    foreach ($directory in $Directories) {
        $dir = [string]$directory
        $directoryRoot = [IO.Path]::GetPathRoot($dir)
        if (-not [IO.Path]::IsPathRooted($dir) -or
            $directoryRoot.Length -lt 3 -or
            -not (Test-Path -LiteralPath $dir -PathType Container)) {
            throw 'Pinned dependency directory unavailable.'
        }
        # Explicit traversal with -Force includes hidden/system files. Never
        # follow junctions or symlinks: reject any reparse point before execution.
        $pending = New-Object 'System.Collections.Generic.Stack[string]'
        $pending.Push($dir)
        while ($pending.Count -gt 0) {
            $current = $pending.Pop()
            $currentInfo = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($currentInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Reparse point in pinned execution image.'
            }
            foreach ($candidate in Get-ChildItem -LiteralPath $current -Force -ErrorAction Stop) {
                if (($candidate.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'Reparse point in pinned execution image.'
                }
                if ($candidate.PSIsContainer) {
                    $pending.Push([string]$candidate.FullName)
                    continue
                }
                $extension = [IO.Path]::GetExtension($candidate.FullName).ToLowerInvariant()
                if (@('.dll','.exe','.json','.so','.dylib') -contains $extension) {
                    if (-not $Pinned.ContainsKey([IO.Path]::GetFullPath($candidate.FullName))) {
                        throw 'Unregistered executable dependency detected.'
                    }
                }
            }
        }
    }
}
Assert-PinnedDirectoryInventory -Directories $directories -Pinned $pinned
if ([IO.Path]::GetFileNameWithoutExtension($runtime) -ieq 'dotnet') {
    & $runtime $assembly supervise --config $configPath --expected-instance $expected
} else {
    & $runtime supervise --config $configPath --expected-instance $expected
}
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-LauncherFailure ('supervisor_exited_code_' + [string]$code)
}
exit $code
