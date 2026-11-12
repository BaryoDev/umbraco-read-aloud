#!/usr/bin/env bash
# Fails when a dotnet test trx shows no results file, fewer than MIN tests, or any test that
# did not pass. A run that discovers nothing exits zero, so the exit code alone proves nothing.
#
#   scripts/assert-tests-ran.sh <results.trx> <min>
set -u

TRX=${1:-}
MIN=${2:-}
test -n "$TRX" && test -n "$MIN" || { echo "usage: $0 <results.trx> <min>"; exit 2; }
test -f "$TRX" || { echo "no trx produced at $TRX: nothing ran"; exit 1; }

TOTAL=$(grep -o 'total="[0-9]*"' "$TRX" | head -1 | grep -o '[0-9]*')
PASSED=$(grep -o 'passed="[0-9]*"' "$TRX" | head -1 | grep -o '[0-9]*')
echo "total=${TOTAL:-0} passed=${PASSED:-0} floor=$MIN"
test "${TOTAL:-0}" -ge "$MIN" || { echo "expected at least $MIN tests, got ${TOTAL:-0}"; exit 1; }
test "$TOTAL" = "${PASSED:-0}" || { echo "not every test passed"; exit 1; }
