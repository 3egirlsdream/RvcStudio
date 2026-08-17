[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$ReleaseNotes = '',
    [ValidatePattern('^https?://')]
    [string]$UpdateServiceUrl = 'https://thankful.top',
    [ValidatePattern('^https?://')]
    [string]$UpdatePath = 'https://thankful.top',
    [switch]$SkipInstaller,
    [switch]$SkipManifest,
    [switch]$SkipVersionPublish,
    [switch]$AllowNoCudaDevice,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$PackagingRoot = $PSScriptRoot
$RvcRoot = Split-Path -Parent $PackagingRoot
$AppProject = Join-Path $RvcRoot 'studio\RvcStudio.App\RvcStudio.App.csproj'
$OutputRoot = Join-Path $PackagingRoot 'output'
$StageRoot = Join-Path $OutputRoot 'stage'
$PublishRoot = Join-Path $OutputRoot 'app-publish'
$InstallerRoot = Join-Path $OutputRoot 'installer'
$UpdateChannel = 'RvcStudio'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $AppProject -Raw
    $configuredVersion = $projectXml.SelectNodes('/Project/PropertyGroup/Version') |
        ForEach-Object { $_.InnerText.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($configuredVersion)) {
        throw "The app project does not define a Version: $AppProject"
    }
    $Version = $configuredVersion
    Write-Host "Using app project version $Version." -ForegroundColor Cyan
}
if ($Version -notmatch '^[0-9]+(\.[0-9]+){1,3}$') {
    throw "Version '$Version' is invalid. Use a numeric version such as 1.2.0."
}

function Assert-Command {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$InstallHint
    )
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found. $InstallHint"
    }
}

function Find-InnoCompiler {
    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    return $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Reset-SafeDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $allowed = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside packaging output: $target"
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

function Invoke-Robocopy {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [string[]]$ExcludeDirectories = @(),
        [string[]]$ExcludeFiles = @()
    )
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $arguments = @($Source, $Destination, '/E', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($ExcludeDirectories.Count -gt 0) {
        $arguments += '/XD'
        $arguments += $ExcludeDirectories
    }
    if ($ExcludeFiles.Count -gt 0) {
        $arguments += '/XF'
        $arguments += $ExcludeFiles
    }
    & robocopy @arguments
    if ($LASTEXITCODE -ge 8) {
        throw "Robocopy failed with exit code $LASTEXITCODE while copying $Source"
    }
}

function Copy-Tree {
    param([Parameter(Mandatory)][string]$RelativePath)
    $source = Join-Path $RvcRoot $RelativePath
    $destination = Join-Path $StageRoot $RelativePath
    Invoke-Robocopy -Source $source -Destination $destination -ExcludeDirectories @('__pycache__') -ExcludeFiles @('*.pyc')
}

function Invoke-VersionApi {
    param(
        [Parameter(Mandatory)][string]$Endpoint,
        [Parameter(Mandatory)][hashtable]$Query
    )
    $queryString = ($Query.GetEnumerator() | Sort-Object Key | ForEach-Object {
        '{0}={1}' -f [Uri]::EscapeDataString([string]$_.Key), [Uri]::EscapeDataString([string]$_.Value)
    }) -join '&'
    $uri = '{0}/api/CloudSync/{1}?{2}' -f $UpdateServiceUrl.TrimEnd('/'), $Endpoint, $queryString
    try {
        $response = Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec 30
    }
    catch {
        throw "Update service request '$Endpoint' failed: $($_.Exception.Message)"
    }
    if ($null -eq $response -or $response.success -ne $true) {
        $detail = if ($null -ne $response.message -and -not [string]::IsNullOrWhiteSpace($response.message.content)) {
            $response.message.content
        }
        else {
            'The server did not return a successful response.'
        }
        throw "Update service request '$Endpoint' failed: $detail"
    }
    return $response
}

function Publish-VersionIfNewer {
    $memo = if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
        "RVC Studio $Version"
    }
    else {
        $ReleaseNotes.Trim()
    }
    $current = [Version]::Parse($Version)
    $lookup = Invoke-VersionApi -Endpoint 'GetVersion' -Query @{ Client = $UpdateChannel }

    if ($null -eq $lookup.data -or [string]::IsNullOrWhiteSpace([string]$lookup.data.VERSION)) {
        Invoke-VersionApi -Endpoint 'InsertVersion' -Query @{
            Client = $UpdateChannel
            Path = $UpdatePath
            Version = $Version
            Memo = $memo
        } | Out-Null
        Write-Host "Created update channel '$UpdateChannel' at version $Version." -ForegroundColor Green
    }
    else {
        try {
            $published = [Version]::Parse([string]$lookup.data.VERSION)
        }
        catch {
            throw "The update service contains an invalid version for '$UpdateChannel': $($lookup.data.VERSION)"
        }
        if ($current -le $published) {
            Write-Host "Server version $published is not older than package version $current; no update was published." -ForegroundColor Yellow
            return
        }
        Invoke-VersionApi -Endpoint 'UpdateVersion' -Query @{
            Client = $UpdateChannel
            Version = $Version
            Memo = $memo
        } | Out-Null
        Write-Host "Published update channel '$UpdateChannel': $published -> $current." -ForegroundColor Green
    }

    $verification = Invoke-VersionApi -Endpoint 'GetVersion' -Query @{ Client = $UpdateChannel }
    if ($null -eq $verification.data -or [string]$verification.data.VERSION -ne $Version) {
        throw "Update service verification failed after publishing version $Version."
    }
}

if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
    throw 'RVC Studio packages can only be built on Windows.'
}
Assert-Command -Name 'dotnet' -InstallHint 'Install the .NET 10 SDK.'
Assert-Command -Name 'robocopy' -InstallHint 'Use Windows 10/11 or install the Windows deployment tools.'
Assert-Command -Name 'subst' -InstallHint 'Use a standard Windows command environment.'

$requiredInputs = @(
    $AppProject,
    (Join-Path $RvcRoot 'runtime\python.exe'),
    (Join-Path $RvcRoot 'realtime_service.py'),
    (Join-Path $RvcRoot 'assets\hubert_base'),
    (Join-Path $RvcRoot 'assets\weights'),
    (Join-Path $RvcRoot 'assets\indices'),
    (Join-Path $RvcRoot 'assets\rmvpe\rmvpe.pt'),
    (Join-Path $PackagingRoot 'RVCStudio.iss'),
    (Join-Path $PackagingRoot 'verify_release.py'),
    (Join-Path $PackagingRoot 'generate_package_manifest.py')
)
foreach ($requiredInput in $requiredInputs) {
    if (-not (Test-Path -LiteralPath $requiredInput)) {
        throw "Required packaging input is missing: $requiredInput"
    }
}

$iscc = $null
if (-not $SkipInstaller) {
    $iscc = Find-InnoCompiler
    if (-not $iscc) {
        throw 'Inno Setup 6.5 or later is required. Install with: winget install JRSoftware.InnoSetup'
    }
    $securityModule = Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1'
    Import-Module -Name $securityModule -Force -ErrorAction Stop
    $vbCableSetup = Join-Path $PackagingRoot 'vendor\vb-cable\official-package\VBCABLE_Setup_x64.exe'
    if (-not (Test-Path -LiteralPath $vbCableSetup)) {
        throw "The official VB-CABLE installer is missing: $vbCableSetup"
    }
    $vbCableSignature = Get-AuthenticodeSignature -LiteralPath $vbCableSetup
    if ($vbCableSignature.Status -ne 'Valid') {
        throw "VB-CABLE installer signature is not valid: $($vbCableSignature.StatusMessage)"
    }
}

$dotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to query the installed .NET SDK.'
}
Write-Host "RVC Studio package preflight passed (.NET SDK $dotnetVersion, version $Version)." -ForegroundColor Green
if ($PreflightOnly) {
    Write-Host 'Preflight-only mode: no files were changed.' -ForegroundColor Yellow
    return
}

$drive = [IO.DriveInfo]::new(([IO.Path]::GetPathRoot($OutputRoot)))
if ($drive.AvailableFreeSpace -lt 15GB) {
    throw "At least 15 GiB of free disk space is required to assemble and compress the release."
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$installerAssets = Join-Path $PackagingRoot 'installer-assets'
New-Item -ItemType Directory -Path $installerAssets -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $RvcRoot 'LICENSE') -Destination (Join-Path $installerAssets 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $RvcRoot 'studio\RvcStudio.App\Assets\rvc-studio-icon-transparent.ico') -Destination (Join-Path $installerAssets 'rvc-studio-icon-transparent.ico') -Force
Reset-SafeDirectory -Path $StageRoot
Reset-SafeDirectory -Path $PublishRoot

& dotnet publish $AppProject -c Release -r win-x64 --self-contained true --nologo `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $PublishRoot
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}
Copy-Item -LiteralPath (Join-Path $PublishRoot 'RVC Studio.exe') -Destination $StageRoot

$runtimeSource = Join-Path $RvcRoot 'runtime'
$sitePackages = Join-Path $runtimeSource 'Lib\site-packages'
$runtimeExcludes = @(
    (Join-Path $sitePackages '~orch'),
    (Join-Path $sitePackages '~orch-2.7.1+cu118.dist-info'),
    (Join-Path $sitePackages '~orchgen'),
    (Join-Path $sitePackages 'nvidia'),
    (Join-Path $sitePackages 'nvidia_cublas_cu11-11.11.3.6.dist-info'),
    (Join-Path $sitePackages 'nvidia_cuda_nvrtc_cu11-11.8.89.dist-info'),
    (Join-Path $sitePackages 'nvidia_cuda_runtime_cu11-11.8.89.dist-info'),
    (Join-Path $sitePackages 'nvidia_cudnn_cu11-8.9.5.29.dist-info'),
    (Join-Path $sitePackages 'nvidia_cufft_cu11-10.9.0.58.dist-info'),
    (Join-Path $sitePackages 'onnxruntime'),
    (Join-Path $sitePackages 'onnxruntime_gpu-1.18.0.dist-info'),
    (Join-Path $sitePackages 'torch\include'),
    '__pycache__'
)
Invoke-Robocopy -Source $runtimeSource -Destination (Join-Path $StageRoot 'runtime') `
    -ExcludeDirectories $runtimeExcludes -ExcludeFiles @('*.lib', '*.pyc')

foreach ($tree in @('engine', 'configs', 'infer', 'i18n', 'tools')) {
    Copy-Tree -RelativePath $tree
}
foreach ($asset in @('assets\hubert_base', 'assets\weights', 'assets\indices')) {
    Copy-Tree -RelativePath $asset
}
New-Item -ItemType Directory -Path (Join-Path $StageRoot 'assets\rmvpe') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $RvcRoot 'assets\rmvpe\rmvpe.pt') -Destination (Join-Path $StageRoot 'assets\rmvpe\rmvpe.pt')

foreach ($file in @('realtime_service.py', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $RvcRoot $file) -Destination $StageRoot
}
$protocolFiles = @(Get-ChildItem -LiteralPath $RvcRoot -File -Filter 'MIT*')
if ($protocolFiles.Count -ne 1) {
    throw "Expected exactly one root MIT protocol notice, found $($protocolFiles.Count)."
}
Copy-Item -LiteralPath $protocolFiles[0].FullName -Destination $StageRoot
Copy-Item -LiteralPath (Join-Path $PackagingRoot 'THIRD-PARTY-NOTICES.txt') -Destination $StageRoot
Copy-Item -LiteralPath (Join-Path $PackagingRoot 'verify_release.py') -Destination (Join-Path $StageRoot 'tools\package_healthcheck.py')
New-Item -ItemType Directory -Path (Join-Path $StageRoot 'licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PackagingRoot 'vendor\inno\Inno-Setup-License.txt') -Destination (Join-Path $StageRoot 'licenses\Inno-Setup-License.txt')

$licenseRoot = Join-Path $StageRoot 'licenses\python-packages'
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $StageRoot 'runtime\Lib\site-packages') -Directory -Filter '*.dist-info' | ForEach-Object {
    $distribution = $_
    Get-ChildItem -LiteralPath $distribution.FullName -File | Where-Object {
        $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE)'
    } | ForEach-Object {
        $targetName = $distribution.Name + '--' + $_.Name
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenseRoot $targetName)
    }
}

$stagePython = Join-Path $StageRoot 'runtime\python.exe'
& $stagePython -I (Join-Path $PackagingRoot 'generate_package_manifest.py') --root $StageRoot --packages-only
if ($LASTEXITCODE -ne 0) {
    throw "Python package manifest generation failed with exit code $LASTEXITCODE"
}
$healthCheckArguments = @('-I', (Join-Path $StageRoot 'tools\package_healthcheck.py'), '--root', $StageRoot)
if (-not $AllowNoCudaDevice) {
    $healthCheckArguments += '--require-cuda'
}
& $stagePython @healthCheckArguments
if ($LASTEXITCODE -ne 0) {
    throw "Staged release health check failed with exit code $LASTEXITCODE"
}

if (-not $SkipManifest) {
    & $stagePython -I (Join-Path $PackagingRoot 'generate_package_manifest.py') --root $StageRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Release manifest generation failed with exit code $LASTEXITCODE"
    }
}

$stageMeasure = Get-ChildItem -LiteralPath $StageRoot -File -Recurse | Measure-Object Length -Sum
Write-Output ("Staged release: {0} files, {1:N2} GiB" -f $stageMeasure.Count, ($stageMeasure.Sum / 1GB))

if (-not $SkipInstaller) {
    Reset-SafeDirectory -Path $InstallerRoot
    $buildDrive = @('R:', 'S:', 'T:', 'U:', 'V:') | Where-Object {
        -not (Test-Path "$_\")
    } | Select-Object -First 1
    if (-not $buildDrive) {
        throw 'No free temporary drive letter is available from R: through V:.'
    }
    & subst $buildDrive $PackagingRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to map the temporary build drive $buildDrive"
    }
    try {
        & $iscc "/DMyAppVersion=$Version" "$buildDrive\RVCStudio.iss"
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        & subst $buildDrive /D
    }
    $setupExecutables = @(Get-ChildItem -LiteralPath $InstallerRoot -File -Filter 'RVC-Studio-NVIDIA-Setup.exe')
    $splitPayloads = @(Get-ChildItem -LiteralPath $InstallerRoot -File -Filter 'RVC-Studio-NVIDIA-Setup-*.bin')
    if ($setupExecutables.Count -ne 1 -or $splitPayloads.Count -ne 0) {
        throw "Expected one offline setup EXE and no split BIN payloads; found $($setupExecutables.Count) EXE and $($splitPayloads.Count) BIN files."
    }
    Copy-Item -LiteralPath (Join-Path $PackagingRoot 'DELIVERY-README.txt') -Destination (Join-Path $InstallerRoot 'README.txt')
    $hashLines = Get-ChildItem -LiteralPath $InstallerRoot -File | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash *$($_.Name)"
    }
    $hashLines | Set-Content -LiteralPath (Join-Path $InstallerRoot 'SHA256SUMS.txt') -Encoding ascii
    $installerMeasure = Get-ChildItem -LiteralPath $InstallerRoot -File | Measure-Object Length -Sum
    Write-Output ("Installer output: {0} files, {1:N2} GiB" -f $installerMeasure.Count, ($installerMeasure.Sum / 1GB))
    if ($SkipVersionPublish) {
        Write-Host 'Server version publishing was skipped by request.' -ForegroundColor Yellow
    }
    else {
        Publish-VersionIfNewer
    }
    Write-Host "Package completed: $InstallerRoot" -ForegroundColor Green
    Write-Host 'The complete offline package is RVC-Studio-NVIDIA-Setup.exe; README.txt and SHA256SUMS.txt are optional companion files.' -ForegroundColor Yellow
}
