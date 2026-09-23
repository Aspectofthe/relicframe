#!/usr/bin/env sh
set -eu

no_build=false
forget=false
no_update=false
for argument in "$@"; do
  case "$argument" in
    --no-build) no_build=true ;;
    --forget-credentials) forget=true ;;
    --no-update) no_update=true ;;
    *) echo "Unknown option: $argument" >&2; exit 2 ;;
  esac
done

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository=$(dirname -- "$script_dir")
if [ "${RELICFRAME_UPDATE_CHECKED:-}" != 1 ] && [ "$no_update" = false ] && [ "${RELICFRAME_AUTO_UPDATE:-1}" != 0 ]; then
  if sh "$script_dir/update-before-launch.sh" "$repository"; then
    :
  else
    update_exit=$?
    if [ "$update_exit" -eq 10 ]; then
      RELICFRAME_UPDATE_CHECKED=1 RELICFRAME_UPDATE_APPLIED=1 exec sh "$script_dir/run-bot.sh" "$@"
    fi
  fi
fi
if [ "${RELICFRAME_UPDATE_APPLIED:-}" = 1 ] && [ "$no_build" = true ]; then
  echo '[update] Ignoring --no-build because new source code needs a Release build.'
  no_build=false
fi
runtime_dir=${RELICFRAME_RUNTIME_DIR:-"$script_dir/runtime"}
token_file="$runtime_dir/launch-token"
guild_file="$runtime_dir/launch-guild-id"

mkdir -p "$runtime_dir"
chmod 700 "$runtime_dir" 2>/dev/null || true
if [ "$forget" = true ]; then
  rm -f -- "$token_file" "$guild_file"
fi

if [ -z "${RELICFRAME_CSHARP_TOKEN:-}" ] && [ -f "$token_file" ]; then
  RELICFRAME_CSHARP_TOKEN=$(cat -- "$token_file")
  export RELICFRAME_CSHARP_TOKEN
fi
if [ -z "${RELICFRAME_TEST_GUILD_ID:-}" ] && [ -f "$guild_file" ]; then
  RELICFRAME_TEST_GUILD_ID=$(cat -- "$guild_file")
  export RELICFRAME_TEST_GUILD_ID
fi

if [ -z "${RELICFRAME_CSHARP_TOKEN:-}" ]; then
  printf 'Paste the Discord bot token (input is hidden): ' >&2
  stty -echo
  trap 'stty echo' EXIT HUP INT TERM
  IFS= read -r RELICFRAME_CSHARP_TOKEN
  stty echo
  trap - EXIT HUP INT TERM
  printf '\n' >&2
  export RELICFRAME_CSHARP_TOKEN
fi
if [ -z "${RELICFRAME_TEST_GUILD_ID:-}" ]; then
  printf 'Enter the Discord server ID: ' >&2
  IFS= read -r RELICFRAME_TEST_GUILD_ID
  export RELICFRAME_TEST_GUILD_ID
fi

case "$RELICFRAME_TEST_GUILD_ID" in
  *[!0-9]*|'') echo 'RELICFRAME_TEST_GUILD_ID must contain 17-20 digits.' >&2; exit 2 ;;
esac
guild_length=${#RELICFRAME_TEST_GUILD_ID}
if [ "$guild_length" -lt 17 ] || [ "$guild_length" -gt 20 ]; then
  echo 'RELICFRAME_TEST_GUILD_ID must contain 17-20 digits.' >&2
  exit 2
fi
if [ -z "$RELICFRAME_CSHARP_TOKEN" ]; then
  echo 'A Discord bot token is required. Nothing was saved.' >&2
  exit 2
fi

umask 077
printf '%s' "$RELICFRAME_CSHARP_TOKEN" > "$token_file"
printf '%s' "$RELICFRAME_TEST_GUILD_ID" > "$guild_file"
chmod 600 "$token_file" "$guild_file"

RELICFRAME_DATA_DIR=${RELICFRAME_DATA_DIR:-"$repository/relicframe/data"}
RELICFRAME_RUNTIME_DIR=$runtime_dir
RELICFRAME_EE_LOG=${RELICFRAME_EE_LOG:-true}
RELICFRAME_TRADE_OCR=${RELICFRAME_TRADE_OCR:-true}
export RELICFRAME_DATA_DIR RELICFRAME_RUNTIME_DIR RELICFRAME_EE_LOG RELICFRAME_TRADE_OCR

if [ -x "$repository/.tools/dotnet/dotnet" ]; then
  dotnet="$repository/.tools/dotnet/dotnet"
else
  dotnet=dotnet
fi

cd "$repository"
if [ "$no_build" = false ]; then
  "$dotnet" build csharp/RelicFrame.Bot -c Release
fi
exec "$dotnet" csharp/RelicFrame.Bot/bin/Release/net10.0/RelicFrame.Bot.dll --test-bot
