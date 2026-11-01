# Safe project check: restore, build (Release), isolated unit tests.
# Never runs engines, never touches system networking.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
Set-Location $env:BYDLO_ROOT

dotnet build BYDLO.slnx -c Release -warnaserror --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test tests/Bydlo.Tests/Bydlo.Tests.csproj -c Release --no-build --nologo -v quiet --results-directory .tmp/TestResults
exit $LASTEXITCODE
