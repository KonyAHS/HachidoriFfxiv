param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][string]$Tag
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOutput = Join-Path $taskRoot 'HachidoriFfxiv/bin/x64/Release'
$taskManifest = Get-Content (Join-Path $taskOutput 'HachidoriFfxiv.json') -Raw | ConvertFrom-Json
if ($Tag -cne "v$($taskManifest.AssemblyVersion)") { throw 'Tag must match the complete plugin version, e.g. v0.1.5.0.' }
$taskDist = Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Force -Path $taskDist | Out-Null
Copy-Item (Join-Path $taskOutput 'HachidoriFfxiv/latest.zip') (Join-Path $taskDist 'latest.zip')
$taskBase = "https://github.com/$Repository/releases/download/$Tag"
$taskManifest | Add-Member NoteProperty RepoUrl "https://github.com/$Repository" -Force
$taskManifest | Add-Member NoteProperty DownloadLinkInstall "$taskBase/latest.zip" -Force
$taskManifest | Add-Member NoteProperty DownloadLinkUpdate "$taskBase/latest.zip" -Force
$taskManifest | Add-Member NoteProperty DownloadLinkTesting "$taskBase/latest.zip" -Force
$taskManifest | Add-Member NoteProperty LastUpdate ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) -Force
# Explicit array wrapper: a custom repository is always a JSON array.
$taskJson = ConvertTo-Json -InputObject @($taskManifest) -Depth 10
[IO.File]::WriteAllText((Join-Path $taskDist 'pluginmaster.json'), $taskJson + "`n", [Text.UTF8Encoding]::new($false))
# A curated source archive avoids publishing local history, settings, logs or builds.
Push-Location $taskRoot
try {
    git archive --format=zip --output=dist/source.zip HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
} finally { Pop-Location }
$taskChecksums = foreach ($taskName in @('latest.zip', 'pluginmaster.json', 'source.zip')) {
    $taskHash = (Get-FileHash (Join-Path $taskDist $taskName) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$taskHash  $taskName"
}
[IO.File]::WriteAllLines((Join-Path $taskDist 'SHA256SUMS.txt'), $taskChecksums, [Text.UTF8Encoding]::new($false))
Write-Host "Release assets ready in dist for $Tag"
