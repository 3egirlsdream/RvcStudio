[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+(\.[0-9]+){1,3}$')]
    [string]$Version,
    [string]$ReleaseNotes = '',
    [ValidatePattern('^https?://')]
    [string]$UpdateServiceUrl = 'https://thankful.top',
    [ValidatePattern('^https?://')]
    [string]$DownloadUrl = 'https://thankful.top',
    [string]$Channel = 'RvcStudio'
)

$ErrorActionPreference = 'Stop'

function Invoke-VersionApi {
    param(
        [Parameter(Mandatory)][string]$Endpoint,
        [Parameter(Mandatory)][hashtable]$Query
    )
    $queryString = ($Query.GetEnumerator() | Sort-Object Key | ForEach-Object {
        '{0}={1}' -f [Uri]::EscapeDataString([string]$_.Key), [Uri]::EscapeDataString([string]$_.Value)
    }) -join '&'
    $uri = '{0}/api/CloudSync/{1}?{2}' -f $UpdateServiceUrl.TrimEnd('/'), $Endpoint, $queryString
    $response = Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec 30
    if ($null -eq $response -or $response.success -ne $true) {
        $detail = if ($response.message.content) { $response.message.content } else { 'Unknown update-service error.' }
        throw "Update service request '$Endpoint' failed: $detail"
    }
    return $response
}

$memo = if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) { "RVC Studio $Version" } else { $ReleaseNotes.Trim() }
$clientVersion = [Version]::Parse($Version)
$lookup = Invoke-VersionApi -Endpoint 'GetVersion' -Query @{ Client = $Channel }

if ($null -eq $lookup.data -or [string]::IsNullOrWhiteSpace([string]$lookup.data.VERSION)) {
    Invoke-VersionApi -Endpoint 'InsertVersion' -Query @{
        Client = $Channel
        Path = $DownloadUrl
        Version = $Version
        Memo = $memo
    } | Out-Null
    Write-Host "Created server channel '$Channel' at version $Version." -ForegroundColor Green
}
else {
    $serverVersion = [Version]::Parse([string]$lookup.data.VERSION)
    if ($clientVersion -le $serverVersion) {
        Write-Host "Server version $serverVersion is not older than client version $clientVersion; no update was made." -ForegroundColor Yellow
        return
    }
    Invoke-VersionApi -Endpoint 'UpdateVersion' -Query @{
        Client = $Channel
        Version = $Version
        Memo = $memo
    } | Out-Null
    Write-Host "Updated server channel '$Channel': $serverVersion -> $clientVersion." -ForegroundColor Green
}

$verification = Invoke-VersionApi -Endpoint 'GetVersion' -Query @{ Client = $Channel }
if ($null -eq $verification.data -or [string]$verification.data.VERSION -ne $Version) {
    throw "Server version verification failed after publishing $Version."
}
