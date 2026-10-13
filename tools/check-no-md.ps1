# Fails (exit 1) when a Markdown file is tracked by git, or is about to be: this project publishes README.rst / .txt only.
# Also fails on files that must never be in this repository (mod files / unit art outside the allowed screenshots and icon).
# Usage: powershell -NoProfile -File tools\check-no-md.ps1          (also run by .githooks\pre-push)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$bad = @()

if (Test-Path (Join-Path $root ".git")) {
    $tracked = @(git ls-files)
    $staged = @(git diff --cached --name-only)
    $files = @($tracked + $staged | Sort-Object -Unique)
} else {
    $files = @(Get-ChildItem -Recurse -File -Force | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.git)[\\/]' } | ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') })
}

foreach ($f in $files) {
    if ($f -match '\.(md|markdown|mdx)$') { $bad += "markdown file: $f" }
    if ($f -match '(^|/)baseline_V' -or $f -match '(^|/)catalog/.*\.csv$') { $bad += "mod-derived file: $f" }
    if ($f -match '\.(ogg|wav|jar|dll|exe|zip)$' -and $f -notmatch '^tests/') { $bad += "binary that does not belong in the repository: $f" }
    if ($f -match '\.png$' -and $f -notmatch '^docs/' -and $f -notmatch '^assets/') { $bad += "picture outside docs/ and assets/: $f" }
}
if ($bad.Count -gt 0) {
    Write-Host "CHECK FAILED:" -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "check-no-md: OK ($($files.Count) files, no Markdown, no mod files)"
exit 0
