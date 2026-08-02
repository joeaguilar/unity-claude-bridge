<#
.SYNOPSIS
    Copy an embedded package back into the repo and restore the project's git pin.

.DESCRIPTION
    The inverse of dev\embed.ps1. Copies the embedded package -- including the
    .meta files Unity generated for any new source file -- back into the repo,
    gates on meta completeness, removes the embedded copy, and restores the git
    dependency in the project's manifest.

    Deliberately does NOT commit, tag or push. Review the diff, then:

        git add -A
        git commit -m "..."
        git tag -a vX.Y.Z -m "..."
        git push origin main --tags

    The project's manifest is pinned to the version in package.json, so the tag
    must exist on the remote before the project can resolve it again.

.EXAMPLE
    .\dev\publish.ps1 -Project C:\Users\Blue\ChoomDoom
    .\dev\publish.ps1 -Project C:\Users\Blue\ChoomDoom -Version 0.3.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Project,

    # Bump package.json to this before copying back. Omit to keep the current version.
    [string] $Version,

    # Skip restoring the git dependency; leaves the project without the package.
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'

# PowerShell 5.1's `-Encoding utf8` means UTF-8 WITH a BOM, and Unity's JSON
# parser rejects a BOM outright ("Non-whitespace before {[", char 65279), then
# refuses to resolve any package at all. See the same note in embed.ps1.
function Write-Utf8NoBom {
    param([string] $Path, [string] $Text)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

$repo = Split-Path -Parent $PSScriptRoot
$pkgName = 'com.blue.claude-bridge'
$gitUrl = 'https://github.com/joeaguilar/unity-claude-bridge.git'
$manifest = Join-Path $Project 'Packages\manifest.json'
$embedded = Join-Path $Project "Packages\$pkgName"

if (-not (Test-Path $embedded)) { throw "No embedded package at $embedded. Did you run dev\embed.ps1?" }
if (-not (Test-Path $manifest)) { throw "No Packages/manifest.json in $Project" }

if ($Version) {
    $pkgJson = Join-Path $embedded 'package.json'
    $raw = Get-Content $pkgJson -Raw
    $raw = [regex]::Replace($raw, '"version"\s*:\s*"[^"]*"', '"version": "' + $Version + '"')
    Write-Utf8NoBom -Path $pkgJson -Text $raw
    Write-Host "Set version to $Version"
}

# Gate on meta completeness BEFORE copying back. A package missing metas resolves
# from git and then silently compiles nothing.
Write-Host "Checking .meta completeness..."
& (Join-Path $PSScriptRoot 'check-metas.ps1') -Path $embedded
if ($LASTEXITCODE -ne 0) {
    throw "Refusing to publish: missing .meta files. Focus the Unity editor to let it generate them, then retry."
}

Write-Host "Copying back to $repo ..."
robocopy $embedded $repo /E /XD 'dev' /NFL /NDL /NJH /NJS | Out-Null
# See the note in embed.ps1: robocopy's exit code is a bitfield and 1 means
# success-with-copies, not failure.
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE" }
$global:LASTEXITCODE = 0

$version = ([regex]::Match((Get-Content (Join-Path $repo 'package.json') -Raw), '"version"\s*:\s*"([^"]*)"')).Groups[1].Value

Write-Host "Removing embedded copy..."
Remove-Item $embedded -Recurse -Force
Remove-Item "$embedded.meta" -Force -ErrorAction SilentlyContinue

if (-not $NoRestore) {
    $raw = Get-Content $manifest -Raw
    $pattern = '(?m)^\s*"' + [regex]::Escape($pkgName) + '"\s*:\s*"[^"]*"\s*,?\r?\n'
    $raw = [regex]::Replace($raw, $pattern, '')

    $dep = '    "' + $pkgName + '": "' + $gitUrl + '#v' + $version + '",' + "`n"
    $raw = [regex]::Replace($raw, '(?m)("dependencies"\s*:\s*\{\r?\n)', ('$1' + $dep), 1)

    Write-Utf8NoBom -Path $manifest -Text $raw
    Write-Host "Restored manifest pin at v$version"
}

Write-Host ''
Write-Host "Published to repo at v$version." -ForegroundColor Green
Write-Host "Review, then commit and tag:" -ForegroundColor Yellow
Write-Host "  cd `"$repo`""
Write-Host "  git add -A; git commit -m `"...`""
Write-Host "  git tag -a v$version -m `"...`"; git push origin main --tags"
Write-Host ''
Write-Host "The project pin needs v$version on the remote before it will resolve." -ForegroundColor Yellow

exit 0
