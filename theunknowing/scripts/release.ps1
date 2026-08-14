<#
.SYNOPSIS
  Builds and packages a The Unknowing release: bumps modinfo.json's
  version, compiles TheUnknowingCode in Release mode, zips a clean
  distributable package into releases/, and drops a copy into the live
  Mods folder (clearing any stale unpack cache for it) for immediate
  testing.

.PARAMETER Version
  Set an exact version instead of auto-bumping (e.g. "0.3.0" or
  "1.0.0-rc.0"). Overrides everything else below.

.PARAMETER Major
  Bump the major version (0.2.3 -> 1.0.0), resetting minor/patch to 0.

.PARAMETER Minor
  Bump the minor version (0.2.3 -> 0.3.0), resetting patch to 0.

.PARAMETER Rc
  Tag as a release candidate, using Vintage Story's own recognized
  "-rc.N" version-tag convention (see GameVersion.SplitVersionString -
  it's specifically understood by the engine's own version comparison,
  ranked below a plain stable version). Combine with -Major/-Minor to
  start a new candidate series (e.g. -Major -Rc: 0.2.7 -> 1.0.0-rc.0);
  used alone it increments the existing -rc.N counter if the current
  version already has one (1.0.0-rc.0 -> 1.0.0-rc.1), or starts a fresh
  -rc.0 on a patch-bumped base otherwise.

.PARAMETER Pre
  Same as -Rc but tags "-pre.N" instead - Vintage Story's own convention
  for a preview/work-in-progress build, ranked below -rc.

.PARAMETER SkipDeploy
  Don't copy the zip into the live VintagestoryData\Mods folder.

.EXAMPLE
  .\scripts\release.ps1
  Patch-bumps the version, builds, and packages.

.EXAMPLE
  .\scripts\release.ps1 -Minor
  Minor-bumps the version (resets patch to 0).

.EXAMPLE
  .\scripts\release.ps1 -Major -Rc
  Starts a release-candidate series for the next major version: 0.2.7 -> 1.0.0-rc.0

.EXAMPLE
  .\scripts\release.ps1 -Rc
  Increments the current -rc.N counter (must already be on an -rc version).

.EXAMPLE
  .\scripts\release.ps1 -Minor -Pre
  Starts a preview series for the next feature: 1.0.0 -> 1.1.0-pre.0

.EXAMPLE
  .\scripts\release.ps1 -Version 1.0.0
  Sets an exact version, e.g. to drop a prerelease tag once it's final.
#>
param(
    [string]$Version,
    [switch]$Major,
    [switch]$Minor,
    [switch]$Rc,
    [switch]$Pre,
    [switch]$SkipDeploy
)

if ($Rc -and $Pre) { throw "Use only one of -Rc / -Pre" }

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$modInfoPath = Join-Path $repoRoot "modinfo.json"
$codeProject = Join-Path $repoRoot "TheUnknowingCode\TheUnknowingCode.csproj"
$releasesDir = Join-Path $repoRoot "releases"
$modsDir = Join-Path $env:APPDATA "VintagestoryData\Mods"

# --- Version bump ---
$modInfo = Get-Content $modInfoPath -Raw | ConvertFrom-Json
$currentVersion = $modInfo.version

if ($Version) {
    $newVersion = $Version
} else {
    # X.Y.Z with an optional -rc.N / -pre.N tag (Vintage Story's own
    # recognized prerelease convention - see GameVersion.SplitVersionString).
    if ($currentVersion -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-(rc|pre)\.(\d+))?$') {
        throw "Expected modinfo.json version to be X.Y.Z or X.Y.Z-rc.N/X.Y.Z-pre.N, got '$currentVersion'"
    }
    # NOTE: locals here deliberately avoid the names Major/Minor/Rc/Pre
    # (case-insensitively) - PowerShell variables aren't case-sensitive, so
    # e.g. a local $major would silently clobber the -Major switch param.
    [int]$majorNum = $Matches[1]
    [int]$minorNum = $Matches[2]
    [int]$patchNum = $Matches[3]
    $existingTag = $Matches[4]   # 'rc', 'pre', or $null
    $existingTagNum = $Matches[5]

    $requestedTag = if ($Rc) { "rc" } elseif ($Pre) { "pre" } else { $null }
    $bumpingCore = $Major -or $Minor

    if ($Major) {
        $majorNum++; $minorNum = 0; $patchNum = 0
    } elseif ($Minor) {
        $minorNum++; $patchNum = 0
    } elseif (-not $requestedTag) {
        $patchNum++
    } elseif ($existingTag -ne $requestedTag) {
        # Entering a new prerelease series without an explicit -Major/-Minor:
        # patch-bump the base, same as the plain default-bump behavior.
        $patchNum++
    }

    if ($requestedTag) {
        $tagNum = if (-not $bumpingCore -and $existingTag -eq $requestedTag) { [int]$existingTagNum + 1 } else { 0 }
        $newVersion = "$majorNum.$minorNum.$patchNum-$requestedTag.$tagNum"
    } else {
        $newVersion = "$majorNum.$minorNum.$patchNum"
    }
}

Write-Host "Version: $currentVersion -> $newVersion"

# Bumped via a targeted regex replace (not a JSON parse/stringify round-trip)
# so the rest of modinfo.json's formatting is left byte-for-byte untouched.
# Written to a temp .js file rather than `node -e "..."` - passing a script
# containing double quotes as a native-exe argument gets mangled by PowerShell.
$env:THEUNKNOWING_NEW_VERSION = $newVersion
$nodeScript = @'
const fs = require("fs");
const p = process.argv[2];
const newVersion = process.env.THEUNKNOWING_NEW_VERSION;
const s = fs.readFileSync(p, "utf8");
const out = s.replace(/("version"\s*:\s*")[^"]*(")/, (_, pre, post) => pre + newVersion + post);
if (out === s) throw new Error("version field not found/unchanged");
fs.writeFileSync(p, out);
'@
$tempScript = Join-Path $env:TEMP "theunknowing-bump-version.js"
Set-Content -Path $tempScript -Value $nodeScript -Encoding utf8
& node $tempScript $modInfoPath
if ($LASTEXITCODE -ne 0) { throw "Failed to update modinfo.json version" }
Remove-Item $tempScript
Remove-Item Env:\THEUNKNOWING_NEW_VERSION

# --- Build (Release) ---
Write-Host "Building TheUnknowingCode (Release)..."
& dotnet build $codeProject -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# --- Package ---
if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}

$zipName = "TheUnknowing-$newVersion.zip"
$zipPath = Join-Path $releasesDir $zipName

# Ship only what the game needs to load: modinfo.json, assets/ (once there is
# any), the compiled DLL, and the docs. Excludes TheUnknowingCode/ (source),
# NOTES.local.md (private scratchpad, gitignored), .gitignore, and releases/
# itself.
$excludeNames = @("TheUnknowingCode", "scripts", "releases", ".git", ".gitignore", "NOTES.local.md")
$items = Get-ChildItem -Path $repoRoot -Exclude $excludeNames

if (Test-Path $zipPath) { Remove-Item $zipPath }

# NOT Compress-Archive - it (and even .NET's own ZipFile.CreateFromDirectory) write zip entry
# paths using Windows' backslash separator. Windows tolerates that everywhere, including this
# script's own local-Mods-folder deploy below, but a Linux game server does not: unpacking the
# zip there produces literal files/folders named e.g. "assets\theunknowing\blocktypes" (backslash
# as part of the name) instead of properly nested directories, so the game's asset manager can
# never find anything under assets/theunknowing/ - items, blocks, worldgen, all silently missing,
# while the mod's C# still loads fine since that doesn't depend on the asset tree at all. This bit
# Caveshrooms for real (see its README) - building the zip by hand here, forcing forward slashes
# in every entry name, avoids repeating that.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($item in $items) {
        $files = if ($item.PSIsContainer) { Get-ChildItem -Path $item.FullName -Recurse -File } else { $item }
        foreach ($file in $files) {
            $relativePath = $file.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relativePath) | Out-Null
        }
    }
} finally {
    $zip.Dispose()
}
Write-Host "Packaged: $zipPath"

# --- Deploy for local testing ---
if (-not $SkipDeploy) {
    if (-not (Test-Path $modsDir)) {
        throw "Mods folder not found at $modsDir"
    }
    $deployPath = Join-Path $modsDir "TheUnknowing.zip"
    Copy-Item -Path $zipPath -Destination $deployPath -Force
    Write-Host "Deployed to: $deployPath"

    # The deploy target is always this same fixed filename (see param block above for why),
    # which means every local rebuild reuses the same Cache\unpack\TheUnknowing.zip_<hash> key.
    # Whether that hash is content-based or something weaker (e.g. name+size) isn't something
    # we've verified, so delete any existing unpack cache for it here rather than assume - see
    # Caveshrooms' own history for why that assumption is worth not making.
    $unpackCacheDir = Join-Path $env:APPDATA "VintagestoryData\Cache\unpack"
    if (Test-Path $unpackCacheDir) {
        Get-ChildItem -Path $unpackCacheDir -Directory -Filter "TheUnknowing.zip_*" | ForEach-Object {
            Remove-Item -Path $_.FullName -Recurse -Force
            Write-Host "Cleared stale unpack cache: $($_.FullName)"
        }
    }
}

Write-Host "Done. Released $newVersion."
