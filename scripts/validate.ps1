param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/validation',
    [switch]$Graphics,
    [switch]$Benchmarks
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'BlockCircuit.csproj'
$out = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
New-Item -ItemType Directory -Force -Path $out | Out-Null
$buildOutput = [IO.Path]::GetFullPath((Join-Path $repo "artifacts/build/$Configuration")) + [IO.Path]::DirectorySeparatorChar
$buildProperty = "-p:OutputPath=$buildOutput"
& dotnet build $project -c $Configuration $buildProperty
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$target = & dotnet msbuild $project -nologo "-p:Configuration=$Configuration" $buildProperty -getProperty:TargetPath
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve assembly path' }
$assembly = ($target | Out-String).Trim()
if (!(Test-Path -LiteralPath $assembly)) { throw "Missing assembly: $assembly" }
$suites = @('self-test', 'engine-tests', 'gameplay-tests', 'terrain-tests', 'controls-tests', 'streaming-tests', 'inventory-tests', 'multiplayer-tests')
if ($Benchmarks) { $suites += @('benchmark', 'circuit-benchmark', 'physics-benchmark', 'storage-benchmark', 'streaming-benchmark', 'falling-benchmark', 'inventory-benchmark', 'multiplayer-benchmark', 'multiplayer-session-benchmark') }
if ($Graphics) { $suites += @('smoke-test', 'palette-smoke-test', 'showcase-smoke-test', 'terrain-smoke-test', 'engine-smoke-test', 'gameplay-smoke-test', 'controls-smoke-test', 'streaming-smoke-test', 'inventory-smoke-test', 'multiplayer-smoke-test') }
Push-Location $out
try {
    foreach ($suite in $suites) {
        $log = & dotnet $assembly "--$suite" 2>&1
        $code = $LASTEXITCODE
        $log | Set-Content -Encoding utf8 -LiteralPath "$Configuration-$suite.log"
        $log | Select-Object -Last 3
        if ($code -ne 0) { throw "$suite failed ($code); see $out" }
    }
} finally { Pop-Location }
