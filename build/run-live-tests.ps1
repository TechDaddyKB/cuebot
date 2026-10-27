# Runs every opt-in suite that talks to real services (Twitch, streaming platforms, OBS, Python) using the
# settings in ~/.cuebot/live.env (or -EnvFile), and writes reports to artifacts/live-tests.
#   ./build/run-live-tests.ps1
#   ./build/run-live-tests.ps1 -Filter Twitch        # only tests whose name contains "Twitch"
param(
    [string]$Filter = "",
    [string]$EnvFile = (Join-Path $HOME ".cuebot/live.env")
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root "artifacts/live-tests"
New-Item -ItemType Directory -Force $results | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

# Live tests only run when explicitly requested; a plain `dotnet test` never goes live.
$env:CUEBOT_LIVE_TESTS = "1"

# Load KEY=VALUE pairs into this process only; values are never printed.
if (Test-Path $EnvFile) {
    $env:CUEBOT_LIVE_ENV_FILE = $EnvFile
    foreach ($line in Get-Content $EnvFile) {
        $line = $line.Trim()
        if ($line -eq "" -or $line.StartsWith("#")) { continue }
        if ($line.StartsWith("export ")) { $line = $line.Substring(7).TrimStart() }
        $eq = $line.IndexOf("=")
        if ($eq -le 0) { continue }
        $key = $line.Substring(0, $eq).Trim()
        $value = $line.Substring($eq + 1).Trim().Trim('"').Trim("'")
        # Variables already set in the environment win over the file.
        if ($value -ne "" -and -not [Environment]::GetEnvironmentVariable($key, "Process")) {
            [Environment]::SetEnvironmentVariable($key, $value, "Process")
        }
    }
    Write-Host "Loaded live-test settings from $EnvFile"
} else {
    Write-Host "No $EnvFile; only settings already in the environment are used (see docs/live.env.example)."
}

$suites = @(
    @{ Project = "tests/Cuebot.LiveTests"; Filter = "" },
    @{ Project = "tests/Cuebot.Obs.Tests"; Filter = "FullyQualifiedName~LiveObsTests" },
    @{ Project = "tests/Cuebot.Scripting.Tests"; Filter = "FullyQualifiedName~Python" }
)
$failed = 0
# Windows PowerShell 5.1 turns a native command's stderr lines into errors; with "Stop" the first one would end the run.
# Failures are counted from exit codes instead.
$ErrorActionPreference = "Continue"
foreach ($suite in $suites) {
    $name = Split-Path -Leaf $suite.Project
    $filters = @($suite.Filter, $(if ($Filter) { "FullyQualifiedName~$Filter" })) | Where-Object { $_ }
    $arguments = @("test", (Join-Path $root $suite.Project), "--results-directory", $results,
                   "--logger", "trx;LogFileName=$name-$stamp.trx", "--logger", "console;verbosity=normal")
    if ($filters) { $arguments += @("--filter", ($filters -join "&")) }
    & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $results "$name-$stamp.log")
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
Write-Host "Reports: $results"
exit $failed
