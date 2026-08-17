[CmdletBinding()]
param(
    [ValidateRange(100, 1900)]
    [int]$VolumeSizeMiB = 1900,
    [string]$ArchivePrefix = 'rvc-studio-build-deps-v1'
)

$ErrorActionPreference = 'Stop'
$PackagingRoot = $PSScriptRoot
$RvcRoot = Split-Path -Parent $PackagingRoot
$StageRoot = Join-Path $PackagingRoot 'output\stage'
$OutputRoot = Join-Path $PackagingRoot 'output\dependencies'
$ChecksumPath = Join-Path $PackagingRoot 'dependencies\SHA256SUMS.txt'

$sevenZip = @(
    (Get-Command 7z -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
    'C:\Program Files\7-Zip\7z.exe',
    'C:\Program Files (x86)\7-Zip\7z.exe'
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $sevenZip) {
    throw '7-Zip was not found. Install it with: winget install 7zip.7zip'
}

$requiredInputs = @(
    (Join-Path $StageRoot 'runtime\python.exe'),
    (Join-Path $StageRoot 'assets\hubert_base\pytorch_model.bin'),
    (Join-Path $StageRoot 'assets\weights\kikiV1.pth'),
    (Join-Path $StageRoot 'assets\indices\kikiV1.index'),
    (Join-Path $StageRoot 'assets\rmvpe\rmvpe.pt')
)
foreach ($inputPath in $requiredInputs) {
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
        throw "Required staged dependency is missing: $inputPath"
    }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$resolvedOutput = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\') + '\'
Get-ChildItem -LiteralPath $OutputRoot -File -Filter "$ArchivePrefix.7z*" | ForEach-Object {
    if (-not $_.FullName.StartsWith($resolvedOutput, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete an archive outside the dependency output: $($_.FullName)"
    }
    Remove-Item -LiteralPath $_.FullName -Force
}

$archivePath = Join-Path $OutputRoot "$ArchivePrefix.7z"
Push-Location $StageRoot
try {
    & $sevenZip a -t7z -mx=5 -mmt=on "-v${VolumeSizeMiB}m" $archivePath runtime assets
    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$parts = @(Get-ChildItem -LiteralPath $OutputRoot -File -Filter "$ArchivePrefix.7z.*" | Sort-Object Name)
if ($parts.Count -eq 0) {
    throw "7-Zip did not create any split archive parts in $OutputRoot"
}
$checksumLines = $parts | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    "$hash *$($_.Name)"
}
$checksumLines | Set-Content -LiteralPath $ChecksumPath -Encoding ascii

$total = ($parts | Measure-Object Length -Sum).Sum
Write-Host ("Created {0} dependency parts ({1:N2} GiB) in {2}" -f $parts.Count, ($total / 1GB), $OutputRoot) -ForegroundColor Green
Write-Host "Updated checksums: $ChecksumPath" -ForegroundColor Green
