[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Finalize')][string]$Phase = 'Prepare',
    [Parameter(Mandatory)][string]$Version,
    [string]$ReleaseNotes = '',
    [string]$Repository = $env:HF_RELEASE_REPOSITORY,
    [string]$PrivateKeyPem = $env:RVC_UPDATE_PRIVATE_KEY_PEM,
    [string]$PublicKeyPem = $env:RVC_UPDATE_PUBLIC_KEY_PEM,
    [string]$StageRoot = (Join-Path $PSScriptRoot 'output\stage'),
    [string]$UpdateRoot = (Join-Path $PSScriptRoot 'output\update'),
    [string]$Installer = (Join-Path $PSScriptRoot 'output\installer\RVC-Studio-NVIDIA-Setup.exe')
)
$ErrorActionPreference = 'Stop'

function Get-Record([IO.FileInfo]$File, [string]$Relative) {
    [ordered]@{ path = $Relative; size = $File.Length; sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash }
}
function Test-Managed([string]$Path) {
    $Path -cmatch '^(RVC Studio\.exe|realtime_service\.py|LICENSE|THIRD-PARTY-NOTICES\.txt|MIT[^/.]*|(?:engine|infer|i18n|tools|configs)/.+\.py|configs/v1/(?:32k|40k|48k)\.json|configs/v2/(?:32k|48k)\.json|licenses/.+)$'
}
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 15 -Compress), [Text.UTF8Encoding]::new($false))
}
New-Item -ItemType Directory -Path $UpdateRoot -Force | Out-Null
$baselinePath = Join-Path $StageRoot 'update-baseline.json'
$packagePath = Join-Path $UpdateRoot 'RVC-Studio-Update.zip'
if ($Phase -eq 'Prepare') {
    $oldPointer = Join-Path $UpdateRoot 'stable.json'
    if (Test-Path -LiteralPath $oldPointer) { Remove-Item -LiteralPath $oldPointer -Force }
    $managed = @()
    $environment = @()
    $prefix = [IO.Path]::GetFullPath($StageRoot).TrimEnd('\') + '\'
    $files = Get-ChildItem -LiteralPath $StageRoot -File -Recurse | Where-Object {
        $_.FullName -notmatch '[\\/]__pycache__[\\/]' -and $_.Extension -notin @('.pyc', '.pyo') -and
        $_.Name -notin @('release-manifest.json', 'update-baseline.json')
    }
    [string[]]$orderedPaths = @($files | ForEach-Object { $_.FullName })
    [Array]::Sort($orderedPaths, [StringComparer]::Ordinal)
    foreach ($filePath in $orderedPaths) {
        $file = Get-Item -LiteralPath $filePath
        $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        if ($relative -ceq 'configs/config.json') { continue }
        $record = Get-Record $file $relative
        if (Test-Managed $relative) { $managed += $record } else { $environment += $record }
    }
    if (-not ($managed | Where-Object { $_.path -ceq 'RVC Studio.exe' })) { throw 'The staged app is missing.' }
    $canonical = ($environment | ForEach-Object { "{0}`t{1}`t{2}" -f $_.path, $_.size, $_.sha256 }) -join "`n"
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $environmentId = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '') }
    finally { $hasher.Dispose() }
    Write-Json $baselinePath ([ordered]@{ version = $Version; environmentId = $environmentId; files = @($managed) })
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [IO.File]::Open($packagePath, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($record in $managed) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $StageRoot $record.path), $record.path, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    Write-Host ('Program update: {0:N0} bytes ({1:N1} MiB)' -f (Get-Item -LiteralPath $packagePath).Length, ((Get-Item -LiteralPath $packagePath).Length / 1MB))
    return
}

if (-not (Test-Path -LiteralPath $Installer)) { throw 'A complete installer is required before finalizing an update.' }
$signedPath = Join-Path $UpdateRoot 'stable.json'
# Never leave a stale signed manifest beside new unsigned output.
if (Test-Path -LiteralPath $signedPath) { Remove-Item -LiteralPath $signedPath -Force }
if ([string]::IsNullOrWhiteSpace($PrivateKeyPem)) {
    Write-Host 'Unsigned local build: package/baseline produced; publishing an installable update is disabled.' -ForegroundColor Yellow
    return
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'HF_RELEASE_REPOSITORY must be owner/repository.' }
if ([string]::IsNullOrWhiteSpace($PublicKeyPem)) { throw 'RVC_UPDATE_PUBLIC_KEY_PEM is required for a signed build.' }
$baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
if ($baseline.version -ne $Version) { throw 'The prepared package version does not match the manifest version.' }
$baseUrl = "https://huggingface.co/$Repository/resolve/main/releases/v$Version"
function Get-Artifact([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    [ordered]@{ url = "$baseUrl/$($file.Name)?download=true"; size = $file.Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
$manifest = [ordered]@{ schema = 1; version = $Version; notes = $ReleaseNotes; environmentId = $baseline.environmentId; package = (Get-Artifact $packagePath); installer = (Get-Artifact $Installer); files = @($baseline.files) }
$payload = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 15 -Compress))
$rsa = [Security.Cryptography.RSA]::Create()
$verifier = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem($PrivateKeyPem)
    $verifier.ImportFromPem($PublicKeyPem)
    if ($rsa.KeySize -lt 2048) { throw 'Use an RSA key of at least 2048 bits.' }
    $signature = $rsa.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    if (-not $verifier.VerifyData($payload, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'The signing key does not match the embedded public key.' }
    Write-Json $signedPath ([ordered]@{ payload = [Convert]::ToBase64String($payload); signature = [Convert]::ToBase64String($signature) })
} finally { $rsa.Dispose(); $verifier.Dispose() }
Write-Host ('Full installer: {0:N0} bytes; signed update manifest ready.' -f (Get-Item -LiteralPath $Installer).Length)
