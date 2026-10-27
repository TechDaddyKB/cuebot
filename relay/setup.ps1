# Deploys the Cuebot webhook relay to your own (free) Cloudflare account.
# Usage: powershell -ExecutionPolicy Bypass -File relay\setup.ps1 [-Rotate]
#   -Rotate replaces the route secret and connect token (every webhook URL changes).
param([switch]$Rotate)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

if (-not (Get-Command node -ErrorAction SilentlyContinue) -or -not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Error 'Node.js 20 or newer is required: https://nodejs.org/'
    exit 1
}

Write-Host "Installing the relay's tools (wrangler)..."
npm install --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setupArgs = @('scripts/setup.mjs')
if ($Rotate) { $setupArgs += '--rotate' }
node @setupArgs
exit $LASTEXITCODE
