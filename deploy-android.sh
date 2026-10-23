#!/usr/bin/env bash
# P! for Android: builds, installs and launches the app on a phone, tablet, Android TV or Fire TV over adb.
#
# Usage:  ./deploy-android.sh [options]
#   --connect HOST[:PORT]  adb connect first (a TV or Fire TV over the network; port 5555 by default)
#   --serial SERIAL        the adb device to use (default: the only one connected, or $ANDROID_SERIAL)
#   --config FILE          test configuration JSON pushed to the app's external files dir as p-config.json
#   --keep-config          leave the configuration already on the device untouched
#   --clear-config         delete the pushed configuration
#   --pc-ip IP             IP of this PC for the UDP log (detected by default)
#   --log-port N           UDP log port (9999)
#   --udp-log              the app also sends its log over UDP to this PC (off by default: the log is in logcat, tag P)
#   --listen FILE          start the UDP log receiver (tools/udplog.py) writing to FILE; implies --udp-log
#   --logcat               after starting, follow logcat for the app (Ctrl+C to stop)
#   --debug                Debug build (faster to build, much slower to run); default is Release
#   --abi ABI              build for one ABI only: arm64-v8a, armeabi-v7a or x86_64 (faster, smaller); "auto" = the device's; default: all three (universal)
#                          A Fire TV is always 32-bit: armeabi-v7a (or --abi auto with --connect)
#   --no-build             do not build (use the apk already generated)
#   --no-install           do not install (to push a configuration, or only listen to the log, on a device that already has the app)
#   --no-run               install but do not launch
#   --uninstall            uninstall first (needed if the signing key ever changes)
#   --build-only           only build and check the apk (no device needed) and copy it to --out
#   --out FILE             where --build-only puts the apk (default: dist/P-<version>-<abi or universal>.apk)
# Variables: ANDROID_HOME (~/Android/Sdk), JAVA_HOME (~/opt/jdk-17), DOTNET_DIR (~/.dotnet), ADB, P_ANDROID_KEYS (the signing key directory, outside the repository)
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ANDROID_HOME="${ANDROID_HOME:-$HOME/Android/Sdk}"
JAVA_HOME="${JAVA_HOME:-$HOME/opt/jdk-17}"
DOTNET_DIR="${DOTNET_DIR:-$HOME/.dotnet}"
ADB="${ADB:-$ANDROID_HOME/platform-tools/adb}"
APP_ID="org.p.app"
ACTIVITY="$APP_ID/$APP_ID.MainActivity"
PROJ="$HERE/src/PAndroid"
EXT_DIR="/sdcard/Android/data/$APP_ID/files"
DEV_CONFIG_NAME="p-config.json"
CONNECT=""; SERIAL="${ANDROID_SERIAL:-}"; CONFIG=""; KEEP=0; CLEAR=0; PC_IP=""; LOG_PORT=9999; UDP_LOG=0; LISTEN=""; LOGCAT=0
CONFIGURATION=Release; ABI=""; BUILD=1; RUN=1; UNINSTALL=0; INSTALL=1; BUILD_ONLY=0; APK_OUT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --connect) CONNECT="$2"; shift 2;;
    --serial) SERIAL="$2"; shift 2;;
    --config) CONFIG="$2"; shift 2;;
    --keep-config) KEEP=1; shift;;
    --clear-config) CLEAR=1; shift;;
    --pc-ip) PC_IP="$2"; shift 2;;
    --log-port) LOG_PORT="$2"; shift 2;;
    --udp-log) UDP_LOG=1; shift;;
    --listen) LISTEN="$2"; UDP_LOG=1; shift 2;;
    --logcat) LOGCAT=1; shift;;
    --debug) CONFIGURATION=Debug; shift;;
    --abi) ABI="$2"; shift 2;;
    --no-build) BUILD=0; shift;;
    --no-run) RUN=0; shift;;
    --no-install) INSTALL=0; shift;;
    --build-only) BUILD_ONLY=1; shift;;
    --out) APK_OUT="$2"; shift 2;;
    --uninstall) UNINSTALL=1; shift;;
    -h|--help) sed -n '2,24p' "$0"; exit 0;;
    *) echo "unknown option: $1 (see --help)"; exit 2;;
  esac
done

export JAVA_HOME PATH="$JAVA_HOME/bin:$DOTNET_DIR:$PATH" DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1 ANDROID_HOME

# Builds the apk (unless --no-build) and checks it for what a Fire TV and an Android TV need (tools/apkcheck.py): sets APK.
build_apk() {
  local RID_ARG=()
  case "$ABI" in
    "") ;;
    arm64-v8a) RID_ARG=(-p:PAndroidRids=android-arm64);;
    armeabi-v7a) RID_ARG=(-p:PAndroidRids=android-arm);;
    x86_64) RID_ARG=(-p:PAndroidRids=android-x64);;
    *) echo "unsupported ABI: $ABI"; exit 2;;
  esac
  if [[ $BUILD -eq 1 ]]; then
    (cd "$PROJ" && dotnet build -c "$CONFIGURATION" ${RID_ARG[@]+"${RID_ARG[@]}"} -p:AndroidSdkDirectory="$ANDROID_HOME" 2>&1 | grep -E "error|Warn|Err|Elapsed|Tiempo|Advertencia|Errores" | grep -v XA0141 | tail -8)
  fi
  APK="$PROJ/bin/$CONFIGURATION/net10.0-android/$APP_ID-Signed.apk"
  [[ -f "$APK" ]] || { echo "no apk at $APK: build first"; exit 1; }
  echo "   $APK ($(du -h "$APK" | cut -f1))"
  if command -v python3 >/dev/null 2>&1; then
    python3 -I "$HERE/tools/apkcheck.py" "$APK" $([[ -z "$ABI" ]] && echo --universal) | sed 's/^/   /' || echo "   (tools/apkcheck.py reports a problem: see above)"
  fi
}

if [[ $BUILD_ONLY -eq 1 ]]; then
  [[ "$ABI" != "auto" ]] || { echo "--abi auto needs a device: name the ABI (a Fire TV: armeabi-v7a)"; exit 2; }
  echo "== Building ($CONFIGURATION)"
  build_apk
  VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "$HERE/Directory.Build.props" | head -1)"
  [[ -n "$APK_OUT" ]] || APK_OUT="$HERE/dist/P-$VERSION-${ABI:-universal}.apk"
  mkdir -p "$(dirname "$APK_OUT")"
  cp "$APK" "$APK_OUT"
  echo "== Done: $APK_OUT ($(du -h "$APK_OUT" | cut -f1)); install it with adb install -r, or serve it to the Downloader app (see the README)"
  exit 0
fi

[[ -x "$ADB" ]] || { echo "adb not found at $ADB (set ANDROID_HOME or ADB)"; exit 1; }

echo "== 1/6 Connecting"
if [[ -n "$CONNECT" ]]; then
  [[ "$CONNECT" == *:* ]] || CONNECT="$CONNECT:5555"
  "$ADB" connect "$CONNECT" | tail -1
  [[ -n "$SERIAL" ]] || SERIAL="$CONNECT"
fi
if [[ -z "$SERIAL" ]]; then
  mapfile -t DEVICES < <("$ADB" devices | awk 'NR>1 && $2=="device" {print $1}')
  if [[ ${#DEVICES[@]} -eq 0 ]]; then
    echo "no device: connect one by USB (USB debugging on) or use --connect HOST[:PORT]"; "$ADB" devices; exit 1
  elif [[ ${#DEVICES[@]} -gt 1 ]]; then
    echo "several devices, choose with --serial:"; "$ADB" devices; exit 1
  fi
  SERIAL="${DEVICES[0]}"
fi
A=("$ADB" -s "$SERIAL")
"${A[@]}" get-state >/dev/null 2>&1 || { echo "the device $SERIAL does not answer (authorised? 'adb devices' must say 'device')"; exit 1; }
MODEL="$("${A[@]}" shell getprop ro.product.model | tr -d '\r')"; SDK="$("${A[@]}" shell getprop ro.build.version.sdk | tr -d '\r')"
ABILIST="$("${A[@]}" shell getprop ro.product.cpu.abilist | tr -d '\r')"
echo "   $SERIAL: $MODEL, Android SDK $SDK, ABIs $ABILIST"

echo "== 2/6 Building ($CONFIGURATION)"
if [[ "$ABI" == "auto" ]]; then ABI="${ABILIST%%,*}"; fi
if [[ $INSTALL -eq 1 ]]; then
  build_apk
else
  echo "   (not installing: --no-install)"
fi

echo "== 3/6 Installing"
if [[ $INSTALL -eq 0 ]]; then
  echo "   (skipped)"
else
  if [[ $UNINSTALL -eq 1 ]]; then "${A[@]}" uninstall "$APP_ID" >/dev/null 2>&1 || true; fi
  if ! OUT="$("${A[@]}" install -r -d "$APK" 2>&1)"; then
    echo "$OUT" | tail -3
    if grep -q "INSTALL_FAILED_UPDATE_INCOMPATIBLE\|signatures do not match" <<<"$OUT"; then echo "   signed with another key: run again with --uninstall"; fi
    if grep -q "INSTALL_FAILED_NO_MATCHING_ABIS" <<<"$OUT"; then echo "   the apk has no library for this device's ABI ($ABILIST): build without --abi, or with --abi auto"; fi
    if grep -q "INSTALL_FAILED_OLDER_SDK" <<<"$OUT"; then echo "   this device is older than Android 7.1 (Fire OS 6): a Fire TV Stick 2nd generation or older cannot run P!"; fi
    exit 1
  fi
  echo "   $(echo "$OUT" | tail -1)"
fi

echo "== 4/6 Test configuration"
if [[ $CLEAR -eq 1 ]]; then
  "${A[@]}" shell rm -f "$EXT_DIR/$DEV_CONFIG_NAME" || true
  echo "   removed $EXT_DIR/$DEV_CONFIG_NAME"
elif [[ $KEEP -eq 0 && ( -n "$CONFIG" || $UDP_LOG -eq 1 ) ]]; then
  if [[ $UDP_LOG -eq 1 && -z "$PC_IP" ]]; then
    DEV_IP="$("${A[@]}" shell ip route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -1 | tr -d '\r' || true)"
    [[ -z "$DEV_IP" && "$SERIAL" == *:* ]] && DEV_IP="${SERIAL%%:*}"
    [[ -n "$DEV_IP" ]] && PC_IP="$(ip -4 route get "$DEV_IP" 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -1 || true)"
    [[ -n "$PC_IP" ]] || PC_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
    echo "   this PC (for the UDP log): ${PC_IP:-unknown}"
  fi
  TMPCFG="$(mktemp)"
  python3 -I - "$CONFIG" "$PC_IP" "$LOG_PORT" "$UDP_LOG" > "$TMPCFG" <<'PY'
import json, sys
cfg_path, pc_ip, port, udp = sys.argv[1:5]
cfg = {}
if cfg_path:
    cfg = json.load(open(cfg_path))
if udp == "1" and pc_ip:
    log = cfg.setdefault("log", {})
    log.setdefault("enabled", True); log.setdefault("host", pc_ip); log.setdefault("port", int(port))
print(json.dumps(cfg))
PY
  echo "   -> $EXT_DIR/$DEV_CONFIG_NAME: $(cat "$TMPCFG")"
  "${A[@]}" shell mkdir -p "$EXT_DIR"
  "${A[@]}" push "$TMPCFG" "$EXT_DIR/$DEV_CONFIG_NAME" | tail -1
  rm -f "$TMPCFG"
else
  echo "   (unchanged)"
fi

if [[ -n "$LISTEN" ]]; then
  PIDFILE="${TMPDIR:-/tmp}/p-udplog.pid"
  if [[ -f "$PIDFILE" ]]; then kill "$(cat "$PIDFILE")" 2>/dev/null || true; rm -f "$PIDFILE"; fi
  nohup python3 -I "$HERE/tools/udplog.py" "$LISTEN" "$LOG_PORT" >/dev/null 2>&1 &
  echo $! > "$PIDFILE"
  echo "   UDP log receiver on $LISTEN (pid $(cat "$PIDFILE"); stop it with: kill \$(cat $PIDFILE))"
fi

if [[ $RUN -eq 1 ]]; then
  echo "== 5/6 Launching $ACTIVITY"
  "${A[@]}" shell am force-stop "$APP_ID"
  "${A[@]}" logcat -c || true
  "${A[@]}" shell am start -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -n "$ACTIVITY" | tail -2
else
  echo "== 5/6 Not launching (--no-run)"
fi

echo "== 6/6 Done"
echo "   logcat:   $ADB -s $SERIAL logcat -v time P:V '*:S'      (all of the app's lines have the tag P; add AndroidRuntime:E for crashes)"
echo "   test configuration on the device: $EXT_DIR/$DEV_CONFIG_NAME (restart the app to apply it)"
if [[ $LOGCAT -eq 1 ]]; then
  exec "$ADB" -s "$SERIAL" logcat -v time P:V AndroidRuntime:E DEBUG:E '*:S'
fi
