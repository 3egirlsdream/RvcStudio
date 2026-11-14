[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('rvc-packaging-test-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $testRoot 'stage'
$output = Join-Path $testRoot 'update'
function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Put([string]$Relative, [string]$Value) {
    $target = Join-Path $stage $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.File]::WriteAllText($target, $Value, [Text.UTF8Encoding]::new($false))
}
$rsa = [Security.Cryptography.RSA]::Create(2048)
try {
    Put 'RVC Studio.exe' 'app-v1'
    Put 'engine/main.py' 'engine-v1'
    Put 'runtime/python.exe' 'runtime-v1'
    Put 'assets/weights/default.pth' 'default-model'
    Put 'configs/custom.json' 'user-config'
    Put 'configs/config.py' 'config-code'
    Put 'configs/config.json' 'user-defaults'
    Put 'configs/v1/32k.json' 'model-architecture'
    Put 'LICENSE' 'license'
    Put 'engine/__pycache__/main.pyc' 'ignore'
    $build = Join-Path $PSScriptRoot 'Build-Update.ps1'
    & $build -Phase Prepare -Version 1.1.0 -StageRoot $stage -UpdateRoot $output
    $first = Get-Content -LiteralPath (Join-Path $stage 'update-baseline.json') -Raw | ConvertFrom-Json
    Assert-That ($first.files.Count -eq 5) 'Package contains unexpected files.'
    Assert-That (-not ($first.files.path -match 'runtime|assets|custom.json|pyc')) 'Protected files entered the small package.'
    Put 'RVC Studio.exe' 'app-v2'
    Put 'engine/main.py' 'engine-v2'
    Put 'configs/config.json' 'new-user-defaults'
    Put 'configs/v1/32k.json' 'new-model-architecture'
    & $build -Phase Prepare -Version 1.2.0 -StageRoot $stage -UpdateRoot $output
    $second = Get-Content -LiteralPath (Join-Path $stage 'update-baseline.json') -Raw | ConvertFrom-Json
    Assert-That ($first.environmentId -eq $second.environmentId) 'Code-only changes must preserve the environment ID.'
    Put 'runtime/python.exe' 'runtime-v2'
    & $build -Phase Prepare -Version 1.2.0 -StageRoot $stage -UpdateRoot $output
    $third = Get-Content -LiteralPath (Join-Path $stage 'update-baseline.json') -Raw | ConvertFrom-Json
    Assert-That ($third.environmentId -ne $second.environmentId) 'Runtime changes must change the environment ID.'
    Put 'assets/weights/default.pth' 'new-default-model'
    & $build -Phase Prepare -Version 1.2.0 -StageRoot $stage -UpdateRoot $output
    $fourth = Get-Content -LiteralPath (Join-Path $stage 'update-baseline.json') -Raw | ConvertFrom-Json
    Assert-That ($third.environmentId -ne $fourth.environmentId) 'Bundled model changes must change the environment ID.'
    $installer = Join-Path $testRoot 'RVC-Studio-NVIDIA-Setup.exe'
    [IO.File]::WriteAllText($installer, 'fixture-installer')
    & $build -Phase Finalize -Version 1.2.0 -StageRoot $stage -UpdateRoot $output -Installer $installer -Repository 'test/repo' -PrivateKeyPem $rsa.ExportPkcs8PrivateKeyPem() -PublicKeyPem $rsa.ExportSubjectPublicKeyInfoPem()
    $envelope = Get-Content -LiteralPath (Join-Path $output 'stable.json') -Raw | ConvertFrom-Json
    Assert-That ($rsa.VerifyData([Convert]::FromBase64String($envelope.payload), [Convert]::FromBase64String($envelope.signature), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) 'Manifest signature failed.'
    $manifest = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload)) | ConvertFrom-Json
    Assert-That ($manifest.package.url -eq 'https://huggingface.co/test/repo/resolve/main/releases/v1.2.0/RVC-Studio-Update.zip?download=true') 'Invalid package URL.'
    & $build -Phase Finalize -Version 1.2.0 -StageRoot $stage -UpdateRoot $output -Installer $installer -PrivateKeyPem ''
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $output 'stable.json'))) 'Unsigned build retained a stale signed manifest.'
    Write-Host 'PASS: package boundaries, component IDs, signed manifest, URLs, unsigned-build guard.'
} finally {
    $rsa.Dispose()
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\rvc-packaging-test-'
    if ($resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
