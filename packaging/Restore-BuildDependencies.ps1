[CmdletBinding()]
param(
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$ReleaseTag = 'build-dependencies-v1',
    [string]$ArchivePrefix = 'rvc-studio-build-deps-v1'
)

$ErrorActionPreference = 'Stop'
$PackagingRoot = $PSScriptRoot
$RvcRoot = Split-Path -Parent $PackagingRoot
$DownloadRoot = Join-Path $PackagingRoot 'output\dependency-download'
$ChecksumPath = Join-Path $PackagingRoot 'dependencies\SHA256SUMS.txt'

if ([string]::IsNullOrWhiteSpace($Repository)) {
    throw 'Repository is required, for example owner/RvcStudio.'
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) is required to restore build dependencies.'
}
$sevenZip = @(
    (Get-Command 7z -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
    'C:\Program Files\7-Zip\7z.exe',
    'C:\Program Files (x86)\7-Zip\7z.exe'
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $sevenZip) {
    throw '7-Zip is required to restore build dependencies.'
}
if (-not (Test-Path -LiteralPath $ChecksumPath -PathType Leaf)) {
    throw "Dependency checksum file is missing: $ChecksumPath"
}

$allowed = [IO.Path]::GetFullPath((Join-Path $PackagingRoot 'output')).TrimEnd('\') + '\'
$target = [IO.Path]::GetFullPath($DownloadRoot)
if (-not $target.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to reset a directory outside packaging output: $target"
}
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
}
New-Item -ItemType Directory -Path $target -Force | Out-Null

& gh release download $ReleaseTag --repo $Repository --pattern "$ArchivePrefix.7z.*" --dir $target
if ($LASTEXITCODE -ne 0) {
    throw "Unable to download dependency release '$ReleaseTag' from $Repository."
}

$expected = @{}
foreach ($line in Get-Content -LiteralPath $ChecksumPath) {
    if ($line -match '^([A-Fa-f0-9]{64}) \*(.+)$') {
        $expected[$Matches[2]] = $Matches[1].ToUpperInvariant()
    }
}
if ($expected.Count -eq 0) {
    throw "No dependency checksums were found in $ChecksumPath"
}
foreach ($entry in $expected.GetEnumerator()) {
    $partPath = Join-Path $target $entry.Key
    if (-not (Test-Path -LiteralPath $partPath -PathType Leaf)) {
        throw "Dependency archive part is missing: $($entry.Key)"
    }
    $actual = (Get-FileHash -LiteralPath $partPath -Algorithm SHA256).Hash
    if ($actual -ne $entry.Value) {
        throw "Dependency checksum mismatch for $($entry.Key): expected $($entry.Value), got $actual"
    }
}

$firstPart = Join-Path $target "$ArchivePrefix.7z.001"
if (-not (Test-Path -LiteralPath $firstPart -PathType Leaf)) {
    throw "First dependency archive part is missing: $firstPart"
}
& $sevenZip x $firstPart "-o$RvcRoot" -y
if ($LASTEXITCODE -ne 0) {
    throw "7-Zip dependency extraction failed with exit code $LASTEXITCODE"
}

foreach ($requiredPath in @(
    (Join-Path $RvcRoot 'runtime\python.exe'),
    (Join-Path $RvcRoot 'assets\hubert_base\pytorch_model.bin'),
    (Join-Path $RvcRoot 'assets\weights\kikiV1.pth'),
    (Join-Path $RvcRoot 'assets\indices\kikiV1.index'),
    (Join-Path $RvcRoot 'assets\rmvpe\rmvpe.pt')
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Restored dependency is missing: $requiredPath"
    }
}
Remove-Item -LiteralPath $target -Recurse -Force
Write-Host "Verified and restored build dependencies from $Repository release $ReleaseTag." -ForegroundColor Green
