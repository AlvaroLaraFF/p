#!/usr/bin/env bash
# P!: builds, signs with the Samsung certificate profile (PROFILE, default "p"), installs and launches the app on the TV.
#
# Usage:  ./deploy.sh <tv-ip> [options]
#   --pc-ip IP          IP of this PC for the UDP log (detected by default; the app receives it in its config)
#   --log-port N        UDP log port (9999)
#   --config FILE       test configuration JSON copied to the TV (autoPlay, mp4Test, ...)
#   --keep-config       leave the configuration already on the TV untouched
#   --no-log            the app does not send its log over UDP
#   --listen FILE       start the log receiver first (tools/udplog.py), writing to FILE
#   --no-build          do not build (use the .tpk already generated)
#   --no-run            install but do not launch
#   --uninstall         uninstall before installing
# Variables: SDB_PORT (26101), PROFILE (p), DOTNET_DIR (~/.dotnet), TIZEN_DIR (~/tizen-studio)
set -euo pipefail

TV_IP="${1:-}"
if [[ -z "$TV_IP" || "$TV_IP" == -* ]]; then sed -n '2,15p' "$0"; exit 2; fi
shift

SDB_PORT="${SDB_PORT:-26101}"
PROFILE="${PROFILE:-p}"
DOTNET_DIR="${DOTNET_DIR:-$HOME/.dotnet}"
TIZEN_DIR="${TIZEN_DIR:-$HOME/tizen-studio}"
APP_ID="org.p.app"
HERE="$(cd "$(dirname "$0")" && pwd)"
PROJ="$HERE/src/PTizen"
SDB="$TIZEN_DIR/tools/sdb"
TIZEN="$TIZEN_DIR/tools/ide/bin/tizen"
DEV_CONFIG_PATH="/home/owner/share/tmp/sdk_tools/p-config.json"
PC_IP=""; LOG_PORT=9999; CONFIG=""; KEEP=0; NOLOG=0; LISTEN=""; BUILD=1; RUN=1; UNINSTALL=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --pc-ip) PC_IP="$2"; shift 2;;
    --log-port) LOG_PORT="$2"; shift 2;;
    --config) CONFIG="$2"; shift 2;;
    --keep-config) KEEP=1; shift;;
    --no-log) NOLOG=1; shift;;
    --listen) LISTEN="$2"; shift 2;;
    --no-build) BUILD=0; shift;;
    --no-run) RUN=0; shift;;
    --uninstall) UNINSTALL=1; shift;;
    *) echo "unknown option: $1"; exit 2;;
  esac
done

SERIAL="$TV_IP:$SDB_PORT"
export PATH="$DOTNET_DIR:$PATH" DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1

if [[ -z "$PC_IP" ]]; then
  PC_IP="$(ip -4 route get "$TV_IP" 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -1 || true)"
fi

echo "== 1/5 Building"
if [[ $BUILD -eq 1 ]]; then
  (cd "$PROJ" && dotnet build -c Release 2>&1 | tail -4)
fi
TPK="$(ls -t "$PROJ"/bin/Release/tizen60/*.tpk 2>/dev/null | head -1 || true)"
[[ -n "$TPK" ]] || { echo "no .tpk found: build first"; exit 1; }

echo "== 2/5 Signing with profile '$PROFILE'"
"$TIZEN" package -t tpk -s "$PROFILE" -- "$TPK" | tail -2

echo "== 3/5 Connecting to the TV $SERIAL"
if ! "$SDB" devices | grep -q "^$SERIAL"; then
  "$SDB" connect "$SERIAL" || true
  if ! "$SDB" devices | grep -q "^$SERIAL.*device"; then
    echo "   sdb cannot connect: retrying with kill-server"; "$SDB" kill-server; sleep 1; "$SDB" connect "$SERIAL" || true
  fi
fi
"$SDB" devices | grep -q "^$SERIAL.*device" || { echo "the TV does not respond (is it on? developer mode with this PC's IP? restarted after enabling it?)"; exit 1; }

if [[ $KEEP -eq 0 ]]; then
  TMPCFG="$(mktemp)"
  python3 -I - "$CONFIG" "$PC_IP" "$LOG_PORT" "$NOLOG" > "$TMPCFG" <<'PY'
import json, sys
cfg_path, pc_ip, port, nolog = sys.argv[1:5]
cfg = {}
if cfg_path:
    cfg = json.load(open(cfg_path))
log = cfg.setdefault("log", {})
if nolog == "1": log["enabled"] = False
elif pc_ip:
    log.setdefault("enabled", True); log.setdefault("host", pc_ip); log.setdefault("port", int(port))
print(json.dumps(cfg))
PY
  echo "== 3b/5 Test configuration -> $DEV_CONFIG_PATH: $(cat "$TMPCFG")"
  "$SDB" -s "$SERIAL" push "$TMPCFG" "$DEV_CONFIG_PATH" | tail -1
  rm -f "$TMPCFG"
fi

if [[ -n "$LISTEN" ]]; then
  PIDFILE="${TMPDIR:-/tmp}/p-udplog.pid"
  if [[ -f "$PIDFILE" ]]; then kill "$(cat "$PIDFILE")" 2>/dev/null || true; rm -f "$PIDFILE"; fi
  nohup python3 -I "$HERE/tools/udplog.py" "$LISTEN" >/dev/null 2>&1 &
  echo $! > "$PIDFILE"
  echo "   log receiver on $LISTEN (pid $(cat "$PIDFILE"); stop it with: kill \$(cat $PIDFILE))"
fi

echo "== 4/5 Installing"
if [[ $UNINSTALL -eq 1 ]]; then "$TIZEN" uninstall -s "$SERIAL" -p "$APP_ID" | tail -2 || true; fi
"$TIZEN" install -s "$SERIAL" -n "$TPK" | tail -3

if [[ $RUN -eq 1 ]]; then
  echo "== 5/5 Launching $APP_ID"
  "$TIZEN" run -s "$SERIAL" -p "$APP_ID" | tail -2
  echo "   (reinstalling restarts the app; if it was already open, 'tizen run' only brings it to the front)"
fi
echo "Done. Log: python3 -I $HERE/tools/udplog.py file.log   (the TV sends it to $PC_IP:$LOG_PORT)"
