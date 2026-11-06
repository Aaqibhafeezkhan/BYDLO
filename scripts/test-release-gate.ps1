# Evaluate the actual workflow expressions with synthetic job results only.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$ci = [IO.File]::ReadAllText((Join-Path $env:BYDLO_ROOT '.github/workflows/ci.yml'))
function Block([string]$job) {
    $match = [regex]::Match($ci, "(?ms)^  ${job}:\r?\n.*?(?=^  [\w-]+:|\z)")
    if (!$match.Success) { throw "Missing job $job" }
    $match.Value
}
$package = Block 'package'
$release = Block 'release'
$required = @('desktop','windows-parity','macos','native-packages','native-package-smoke','unix-activation','amnezia-macos','nix','signing-policy')
$needs = [regex]::Match($package, '(?m)^    needs: \[([^\]]+)\]').Groups[1].Value.Split(',').Trim()
foreach ($job in @($required) + 'sign-windows') {
    if ($job -notin $needs) { throw "Missing package dependency: $job" }
}
# Preserve the build/test chains and macOS suite, not just artifact availability.
if ((Block 'desktop') -notmatch 'needs: \[test, helper\]' -or
    (Block 'macos') -notmatch 'dotnet test tests/Bydlo.Tests/Bydlo.Tests.csproj' -or
    (Block 'unix-activation') -notmatch 'FullyQualifiedName~UnixActivationBrokerTests' -or
    $release -notmatch 'needs: package') { throw 'Required test chain disconnected' }
function Evaluate([string]$block, [hashtable]$results, [bool]$unsigned = $false,
    [string]$event = 'push', [string]$ref = 'refs/tags/v9.0.0',
    [string]$server = 'https://github.com', [bool]$cancelled = $false) {
    $condition = [regex]::Match($block, '(?ms)^    if: >-\r?\n(.*?)(?=^    \w)').Groups[1].Value.Trim()
    if (!$condition) { throw 'Missing explicit condition' }
    $condition = $condition.Replace('!cancelled()', '(-not $cancelled)').Replace('&&', '-and').Replace('||', '-or')
    $condition = ($condition -replace '\s+', ' ').Replace('==', '-ceq').Replace('!=', '-cne')
    $condition = $condition.Replace("startsWith(github.ref, 'refs/tags/v')", '$ref.StartsWith(''refs/tags/v'')')
    $condition = $condition.Replace('github.server_url', '$server').Replace('github.event_name', '$event')
    $condition = $condition.Replace('needs.signing-policy.outputs.unsigned', "'$($unsigned.ToString().ToLowerInvariant())'")
    $condition = [regex]::Replace($condition, 'needs\.([\w-]+)\.result', {
        param($m)
        if (!$results.ContainsKey($m.Groups[1].Value)) { throw "Unknown need $m" }
        "'$($results[$m.Groups[1].Value])'"
    })
    [bool](& ([scriptblock]::Create('(' + $condition + ')')))
}
$script:checks = 0
function Require([bool]$value) {
    if (!$value) { throw 'Release gate regression failed' }
    $script:checks++
}
$results = @{}
foreach ($job in $needs) { $results[$job] = 'success' }
Require (Evaluate $package $results)
foreach ($job in $needs) {
    foreach ($status in @('failure','cancelled','skipped')) {
        $results[$job] = $status
        Require (!(Evaluate $package $results))
        $packageResult = if (Evaluate $package $results) { 'success' } else { 'skipped' }
        Require (!(Evaluate $release @{ package = $packageResult }))
    }
    $results[$job] = 'success'
}
$results['sign-windows'] = 'skipped'
Require (Evaluate $package $results -unsigned $true)
Require (Evaluate $release @{package = 'success'}) # Issue 336: permitted transitive skip.
foreach ($job in $required) {
    foreach ($status in @('failure','cancelled','skipped')) {
        $results[$job] = $status
        Require (!(Evaluate $package $results -unsigned $true))
    }
    $results[$job] = 'success'
}
Require (Evaluate $package $results -unsigned $true -event workflow_dispatch -ref refs/heads/developer)
Require (!(Evaluate $release @{package = 'success'} -event workflow_dispatch -ref refs/heads/developer))
Require (!(Evaluate $package $results -unsigned $true -event pull_request -ref refs/pull/1/merge))
Require (!(Evaluate $package $results -unsigned $true -server https://gitea.example))
Require (!(Evaluate $release @{package = 'success'} -server https://gitea.example))
Require (!(Evaluate $package $results -unsigned $true -cancelled $true))
Require (!(Evaluate $release @{package = 'success'} -cancelled $true))
foreach ($status in @('failure','cancelled','skipped')) {
    Require (!(Evaluate $release @{package = $status}))
}
Write-Host "Release gate regression passed ($script:checks checks)."
