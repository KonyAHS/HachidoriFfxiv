#Requires -Version 7.0
# Builds and verifies only when the checkout changed since the last verified
# build; pass -Rebuild to always run the full build and regression checks.
[CmdletBinding(SupportsShouldProcess)]
param([switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$launcher = Join-Path $env:APPDATA 'XIVLauncher'
$settings = Get-Content -LiteralPath (Join-Path $launcher 'dalamudConfig.json') -Raw | ConvertFrom-Json
$targets = @(Get-Process ffxiv_dx11 -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Path) { Join-Path (Split-Path $_.Path) 'HachidoriFfxiv.dll' }
} | Sort-Object -Unique | Where-Object {
    $null -ne $settings.DevPluginSettings.PSObject.Properties[$_] -and (Test-Path -LiteralPath $_ -PathType Leaf)
})
if ($targets.Count -ne 1) {
    throw 'Expected one running FFXIV installation with its HachidoriFfxiv.dll registered as a Dalamud dev plugin.'
}
$destination = $targets[0]
$gameDirectory = Split-Path $destination

function Assert-PluginFeatures([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $types = @($metadata.TypeDefinitions | ForEach-Object {
                $definition = $metadata.GetTypeDefinition($_)
                "$($metadata.GetString($definition.Namespace)).$($metadata.GetString($definition.Name))"
            })
        }
        finally { $pe.Dispose() }
    }
    finally { $stream.Dispose() }

    $required = @(
        'HachidoriFfxiv.Media.MediaMiner'
        'HachidoriFfxiv.Media.AnkiConnect'
        'HachidoriFfxiv.Media.GameFrames'
        'HachidoriFfxiv.Media.GameAudioRecorder'
        'HachidoriFfxiv.Media.AnimationEncoder'
        'HachidoriFfxiv.Audio.PronunciationDecoder'
        'HachidoriFfxiv.Ui.NpcImageCapture'
        'HachidoriFfxiv.Rendering.NativeChatLayout'
        'HachidoriFfxiv.Offline.OfflineMode'
        'HachidoriFfxiv.WebView.WebViewPopupController'
    )
    $missing = @($required | Where-Object { $_ -notin $types })
    if ($missing.Count -gt 0) {
        throw "Build is missing the integrated mining, chat and offline features: $($missing -join ', ')."
    }
}

& {
    $WhatIfPreference = $false
    & (Join-Path $PSScriptRoot 'build.ps1') -Verify -IfChanged:(!$Rebuild)
}
$output = Join-Path $PSScriptRoot 'HachidoriFfxiv/bin/x64/Release'
$candidate = Join-Path $output 'HachidoriFfxiv.dll'
Assert-PluginFeatures $candidate
$next = [Diagnostics.FileVersionInfo]::GetVersionInfo($candidate)
$current = [Diagnostics.FileVersionInfo]::GetVersionInfo($destination)
if ([version]$next.FileVersion -lt [version]$current.FileVersion) {
    throw "The installed version is $($current.ProductVersion). This build is older ($($next.ProductVersion))."
}

$hash = (Get-FileHash -LiteralPath $candidate).Hash
$runtimeManifest = 'HachidoriFfxiv.deps.json'
$deps = Get-Content -LiteralPath (Join-Path $output $runtimeManifest) -Raw | ConvertFrom-Json
$runtime = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
# Native DLLs outside deps.json: offline engine (hdw_native.dll) and WebView2 loader (WebView2Loader.dll).
$files = @($runtimeManifest, 'HachidoriFfxiv.json', 'hdw_native.dll', 'hdw_native.json', 'WebView2Loader.dll')
foreach ($engineFile in @('hdw_native.dll', 'hdw_native.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $output $engineFile) -PathType Leaf)) {
        throw "Build is missing the offline engine ($engineFile). Run native/build.ps1."
    }
}
if (!(Test-Path -LiteralPath (Join-Path $output 'WebView2Loader.dll') -PathType Leaf)) {
    throw 'Build is missing WebView2Loader.dll.'
}
foreach ($library in $runtime.PSObject.Properties) {
    foreach ($asset in $library.Value.runtime.PSObject.Properties) {
        $name = [IO.Path]::GetFileName($asset.Name)
        if ($name -ne 'HachidoriFfxiv.dll') { $files += $name }
    }
}
# Replace the DLL last so Dalamud reloads against the complete build.
$files = @($files | Sort-Object -Unique) + 'HachidoriFfxiv.dll'
$sameNativeEngine = (Test-Path -LiteralPath (Join-Path $gameDirectory 'hdw_native.dll') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $gameDirectory 'hdw_native.json') -PathType Leaf) -and
    (Get-FileHash -LiteralPath (Join-Path $gameDirectory 'hdw_native.json')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $output 'hdw_native.json')).Hash
$changed = @($files | Where-Object {
    if ($_ -eq 'hdw_native.dll' -and $sameNativeEngine) { return $false }
    $installed = Join-Path $gameDirectory $_
    !(Test-Path -LiteralPath $installed -PathType Leaf) -or
        (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath (Join-Path $output $_)).Hash
})
if ($changed.Count -eq 0) {
    Write-Host "Already installed: $($next.ProductVersion) ($hash)"
    return
}
if (!$PSCmdlet.ShouldProcess($destination, "Deploy $($next.ProductVersion) over $($current.ProductVersion)")) { return }

$backup = Join-Path $env:TEMP ('HachidoriFfxiv-deploy-' + [guid]::NewGuid())
$null = New-Item -Path $backup -ItemType Directory
$hadPrevious = @{}
foreach ($name in $changed) {
    $installed = Join-Path $gameDirectory $name
    $hadPrevious[$name] = Test-Path -LiteralPath $installed -PathType Leaf
    if ($hadPrevious[$name]) { Copy-Item -LiteralPath $installed -Destination $backup }
}

$attempted = @()
try {
    foreach ($name in $changed) {
        Copy-Item -LiteralPath (Join-Path $output $name) -Destination (Join-Path $gameDirectory $name) -Force
        $attempted += $name
    }
    if ((Get-FileHash -LiteralPath $destination).Hash -ne $hash) {
        throw 'The installed DLL does not match the verified build.'
    }
}
catch {
    foreach ($name in $attempted) {
        $installed = Join-Path $gameDirectory $name
        if ($hadPrevious[$name]) {
            Copy-Item -LiteralPath (Join-Path $backup $name) -Destination $installed -Force
        }
        elseif (Test-Path -LiteralPath $installed -PathType Leaf) {
            Remove-Item -LiteralPath $installed
        }
    }
    throw
}

Write-Host "Installed $($next.ProductVersion) ($hash)"
Write-Host "Backup: $backup"
Write-Host "Check for 'Finished loading HachidoriFfxiv' in $(Join-Path $launcher 'dalamud.log')."
