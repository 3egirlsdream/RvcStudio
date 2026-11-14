[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('rvc-store-packaging-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $testRoot 'source'
$stage=Join-Path $testRoot 'stage'
try {
    foreach($tree in @('weights','indices')) {New-Item -ItemType Directory -Path (Join-Path $source "assets\$tree") -Force | Out-Null}
    $free=@('guanguanV1','keruanV1','kikiV1','youzhanv2-xi')
    foreach($model in $free) {
        [IO.File]::WriteAllText((Join-Path $source "assets\weights\$model.pth"),'free')
        if($model -ne 'youzhanv2-xi') {[IO.File]::WriteAllText((Join-Path $source "assets\indices\$model.index"),'index')}
    }
    foreach($model in @('mygf-dx','mygf-xnf','mygf-zyss2','new-member')) {
        [IO.File]::WriteAllText((Join-Path $source "assets\weights\$model.pth"),'private model')
        [IO.File]::WriteAllText((Join-Path $source "assets\indices\$model.index"),'private index')
    }
    & (Join-Path $PSScriptRoot 'Copy-BuiltinModels.ps1') -SourceRoot $source -StageRoot $stage
    $models=@(Get-ChildItem -LiteralPath (Join-Path $stage 'assets\weights') -Filter '*.pth')
    if($models.Count -ne 4 -or @($models | Where-Object BaseName -NotIn $free).Count) {throw 'Free-model boundary failed.'}
    if(@(Get-ChildItem -LiteralPath (Join-Path $stage 'assets\indices') -Filter '*.index').Count -ne 3) {throw 'Expected three free indices.'}
    if(-not (Test-Path -LiteralPath (Join-Path $source 'assets\weights\mygf-dx.pth'))) {throw 'Source models were modified.'}
    [IO.File]::WriteAllText((Join-Path $stage 'assets\weights\rogue.pth'),'unexpected')
    $rejected=$false
    try {& (Join-Path $PSScriptRoot 'Copy-BuiltinModels.ps1') -SourceRoot $source -StageRoot $stage} catch {$rejected=$true}
    if(-not $rejected) {throw 'Contaminated staging directory must fail.'}
    Write-Host 'PASS: four free models only; optional missing index; private originals preserved; contaminated stage rejected.'
} finally {
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)) {throw 'Unsafe temporary cleanup target.'}
    if(Test-Path -LiteralPath $resolved) {Remove-Item -LiteralPath $resolved -Recurse -Force}
}
