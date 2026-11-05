# Extracts the release-notes section for one tag from docs/RELEASE_NOTES.md.
# Usage: extract-release-notes.ps1 -Tag v0.4.0 [-Input docs/RELEASE_NOTES.md] [-Output .tmp/release-notes.md]
# Fails when the section is missing or duplicated; history stays in docs.
param(
  [Parameter(Mandatory = $true)][string]$Tag,
  [string]$Input = "docs/RELEASE_NOTES.md",
  [string]$Output = ".tmp/release-notes.md"
)
$ErrorActionPreference = "Stop"
$version = $Tag.TrimStart("v")
$header = "# BYDLO v$version"
$lines = Get-Content -Path $Input
$matches = @($lines | Where-Object { $_ -ceq $header })
if ($matches.Count -eq 0) {
  Write-Error "Release notes section not found for tag '$Tag': expected header '$header' in $Input"
  exit 1
}
if ($matches.Count -gt 1) {
  Write-Error "Duplicate release notes headers for tag '$Tag' in $Input"
  exit 1
}
$printing = $false
$body = @()
foreach ($line in $lines) {
  if ($line -ceq $header) { $printing = $true; continue }
  if ($printing -and $line.StartsWith("# BYDLO ", [System.StringComparison]::Ordinal)) { break }
  if ($printing) { $body += $line }
}
if ($body.Count -eq 0 -or ($body -join "").Trim().Length -eq 0) {
  Write-Error "Release notes section for tag '$Tag' is empty in $Input"
  exit 1
}
$dir = Split-Path -Parent $Output
if ($dir -ne "") { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$body -join "`n" | Set-Content -Path $Output -Encoding utf8 -NoNewline
Write-Output "Wrote $Output for $Tag."
