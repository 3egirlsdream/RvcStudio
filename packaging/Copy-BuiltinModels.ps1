[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$StageRoot)
$ErrorActionPreference = 'Stop'
# The v1.0.3 build-dependencies-v1 inventory is the immutable free-model boundary.
$freeModels = @('guanguanV1','keruanV1','kikiV1','youzhanv2-xi')
foreach ($tree in @('weights','indices')) { New-Item -ItemType Directory -Path (Join-Path $StageRoot "assets\$tree") -Force | Out-Null }
foreach ($name in $freeModels) {
    $model = Join-Path $SourceRoot "assets\weights\$name.pth"
    if (-not (Test-Path -LiteralPath $model)) { throw "Required free model missing: $name" }
    Copy-Item -LiteralPath $model -Destination (Join-Path $StageRoot "assets\weights\$name.pth") -Force
    $index = Join-Path $SourceRoot "assets\indices\$name.index"
    if (Test-Path -LiteralPath $index) { Copy-Item -LiteralPath $index -Destination (Join-Path $StageRoot "assets\indices\$name.index") -Force }
    elseif ($name -ne 'youzhanv2-xi') { throw "Required free index missing: $name" }
    foreach ($suffix in @('.rvcstudio.json','.pth.rvcstudio.json')) {
        $profile = Join-Path $SourceRoot "assets\weights\$name$suffix"
        if (Test-Path -LiteralPath $profile) { Copy-Item -LiteralPath $profile -Destination (Join-Path $StageRoot "assets\weights\$name$suffix") -Force }
    }
}
$packagedModels = @(Get-ChildItem -LiteralPath (Join-Path $StageRoot 'assets\weights') -File -Filter '*.pth')
if (@($packagedModels | Where-Object { $_.BaseName -notin $freeModels }).Count) { throw 'An unapproved or membership model entered the public installer.' }
$packagedIndices = @(Get-ChildItem -LiteralPath (Join-Path $StageRoot 'assets\indices') -File -Filter '*.index')
if (@($packagedIndices | Where-Object { $_.BaseName -notin $freeModels }).Count) { throw 'An unapproved membership index entered the public installer.' }
