param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [string]$NotesOut
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOutput = Join-Path $taskRoot 'HachidoriFfxiv/bin/x64/Release'
$taskManifest = Get-Content (Join-Path $taskOutput 'HachidoriFfxiv.json') -Raw | ConvertFrom-Json
# Dalamud stamps a four-part AssemblyVersion (2.0 -> 2.0.0.0); a tag may omit trailing .0 parts.
function Get-TaskFullVersion([string]$taskValue) {
    $taskParts = @($taskValue.Split('.'))
    while ($taskParts.Count -lt 4) { $taskParts += '0' }
    return [version]($taskParts -join '.')
}
if ($Tag -cnotmatch '^v\d+(\.\d+){1,3}$' -or (Get-TaskFullVersion $Tag.Substring(1)) -ne (Get-TaskFullVersion $taskManifest.AssemblyVersion)) {
    throw "Tag $Tag must match the plugin version $($taskManifest.AssemblyVersion), e.g. v2.0 or v2.0.0.0."
}
# Every release must have a curated description of features in CHANGELOG.md.
$notesParams = @{
    Tag        = $Tag
    Repository = $Repository
}
if (-not [string]::IsNullOrWhiteSpace($NotesOut)) {
    $notesParams.OutFile = $NotesOut
    & (Join-Path $PSScriptRoot 'release-notes.ps1') @notesParams
}
else {
    $null = & (Join-Path $PSScriptRoot 'release-notes.ps1') @notesParams
}
$taskDist = Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Force -Path $taskDist | Out-Null
Copy-Item (Join-Path $taskOutput 'HachidoriFfxiv/latest.zip') (Join-Path $taskDist 'latest.zip')
$taskDictZip = Join-Path $taskRoot 'dictionaries/ffxiv/FFXIV-Hachidori-JP-EN.zip'
if (!(Test-Path -LiteralPath $taskDictZip -PathType Leaf)) { throw 'dictionaries/ffxiv/FFXIV-Hachidori-JP-EN.zip is missing.' }
Copy-Item $taskDictZip (Join-Path $taskDist 'FFXIV-Hachidori-JP-EN.zip')
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
# hdw_native.dll's corresponding source: the pinned hoshidicts and Hachidori
# sources exactly as native/build.ps1 fetched and patched them for this build.
$taskNativeSource = Join-Path $taskRoot 'native/src'
if (!(Test-Path -LiteralPath (Join-Path $taskNativeSource 'hoshidicts'))) { throw 'native/src is missing; run native/build.ps1 first.' }
$taskNativeZip = [IO.Compression.ZipFile]::Open((Join-Path $taskDist 'native-source.zip'), [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskFile in @('native/sources.json', 'native/CMakeLists.txt', 'native/build.ps1', 'native/hdw_native_windows.cpp')) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskNativeZip, (Join-Path $taskRoot $taskFile), $taskFile) | Out-Null
    }
    Get-ChildItem -LiteralPath (Join-Path $taskRoot 'native/patches'), (Join-Path $taskRoot 'native/compat'), $taskNativeSource -Recurse -File -Force |
        Where-Object { $_.FullName -notmatch '[\\/]\.git([\\/]|$)' } |
        ForEach-Object {
            $taskEntry = [IO.Path]::GetRelativePath($taskRoot, $_.FullName).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskNativeZip, $_.FullName, $taskEntry) | Out-Null
        }
}
finally { $taskNativeZip.Dispose() }
$taskChecksums = foreach ($taskName in @('latest.zip', 'pluginmaster.json', 'FFXIV-Hachidori-JP-EN.zip', 'source.zip', 'native-source.zip')) {
    $taskHash = (Get-FileHash (Join-Path $taskDist $taskName) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$taskHash  $taskName"
}
[IO.File]::WriteAllLines((Join-Path $taskDist 'SHA256SUMS.txt'), $taskChecksums, [Text.UTF8Encoding]::new($false))
Write-Host "Release assets ready in dist for $Tag"
