#!/usr/bin/env bash
# Deploys the Cuebot webhook relay to your own (free) Cloudflare account.
# Usage: ./setup.sh [--rotate]     (--rotate replaces the route secret and connect token)
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v node >/dev/null 2>&1 || ! command -v npm >/dev/null 2>&1; then
  echo "Node.js 20 or newer is required: https://nodejs.org/" >&2
  exit 1
fi

echo "Installing the relay's tools (wrangler)..."
npm install --no-audit --no-fund
node scripts/setup.mjs "$@"
