#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "Usage: $0 PATH_TO_SLAY_THE_SPIRE_2 [PATH_TO_GAME_DATA]" >&2
    exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
game_directory="$1"
data="${2:-}"
if [[ -z "$data" ]]; then
    for candidate in data_sts2_linuxbsd_x86_64 data_sts2_windows_x86_64; do
        if [[ -f "$game_directory/$candidate/sts2.dll" ]]; then
            data="$game_directory/$candidate"
            break
        fi
    done
fi
if [[ -z "$data" || ! -f "$data/sts2.dll" ]]; then
    echo "Could not find a supported game data directory" >&2
    exit 2
fi
if ! command -v dotnet >/dev/null 2>&1; then
    echo ".NET Runtime/SDK 9 is required and dotnet must be available in PATH" >&2
    exit 2
fi

source_worker="$script_dir/EndTurnSimulator/bin/Release/net9.0/EndTurnSimulator.dll"
packaged_worker="$script_dir/simulator/EndTurnSimulator.dll"
if [[ -f "$script_dir/EndTurnSimulator/EndTurnSimulator.csproj" ]]; then
    for project in EndTurnDamagePreview EndTurnSimulator; do
        dotnet build "$script_dir/$project/$project.csproj" -c Release -p:GameData="$data" -v q
    done
    worker="$source_worker"
else
    worker="$packaged_worker"
fi
if [[ ! -f "$worker" ]]; then
    echo "Could not find EndTurnSimulator.dll" >&2
    exit 2
fi

diagnostic="$(dotnet "$worker" "$data" --diagnostic --baselib)"
echo "DIAGNOSTIC $diagnostic"

for scenario in turns burn block-expiry barricade regen poison choice lethal-burn poison-victory osty osty-lethal; do
    dotnet "$worker" "$data" --selftest --case="$scenario"
done

fixture="$(dotnet "$worker" "$data" --fixture --baselib)"
result="$(printf '%s\n' "$fixture" | dotnet "$worker" "$data")"
if [[ "$result" != *'"Verified":true'* || "$result" != *'"Hp":65'* || "$result" != *'"Block":0'* ]]; then
    echo "Independent replay mismatch: $result" >&2
    exit 1
fi
echo "PASS independent replay: 65 HP, 0 block"

coproc simulator { dotnet "$worker" "$data" --server --baselib; }
simulator_output="${simulator[0]}"
simulator_input="${simulator[1]}"
simulator_pid="$simulator_PID"
IFS= read -r ready <&"$simulator_output"
if [[ "$ready" != "READY" ]]; then
    echo "Warm simulator did not start" >&2
    exit 1
fi
for _ in 1 2 3 4 5; do
    printf '%s\n' "$fixture" >&"$simulator_input"
    IFS= read -r warm <&"$simulator_output"
    elapsed="$(sed -n 's/.*"ElapsedMs":\([0-9][0-9]*\).*/\1/p' <<<"$warm")"
    if [[ "$warm" != *'"Verified":true'* || "$warm" != *'"Hp":65'* || -z "$elapsed" || "$elapsed" -gt 100 ]]; then
        echo "Warm simulation failed or exceeded 100 ms: $warm" >&2
        exit 1
    fi
done
exec {simulator_input}>&-
wait "$simulator_pid"
echo "PASS persistent warm engine: 5 verified runs under 100 ms"

invalid_baseline="$(sed -E 's/"Baseline":"[^"]*"/"Baseline":"intentionally-invalid"/' <<<"$fixture")"
result="$(printf '%s\n' "$invalid_baseline" | dotnet "$worker" "$data")"
if [[ "$result" == *'"Verified":true'* ]]; then
    echo "Divergent state was accepted" >&2
    exit 1
fi
echo "PASS divergent baseline rejected"

unsupported="$(sed -E 's/"GameModule":"[^"]*"/"GameModule":"unsupported-build"/' <<<"$fixture")"
result="$(printf '%s\n' "$unsupported" | dotnet "$worker" "$data")"
if [[ "$result" == *'"Verified":true'* ]]; then
    echo "Unsupported version was accepted" >&2
    exit 1
fi
echo "PASS unsupported engine version rejected"
