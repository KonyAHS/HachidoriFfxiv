# Builds hdw_native.dll: Hachidori's lookup engine (hoshidicts + wasm/bindings.cpp)
# compiled natively for the plugin's offline mode. Sources are pinned in
# sources.json, fetched into native/src and patched for MSVC (native/patches).
# Requires git, CMake 3.22+ and Visual Studio with the C++ workload (x64).
#
#   ./native/build.ps1          # build when an input changed
#   ./native/build.ps1 -Force   # always rebuild
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sources = Get-Content -LiteralPath (Join-Path $root 'sources.json') -Raw | ConvertFrom-Json
$srcDir = Join-Path $root 'src'
$buildDir = Join-Path $root 'build'
$binDir = Join-Path $root 'bin'
$dll = Join-Path $binDir 'hdw_native.dll'
$info = Join-Path $binDir 'hdw_native.json'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." }
}

# Every file that decides the DLL's contents, hashed into one stamp.
function Get-InputStamp {
    $files = @('sources.json', 'CMakeLists.txt', 'hdw_native_windows.cpp', 'build.ps1') |
        ForEach-Object { Join-Path $root $_ }
    $files += Get-ChildItem -LiteralPath (Join-Path $root 'patches'), (Join-Path $root 'compat') -Recurse -File |
        Sort-Object FullName | ForEach-Object FullName
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.MemoryStream]::new()
        foreach ($file in $files) {
            # Line endings depend on the checkout; hash the normalised text.
            $text = [IO.File]::ReadAllText($file).Replace("`r`n", "`n")
            $bytes = [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFileName($file) + "`n" + $text)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        return ([BitConverter]::ToString($sha.ComputeHash($stream.ToArray())) -replace '-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

$stamp = Get-InputStamp
if (-not $Force -and (Test-Path -LiteralPath $dll) -and (Test-Path -LiteralPath $info)) {
    $existing = Get-Content -LiteralPath $info -Raw | ConvertFrom-Json
    if ($existing.inputStamp -eq $stamp) {
        Write-Host "hdw_native.dll is up to date ($($sources.hachidori.version), hoshidicts $($sources.hoshidicts.commit.Substring(0, 7)))."
        return
    }
}

New-Item -ItemType Directory -Force -Path $srcDir, $binDir | Out-Null

# --- hoshidicts at the pinned commit, with its submodules, plus the MSVC patch.
$hoshidicts = Join-Path $srcDir 'hoshidicts'
$head = if (Test-Path -LiteralPath (Join-Path $hoshidicts '.git')) { (git -C $hoshidicts rev-parse HEAD 2>$null) } else { '' }
if ($head -ne $sources.hoshidicts.commit) {
    if (Test-Path -LiteralPath $hoshidicts) { Remove-Item -LiteralPath $hoshidicts -Recurse -Force }
    New-Item -ItemType Directory -Path $hoshidicts | Out-Null
    Invoke-Checked 'git init' { git -C $hoshidicts init --quiet }
    Invoke-Checked 'git remote' { git -C $hoshidicts remote add origin $sources.hoshidicts.repository }
    Invoke-Checked 'Fetching hoshidicts' { git -C $hoshidicts fetch --quiet --depth 1 origin $sources.hoshidicts.commit }
    Invoke-Checked 'Checking out hoshidicts' { git -C $hoshidicts -c advice.detachedHead=false checkout --quiet FETCH_HEAD }
    Invoke-Checked 'Fetching hoshidicts submodules' { git -C $hoshidicts submodule update --init --recursive --depth 1 --quiet }
}
Invoke-Checked 'Resetting hoshidicts' { git -C $hoshidicts checkout --quiet -- . }
Invoke-Checked 'Patching hoshidicts' { git -C $hoshidicts apply --whitespace=nowarn (Join-Path $root 'patches/hoshidicts-msvc.patch') }

# --- Hachidori's bindings.cpp at the pinned commit, checked against its hash.
$hachidori = Join-Path $srcDir 'hachidori'
$bindings = Join-Path $hachidori $sources.hachidori.bindings
New-Item -ItemType Directory -Force -Path (Split-Path $bindings) | Out-Null
$repoPath = ([Uri]$sources.hachidori.repository).AbsolutePath.Trim('/')
$url = "https://raw.githubusercontent.com/$repoPath/$($sources.hachidori.commit)/$($sources.hachidori.bindings)"
Invoke-WebRequest -Uri $url -OutFile $bindings -UseBasicParsing
$actual = (Get-FileHash -LiteralPath $bindings -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $sources.hachidori.bindingsSha256) {
    throw "bindings.cpp hash mismatch: expected $($sources.hachidori.bindingsSha256), got $actual."
}
Push-Location $hachidori
try { Invoke-Checked 'Patching bindings.cpp' { git apply --whitespace=nowarn (Join-Path $root 'patches/bindings-windows.patch') } }
finally { Pop-Location }

# --- Configure and build with the newest Visual Studio CMake knows.
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    if (-not (Test-Path -LiteralPath $cmake)) { throw 'CMake was not found. Install CMake or the Visual Studio C++ CMake tools.' }
}

Invoke-Checked 'Configuring hdw_native' {
    & $cmake -S $root -B $buildDir -A x64 `
        "-DHDW_SOURCE_DIR=$srcDir" `
        "-DHDW_HACHIDORI_VERSION=$($sources.hachidori.version)" `
        "-DHDW_HACHIDORI_COMMIT=$($sources.hachidori.commit)" `
        "-DHDW_HOSHIDICTS_COMMIT=$($sources.hoshidicts.commit)" `
        "-DHDW_DICTIONARY_FORMAT=$($sources.hoshidicts.newestDictionaryFormat)"
}
Invoke-Checked 'Building hdw_native' { & $cmake --build $buildDir --config Release --target hdw_native --parallel }

Copy-Item -LiteralPath (Join-Path $buildDir 'Release/hdw_native.dll') -Destination $dll -Force
[ordered]@{
    hachidori = $sources.hachidori.version
    hachidoriCommit = $sources.hachidori.commit
    hoshidictsCommit = $sources.hoshidicts.commit
    dictionaryFormat = $sources.hoshidicts.newestDictionaryFormat
    inputStamp = $stamp
} | ConvertTo-Json | Set-Content -LiteralPath $info -Encoding utf8
Write-Host "Built $dll"
