#!/usr/bin/env bash
# Chromium conformance run: every browser-backed test must actually run.
#
# The default test run returns early on a machine without Chromium so the package can be built
# anywhere. This script sets ZEROCDP_REQUIRE_BROWSER=1, which turns a missing browser into a
# failure, and then executes the whole suite against the real browser.
#
# Usage:
#   scripts/test-chromium.sh                     # full suite
#   scripts/test-chromium.sh --filter Category=X # any dotnet test arguments
set -euo pipefail

cd "$(dirname "$0")/.."

export ZEROCDP_REQUIRE_BROWSER=1

dotnet test tests/ZeroCdp.Tests/ZeroCdp.Tests.csproj -c Release "$@"
