<#
.SYNOPSIS
    Publishes this plugin and packages it as a host-installable zip.

.DESCRIPTION
    Steps:
      1. (If -Version supplied) overwrite plugin.json's version field
         so the manifest, the assembly metadata, and the zip filename
         all reflect the release tag.
      2. dotnet publish src/*.csproj  → staging folder under dist/_staging/
         When -Version is supplied, MSBuild also bakes it into the
         assembly via -p:Version= so the plugin's runtime
         IPlugin.Version (read through PluginVersion.OfAssembly) matches
         what the manifest says.
      3. Read plugin.json from the publish output (manifest is the source
         of truth for the zip name and wrapper-folder layout).
      4. Strip .pdb files (debug symbols — never needed at runtime).
      5. Rename the staging folder to `<safe-publisher>__<safe-id>` so
         the extracted folder name matches what Game Master Sound Board's
         installer would produce.
      6. Compress to dist/<publisher>-<id>-<version>.zip.

    The same shape every Game Master Sound Board plugin ships in. Drop
    the resulting zip on the host's Plugin Manager and it installs.

    The shared logic lives here, copied verbatim across every sibling
    plugin repo. Keeping it in-repo (rather than referencing a script in
    the main app) means a sibling repo is fully self-contained — anyone
    forking it can build + package without needing the host source.

.PARAMETER Configuration
    MSBuild configuration to publish. Defaults to Release.

.PARAMETER OutDir
    Destination directory for the zip. Defaults to dist/ at the repo root.

.PARAMETER Version
    SemVer version to bake into both the manifest and the assembly.
    Optional. When omitted, the version in plugin.json + the csproj's
    <Version> default are used (typical for local developer builds).

    Release CI passes the version derived from the tag (e.g. tag
    `v1.2.3` → `-Version 1.2.3`), so a release artifact always matches
    its tag regardless of what plugin.json happens to say in source.
#>

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutDir,
    [string] $Version
)

$ErrorActionPreference = 'Stop'

# ── Filesystem-safe folder-name segment.
#    Mirrors PluginManifestFile.GetSafeFolderName in the host:
#    invalid file-name chars, spaces, and periods collapse to '_'. The
#    publisher↔id boundary is `__` (double underscore). Kept in sync
#    intentionally so the zip's wrapper folder matches what the host
#    creates on install.
function Get-SafePluginFolderSegment {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Segment)
    if ([string]::IsNullOrEmpty($Segment)) { return 'plugin' }
    $invalid = [System.IO.Path]::GetInvalidFileNameChars()
    $chars = $Segment.ToCharArray()
    for ($i = 0; $i -lt $chars.Length; $i++) {
        $c = $chars[$i]
        if ($invalid -contains $c -or $c -eq ' ' -or $c -eq '.') {
            $chars[$i] = '_'
        }
    }
    $name = (-join $chars).Trim('_')
    if ([string]::IsNullOrEmpty($name)) { return 'plugin' }
    return $name
}

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'dist' }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

# Discover the plugin csproj. Convention: src/<PluginName>.csproj.
$csproj = Get-ChildItem -Path (Join-Path $RepoRoot 'src') -Filter '*.csproj' |
    Sort-Object FullName | Select-Object -First 1
if (-not $csproj) { throw "No csproj found under $RepoRoot/src/" }

# Source plugin.json (NOT the publish-output copy — that doesn't exist
# yet). When -Version is supplied we rewrite this file in place so the
# manifest matches the assembly version that MSBuild is about to bake
# in. CI runs on a fresh checkout so the rewrite is never committed
# back; local devs who pass -Version see a real edit to their source
# tree.
$sourceManifestPath = Join-Path $csproj.Directory.FullName 'plugin.json'
if (-not (Test-Path $sourceManifestPath)) {
    throw "Missing plugin.json next to $($csproj.Name). The csproj should have " +
          "<None Update='plugin.json'><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>."
}

if ($Version) {
    Write-Host "==> Stamping version $Version into plugin.json" -ForegroundColor Cyan
    $rawManifest = Get-Content $sourceManifestPath -Raw
    # Replace the version field only. Keep the rest of the JSON
    # byte-for-byte so we don't reorder fields or churn formatting.
    $rawManifest = [regex]::Replace(
        $rawManifest,
        '("version"\s*:\s*")([^"]*)(")',
        { param($m) $m.Groups[1].Value + $Version + $m.Groups[3].Value },
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    Set-Content -Path $sourceManifestPath -Value $rawManifest -NoNewline
}

Write-Host "==> Packaging $($csproj.BaseName) -> $OutDir" -ForegroundColor Cyan

# Clean output dir.
if (Test-Path $OutDir) { Remove-Item -Path $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$staging = Join-Path $OutDir '_staging/build'

# Publish. Pass -p:Version= when a version override was supplied; that
# bakes it into AssemblyVersion + AssemblyInformationalVersion, which
# PluginVersion.OfAssembly reads at runtime. Without -p:Version, MSBuild
# uses the csproj's <Version> property as the default.
$publishArgs = @(
    'publish',
    $csproj.FullName,
    '-c', $Configuration,
    '-o', $staging,
    '--nologo',
    '-v', 'quiet'
)
if ($Version) {
    $publishArgs += "-p:Version=$Version"
}

Write-Host "   Publishing $Configuration..." -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# Read manifest from the publish output (which is the post-stamp copy).
$manifestPath = Join-Path $staging 'plugin.json'
if (-not (Test-Path $manifestPath)) {
    throw "Missing plugin.json in publish output. Check that the csproj has " +
          "<None Update='plugin.json'><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>."
}
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
foreach ($field in 'publisher','id','entryDll') {
    if (-not $manifest.$field) { throw "plugin.json is missing required field '$field'." }
}
$pluginPublisher = $manifest.publisher
$pluginId        = $manifest.id
$pluginVersion   = if ($manifest.version) { $manifest.version } else { '0.0.0' }

# Strip debug symbols.
Get-ChildItem -Path $staging -Filter '*.pdb' -Recurse | Remove-Item -Force

# Rename to safe folder name (publisher__id), matching what the host's
# installer would produce on disk. NOTE: the wrapper folder name does
# NOT include the version — versioning is a release-artifact concern
# (encoded in the zip filename), not an on-disk-layout concern. Every
# version of the same (publisher, id) lives in the same folder once
# installed.
$safeFolderName = "{0}__{1}" -f (Get-SafePluginFolderSegment $pluginPublisher),
                                (Get-SafePluginFolderSegment $pluginId)
$finalFolder = Join-Path (Split-Path $staging) $safeFolderName
if (Test-Path $finalFolder) { Remove-Item -Path $finalFolder -Recurse -Force }
Rename-Item -Path $staging -NewName $safeFolderName -Force

# Compress. The zip filename uses the full lineage triple so downloads
# from multiple publishers don't collide and version-pinned files are
# obvious from the name alone:
#
#     <publisher>-<id>-<version>.zip
#
# Compress-Archive includes the source folder as the wrapper inside the
# zip — exactly the shape the installer expects.
$zipName = "{0}-{1}-{2}.zip" -f $pluginPublisher, $pluginId, $pluginVersion
$zipPath = Join-Path $OutDir $zipName
if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force }
Write-Host "   Compressing $zipName" -ForegroundColor DarkGray
Compress-Archive -Path $finalFolder -DestinationPath $zipPath -CompressionLevel Optimal

# Sanity-check: open the produced zip and confirm plugin.json lives at
# the expected `<safeFolder>/plugin.json` path. Catches Compress-Archive
# shape surprises before users hit them at install time.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$verify = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $expected = "$safeFolderName/plugin.json"
    if (-not ($verify.Entries | Where-Object { $_.FullName -eq $expected })) {
        throw "Sanity check failed: $zipName does not contain $expected."
    }
} finally {
    $verify.Dispose()
}

# Cleanup staging.
Remove-Item -Path (Join-Path $OutDir '_staging') -Recurse -Force

$sizeKb = [int]((Get-Item $zipPath).Length / 1KB)
Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
Write-Host "    Plugin: $($manifest.name) ($pluginPublisher/$pluginId) v$pluginVersion"
Write-Host "    Zip:    $zipPath ($sizeKb KB)"
Write-Host "    Drop this zip on Settings -> Plugin Manager to install." -ForegroundColor DarkGray
