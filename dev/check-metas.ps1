<#
.SYNOPSIS
    Fail if any importable file in the package is missing its .meta sibling.

.DESCRIPTION
    A UPM package installed from a git URL lands in Library/PackageCache, which
    Unity treats as immutable and never generates .meta files for. Without them
    the package resolves cleanly, reports success, and silently compiles nothing
    -- the only trace is a line in Editor.log. This already shipped once as the
    broken v0.1.0 release.

    Tilde-suffixed folders (Tools~, Samples~, Skill~) are ignored by Unity's
    importer and correctly have no metas. Dotfiles are likewise not imported.

.EXAMPLE
    .\dev\check-metas.ps1
    .\dev\check-metas.ps1 -Path C:\path\to\package
#>
[CmdletBinding()]
param(
    [string] $Path = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) { throw "No such path: $Path" }

$missing = @()

Get-ChildItem -Path $Path -Recurse -Force | Where-Object {
    $rel = $_.FullName.Substring($Path.Length).TrimStart('\', '/')

    # Skip anything Unity would not import.
    if ($rel -like '.git*')       { return $false }
    if ($rel -like '*~\*')        { return $false }   # inside a tilde folder
    if ($rel -like '*~/*')        { return $false }
    if ($_.Name -like '*~')       { return $false }   # the tilde folder itself
    if ($_.Name -like '.*')       { return $false }   # dotfiles
    if ($_.Name -like '*.meta')   { return $false }
    if ($_.Name -eq 'dev')        { return $false }   # tooling, not shipped content
    if ($rel -like 'dev\*')       { return $false }
    if ($rel -like 'dev/*')       { return $false }
    if ($_.Name -eq '.itr.db')    { return $false }

    return $true
} | ForEach-Object {
    if (-not (Test-Path "$($_.FullName).meta")) {
        $missing += $_.FullName.Substring($Path.Length).TrimStart('\', '/')
    }
}

if ($missing.Count -gt 0) {
    Write-Host "Missing .meta files ($($missing.Count)):" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'A git-installed package missing these will resolve and then compile nothing.' -ForegroundColor Red
    Write-Host 'Embed the package in a Unity project, let the editor generate them, and copy them back.' -ForegroundColor Red
    exit 1
}

Write-Host "All importable files have .meta siblings." -ForegroundColor Green
exit 0
