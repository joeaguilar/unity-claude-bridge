<#
.SYNOPSIS
    Embed this package into a Unity project so it can actually be edited.

.DESCRIPTION
    A git-installed package resolves into Library/PackageCache, which Unity marks
    immutable: edits there are wiped on the next resolve, and the importer will
    not generate .meta files for it. So development means temporarily embedding
    the package under <project>/Packages, where it is mutable and Unity does
    generate metas.

    This copies the repo working tree into the target project and drops the git
    dependency from its manifest so the embedded copy is the one that loads.

    Run dev\publish.ps1 when done to copy changes back and restore the pin.

.EXAMPLE
    .\dev\embed.ps1 -Project C:\Users\Blue\ChoomDoom
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Project
)

$ErrorActionPreference = 'Stop'

# PowerShell 5.1's `-Encoding utf8` means UTF-8 WITH a BOM. Unity's JSON parser
# rejects a BOM outright ("Non-whitespace before {[", char 65279) and refuses to
# resolve any package, so manifest.json and packages-lock.json must be written
# without one. Set-Content and Out-File cannot do that on 5.1; this can.
function Write-Utf8NoBom {
    param([string] $Path, [string] $Text)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

$repo = Split-Path -Parent $PSScriptRoot
$pkgName = 'com.blue.claude-bridge'
$manifest = Join-Path $Project 'Packages\manifest.json'
$target = Join-Path $Project "Packages\$pkgName"

if (-not (Test-Path (Join-Path $Project 'Assets')))  { throw "Not a Unity project (no Assets/): $Project" }
if (-not (Test-Path $manifest))                      { throw "No Packages/manifest.json in $Project" }

# Replace any previous embedded copy outright, so a deleted file in the repo
# does not linger in the project.
if (Test-Path $target) {
    Write-Host "Removing previous embedded copy..."
    Remove-Item $target -Recurse -Force
}

Write-Host "Embedding $pkgName into $Project ..."
robocopy $repo $target /E /XD '.git' 'dev' /XF '.itr.db' /NFL /NDL /NJH /NJS | Out-Null
# robocopy uses exit codes as a bitfield: 0 nothing copied, 1 files copied,
# 2 extras, 8+ genuine failure. Left alone, a successful copy leaves
# $LASTEXITCODE at 1 and the script reads as failed to any caller.
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE" }
$global:LASTEXITCODE = 0

# Drop the git dependency; an embedded package with the same name would otherwise
# collide with the resolved one.
$raw = Get-Content $manifest -Raw
$pattern = '(?m)^\s*"' + [regex]::Escape($pkgName) + '"\s*:\s*"[^"]*"\s*,?\r?\n'
if ($raw -match $pattern) {
    $version = ([regex]::Match($raw, '"' + [regex]::Escape($pkgName) + '"\s*:\s*"([^"]*)"')).Groups[1].Value
    $raw = [regex]::Replace($raw, $pattern, '')
    Write-Utf8NoBom -Path $manifest -Text $raw
    Write-Host "Removed git dependency from manifest (was: $version)"
} else {
    Write-Host "No git dependency in manifest; nothing to remove."
}

# Removing it from manifest.json is not enough. packages-lock.json independently
# pins the resolved git package, so UPM keeps loading the PackageCache copy and
# the embedded folder becomes a same-name duplicate -- which Package Manager
# reports as "invalid" while the editor quietly keeps running the cached
# assembly. The lock file is generated, so rewriting it is safe.
$lockPath = Join-Path $Project 'Packages\packages-lock.json'
if (Test-Path $lockPath) {
    $lock = Get-Content $lockPath -Raw | ConvertFrom-Json
    if ($lock.dependencies.PSObject.Properties.Name -contains $pkgName) {
        $lock.dependencies.PSObject.Properties.Remove($pkgName)
        Write-Utf8NoBom -Path $lockPath -Text ($lock | ConvertTo-Json -Depth 30)
        Write-Host "Removed stale pin from packages-lock.json"
    }
}

Write-Host ''
Write-Host "Embedded." -ForegroundColor Green
Write-Host ''
Write-Host "RESTART the Unity editor before doing anything else." -ForegroundColor Yellow
Write-Host "Focusing the window is not enough: Unity does not reliably re-resolve" -ForegroundColor Yellow
Write-Host "packages when manifest.json changes underneath a running editor, and it" -ForegroundColor Yellow
Write-Host "will keep loading the cached copy while the embedded one sits unused." -ForegroundColor Yellow
Write-Host ''
Write-Host "Edit under:  $target\Editor"
Write-Host "Compile via: .\tools\unity.ps1 sync   (from $Project)"
Write-Host "When done:   .\dev\publish.ps1 -Project `"$Project`" -Version <x.y.z>"

exit 0
