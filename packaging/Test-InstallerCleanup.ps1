[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('rvc-cleanup-cli-test-' + [Guid]::NewGuid().ToString('N'))
$installation = Join-Path $testRoot 'installation'
$incoming = Join-Path $testRoot 'new-manifest.json'
$plan = Join-Path $testRoot 'plan.json'
function Put([string]$Relative, [string]$Value) {
    $path = Join-Path $installation $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $Value, [Text.UTF8Encoding]::new($false))
    Get-Record $path $Relative
}
function Get-Record([string]$Path, [string]$Relative) {
    @{ path = $Relative; size = (Get-Item -LiteralPath $Path).Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Run-Helper([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Executable).Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Installer helper timed out.' }
        if ($process.ExitCode -ne 0) { throw "Installer helper failed: $($process.ExitCode)" }
    } finally { $process.Dispose() }
}
try {
    $old = @(
        (Put 'RVC Studio.exe' 'old-app'),
        (Put 'runtime/obsolete.dll' 'old-runtime'),
        (Put 'engine/removed.py' 'old-code'),
        (Put 'assets/weights/custom.pth' 'user-model'),
        (Put 'configs/config.json' 'user-config')
    )
    Put 'runtime/untracked.txt' 'user-added' | Out-Null
    $oldInventory = @{ file_count = $old.Count; files = $old } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $installation 'release-manifest.json'), $oldInventory)
    $next = @{ file_count = 1; files = @((Get-Record (Resolve-Path -LiteralPath $Executable).Path 'RVC Studio.exe')) } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($incoming, $next)
    Run-Helper @('--installer-cleanup', 'prepare', $installation, $incoming, $plan)
    if (-not (Test-Path -LiteralPath (Join-Path $installation 'runtime/obsolete.dll'))) { throw 'Preparation deleted an installed file.' }
    Copy-Item -LiteralPath $Executable -Destination (Join-Path $installation 'RVC Studio.exe') -Force
    Run-Helper @('--installer-cleanup', 'apply', $plan)
    Run-Helper @('--installer-cleanup', 'apply', $plan)
    foreach ($retired in @('runtime/obsolete.dll','engine/removed.py')) {
        if (Test-Path -LiteralPath (Join-Path $installation $retired)) { throw "Retired file survived: $retired" }
    }
    foreach ($protected in @('assets/weights/custom.pth','configs/config.json','runtime/untracked.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $installation $protected))) { throw "Protected file was removed: $protected" }
    }
    if ([IO.File]::ReadAllText((Join-Path $installation 'release-manifest.json')) -ne $next) { throw 'New inventory was not committed.' }
    Write-Host 'PASS: published single-file installer cleanup helper, idempotent retry and user-data protection.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\rvc-cleanup-cli-test-'
    if ($resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
