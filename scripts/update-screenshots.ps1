# Render the Russian mock UI and publish images under content-addressed names.
[CmdletBinding()]
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$previousDataDirectory = $env:BYDLO_DATA_DIR
. (Join-Path $PSScriptRoot 'env.ps1')
$repoRoot = $env:BYDLO_ROOT
$previousLocation = Get-Location
$renderDirectory = Join-Path $repoRoot ('.tmp/screenshots-' + [Guid]::NewGuid().ToString('N'))
$imagesDirectory = Join-Path $repoRoot 'docs/images'
$readmePath = Join-Path $repoRoot 'README.md'

try {
  Set-Location -LiteralPath $repoRoot
  $env:BYDLO_DATA_DIR = Join-Path $renderDirectory 'appdata'
  $dotnetArguments = @('run', '--project', 'tools/Bydlo.Screenshots', '-c', 'Release')
  if ($NoBuild) { $dotnetArguments += '--no-build' }
  $dotnetArguments += @('--', $renderDirectory, 'ru')
  & dotnet @dotnetArguments
  if ($LASTEXITCODE -ne 0) { throw 'Screenshot rendering failed; documentation was not changed.' }

  $readme = [IO.File]::ReadAllText($readmePath)
  $obsoleteImages = @()
  $snapshots = @(Get-ChildItem -LiteralPath $renderDirectory -Filter '*.png' -File)
  if ($snapshots.Count -eq 0) { throw 'The renderer produced no images.' }

  foreach ($snapshot in $snapshots) {
    $digest = (Get-FileHash -LiteralPath $snapshot.FullName -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0, 12)
    $fileName = $snapshot.BaseName + '-' + $digest + '.png'
    $pattern = [Regex]::Escape($snapshot.BaseName) + '(?:-[a-f0-9]{12})?\.png'
    $obsoleteImages += Get-ChildItem -LiteralPath $imagesDirectory -File |
      Where-Object { $_.Name -match ('^' + $pattern + '$') -and $_.Name -ne $fileName }
    Copy-Item -LiteralPath $snapshot.FullName -Destination (Join-Path $imagesDirectory $fileName) -Force
    $readme = [Regex]::Replace($readme, 'docs/images/' + $pattern, 'docs/images/' + $fileName)
  }

  foreach ($reference in [Regex]::Matches($readme, 'docs/images/[^"\s)]+\.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $reference.Value) -PathType Leaf)) {
      throw ('Missing README image: ' + $reference.Value)
    }
  }
  [IO.File]::WriteAllText($readmePath, $readme, [Text.UTF8Encoding]::new($false))
  foreach ($obsoleteImage in $obsoleteImages) {
    Remove-Item -LiteralPath $obsoleteImage.FullName
  }
  Write-Output ('Updated {0} screenshots and all README image references.' -f $snapshots.Count)
}
finally {
  $env:BYDLO_DATA_DIR = $previousDataDirectory
  Set-Location -LiteralPath $previousLocation.Path
}
