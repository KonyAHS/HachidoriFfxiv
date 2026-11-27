param(
    [string]$Tag,
    [string]$Repository,
    [string]$OutFile,
    [string]$ChangelogPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'CHANGELOG.md'),
    [string]$ProjectPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'HachidoriFfxiv/HachidoriFfxiv.csproj'),
    [switch]$ValidateProjectVersion,
    [switch]$ValidateAll,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

function Get-NormalizedVersion([string]$Value) {
    $trimmed = $Value.Trim()
    if ($trimmed.StartsWith('v') -or $trimmed.StartsWith('V')) {
        $trimmed = $trimmed.Substring(1)
    }
    if ($trimmed -notmatch '^\d+(\.\d+){1,3}$') {
        throw "Invalid version format '$Value'. Expected e.g. v2.0.5 or 2.0.5."
    }
    $parts = @($trimmed.Split('.'))
    while ($parts.Count -lt 4) { $parts += '0' }
    return [version]($parts -join '.')
}

function Parse-ChangelogSections([string]$Content) {
    $lines = $Content -split "`r?`n"
    $sections = [System.Collections.Generic.List[hashtable]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $current = $null
    $bodyLines = [System.Collections.Generic.List[string]]::new()

    foreach ($line in $lines) {
        if ($line -match '^##\s+(v?\d+(?:\.\d+){1,3})(?:\s|$)') {
            if ($null -ne $current) {
                $current.Body = ($bodyLines -join "`n").Trim()
                $sections.Add($current)
            }
            $rawTag = $Matches[1]
            $norm = Get-NormalizedVersion $rawTag
            $normKey = $norm.ToString()
            if (-not $seen.Add($normKey)) {
                throw "Duplicate release entry for version '$rawTag' ($normKey) in changelog."
            }
            $current = @{
                HeaderTag         = $rawTag
                NormalizedVersion = $norm
                Body              = ''
            }
            $bodyLines = [System.Collections.Generic.List[string]]::new()
        }
        elseif ($null -ne $current) {
            $bodyLines.Add($line)
        }
    }
    if ($null -ne $current) {
        $current.Body = ($bodyLines -join "`n").Trim()
        $sections.Add($current)
    }
    return $sections
}

function Strip-MarkdownText([string]$Text) {
    $s = $Text
    # Replace markdown links [label](url) with label.
    $s = [regex]::Replace($s, '\[([^\]]+)\]\([^)]+\)', '$1')
    # Remove bare URLs.
    $s = [regex]::Replace($s, 'https?://\S+', '')
    # Remove inline markdown formatting punctuation.
    $s = [regex]::Replace($s, '[*_`~<>#]+', '')
    # Collapse whitespace.
    $s = [regex]::Replace($s, '\s+', ' ').Trim()
    return $s
}

function Assert-ValidFeatureNotes([string]$Label, [string]$Body) {
    if ([string]::IsNullOrWhiteSpace($Body)) {
        throw "Release notes for $Label are empty. Add a description of features to CHANGELOG.md before releasing."
    }

    $inCodeBlock = $false
    $validBullets = 0
    $totalDescriptiveChars = 0

    foreach ($rawLine in ($Body -split "`r?`n")) {
        $line = $rawLine.Trim()
        if ($line -match '^```') {
            $inCodeBlock = -not $inCodeBlock
            continue
        }
        if ($inCodeBlock) { continue }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '^<!--.*-->$') { continue }
        if ($line -match '^#{1,6}\s+') { continue }
        if ($line -match '^(-{3,}|\*{3,}|_{3,})$') { continue }
        if ($line -match '^\*?\*?Full Changelog\*?\*?:') { continue }

        if ($line -match '\b(TODO|TBD|WIP|FIXME)\b' -or $line -match '<describe\b') {
            throw "Release notes for $Label contain placeholder text ('$line'). Replace placeholders with real feature descriptions in CHANGELOG.md."
        }

        if ($line -match '^[-*+]\s+(.+)$') {
            $plain = Strip-MarkdownText $Matches[1]
            if ($plain.Length -ge 15) {
                $validBullets++
            }
            $totalDescriptiveChars += $plain.Length
        }
        else {
            $plain = Strip-MarkdownText $line
            $totalDescriptiveChars += $plain.Length
        }
    }

    if ($validBullets -lt 1) {
        throw "Release notes for $Label must include at least one descriptive feature bullet ('- ...', at least 15 characters) in CHANGELOG.md."
    }
    if ($totalDescriptiveChars -lt 30) {
        throw "Release notes for $Label are too brief ($totalDescriptiveChars chars). Add a clear description of the release's features in CHANGELOG.md."
    }
}

function Resolve-ReleaseNotes(
    [System.Collections.Generic.List[hashtable]]$Sections,
    [string]$TargetVersion,
    [string]$Repo
) {
    $normTarget = Get-NormalizedVersion $TargetVersion
    $index = -1
    for ($i = 0; $i -lt $Sections.Count; $i++) {
        if ($Sections[$i].NormalizedVersion -eq $normTarget) {
            $index = $i
            break
        }
    }
    if ($index -lt 0) {
        throw "No release notes section found for '$TargetVersion' in CHANGELOG.md. Add a '## $TargetVersion' section with feature descriptions before releasing."
    }

    $section = $Sections[$index]
    Assert-ValidFeatureNotes $TargetVersion $section.Body

    $notes = $section.Body
    if (-not [string]::IsNullOrWhiteSpace($Repo) -and $notes -notmatch '(?m)^\*\*Full Changelog\*\*:') {
        $displayTag = if ($TargetVersion.StartsWith('v') -or $TargetVersion.StartsWith('V')) { $TargetVersion } else { "v$TargetVersion" }
        if ($index + 1 -lt $Sections.Count) {
            $prevHeader = $Sections[$index + 1].HeaderTag
            $prevTag = if ($prevHeader.StartsWith('v') -or $prevHeader.StartsWith('V')) { $prevHeader } else { "v$prevHeader" }
            $notes = "$notes`n`n**Full Changelog**: https://github.com/$Repo/compare/$prevTag...$displayTag"
        }
        else {
            $notes = "$notes`n`n**Full Changelog**: https://github.com/$Repo/commits/$displayTag"
        }
    }
    return $notes
}

function Invoke-SelfTest {
    function Assert-Throws([string]$Name, [scriptblock]$Action) {
        $threw = $false
        try { & $Action } catch { $threw = $true }
        if (-not $threw) { throw "Self-test failed: expected failure for '$Name'." }
    }

    # Missing section
    $emptyChangelog = Parse-ChangelogSections "# Changelog`n`n## v1.0.0`n- Initial release with core hover dictionary support.`n"
    Assert-Throws 'missing version' { Resolve-ReleaseNotes $emptyChangelog 'v1.0.1' 'owner/repo' | Out-Null }

    # Empty section
    Assert-Throws 'empty section' { Assert-ValidFeatureNotes 'v2.0.9' '' }

    # Full Changelog link only (the v2.0.2 / v2.0.3 bug)
    Assert-Throws 'changelog link only' {
        Assert-ValidFeatureNotes 'v2.0.2' '**Full Changelog**: https://github.com/KonyAHS/HachidoriFfxiv/compare/v2.0.1...v2.0.2'
    }

    # One-line release title without feature bullets
    Assert-Throws 'title without feature bullets' {
        Assert-ValidFeatureNotes 'v2.0.3' 'Hachidori for FFXIV v2.0.3'
    }

    # Placeholder bullet
    Assert-Throws 'placeholder TODO' {
        Assert-ValidFeatureNotes 'v2.0.9' "- TODO: fill in release features before shipping this build"
    }

    # Too-short bullet
    Assert-Throws 'too-short bullet' {
        Assert-ValidFeatureNotes 'v2.0.9' "- Bug fixes"
    }

    # Duplicate version
    Assert-Throws 'duplicate version' {
        Parse-ChangelogSections "## v2.0`n- First feature description here.`n## v2.0.0.0`n- Duplicate version entry here.`n" | Out-Null
    }

    # Valid notes + version normalization + Full Changelog link generation
    $sample = @"
# Changelog

## v2.0
Major release with embedded popup support.

### Highlights
- **Embedded WebView2 popup:** Renders Hachidori's real browser popup directly inside FFXIV.

## v1.9
- **Initial hover engine:** Added native hover lookups for Japanese dialogue.
"@
    $parsed = Parse-ChangelogSections $sample
    $rendered = Resolve-ReleaseNotes $parsed '2.0.0.0' 'KonyAHS/HachidoriFfxiv'
    if ($rendered -notmatch 'Embedded WebView2 popup' -or $rendered -notmatch 'compare/v1\.9\.\.\.v2\.0\.0\.0') {
        throw 'Self-test failed: valid notes did not render expected feature text and compare link.'
    }

    Write-Host 'PASS: release-notes validator self-test'
}

if ($SelfTest) {
    Invoke-SelfTest
}

if (-not ($ValidateAll -or $ValidateProjectVersion -or -not [string]::IsNullOrWhiteSpace($Tag))) {
    if (-not $SelfTest) {
        throw 'Specify at least one of -Tag, -ValidateProjectVersion, -ValidateAll, or -SelfTest.'
    }
    return
}

if (-not (Test-Path -LiteralPath $ChangelogPath -PathType Leaf)) {
    throw "Changelog not found at '$ChangelogPath'. Create CHANGELOG.md with feature descriptions for every release."
}

$changelogContent = Get-Content -LiteralPath $ChangelogPath -Raw
$sections = Parse-ChangelogSections $changelogContent
if ($sections.Count -eq 0) {
    throw "No release sections ('## vX.Y.Z') found in '$ChangelogPath'."
}

if ($ValidateAll) {
    foreach ($section in $sections) {
        Assert-ValidFeatureNotes $section.HeaderTag $section.Body
    }
    Write-Host "PASS: all $($sections.Count) release entries in CHANGELOG.md have valid feature descriptions"
}

if ($ValidateProjectVersion) {
    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
        throw "Project file not found at '$ProjectPath'."
    }
    $csproj = Get-Content -LiteralPath $ProjectPath -Raw
    if ($csproj -notmatch '<Version>\s*([^<\s]+)\s*</Version>') {
        throw "Could not read <Version> from '$ProjectPath'."
    }
    $projectVersion = $Matches[1]
    $null = Resolve-ReleaseNotes $sections "v$projectVersion" $Repository
    Write-Host "PASS: CHANGELOG.md contains valid feature descriptions for project version $projectVersion"
}

if (-not [string]::IsNullOrWhiteSpace($Tag)) {
    $resolved = Resolve-ReleaseNotes $sections $Tag $Repository
    if (-not [string]::IsNullOrWhiteSpace($OutFile)) {
        $parentDir = Split-Path -Path $OutFile -Parent
        if (-not [string]::IsNullOrWhiteSpace($parentDir)) {
            New-Item -ItemType Directory -Force -Path $parentDir | Out-Null
        }
        [IO.File]::WriteAllText($OutFile, $resolved + "`n", [Text.UTF8Encoding]::new($false))
        Write-Host "Release notes written to $OutFile for $Tag"
    }
    else {
        Write-Output $resolved
    }
}
