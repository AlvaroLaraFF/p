"""Checks a built P! apk for what a Fire TV (Fire OS 6, 7, 8...) and Android TV need, without a device.

Usage: python3 -I tools/apkcheck.py FILE.apk [--universal]

  --universal   the apk is meant for every device: it must carry arm64-v8a, armeabi-v7a and x86_64 libraries

Looks at: the package and its SDK levels (minSdk 25 = Fire OS 6), the launcher entries (a Fire TV lists an app by its LEANBACK_LAUNCHER activity and its 320 x 180 banner),
the features that must not be required (touch screen, leanback), the native libraries (Amazon documents every Fire TV as a 32-bit device: armeabi-v7a is the one that runs),
the signature (Fire OS 8 refuses an apk without an APK Signature Scheme v2 signature), that the Google Cast sender (a client of Google Play services) is there but nothing of Google's starts by itself (Fire OS has no Google services), the foreground service and permissions of the sending, and native libraries with a
segment that is both writable and executable (Fire OS 7 refuses to load them). Needs the Android SDK build-tools (ANDROID_HOME, default ~/Android/Sdk); apksigner also needs java
(JAVA_HOME, default ~/opt/jdk-17). Exit status 1 if something that stops the app from installing or running is wrong.
"""
import os
import re
import struct
import subprocess
import sys
import zipfile

FAILS = []
WARNS = []


def ok(msg):
    print("  [ ok ] " + msg)


def fail(msg):
    FAILS.append(msg)
    print("  [FAIL] " + msg)


def warn(msg):
    WARNS.append(msg)
    print("  [warn] " + msg)


def build_tool(name):
    home = os.environ.get("ANDROID_HOME") or os.path.expanduser("~/Android/Sdk")
    root = os.path.join(home, "build-tools")
    if not os.path.isdir(root):
        return None
    versions = sorted(os.listdir(root), key=lambda v: [int(x) if x.isdigit() else 0 for x in re.split(r"[.\-]", v)])
    for v in reversed(versions):
        p = os.path.join(root, v, name)
        if os.path.exists(p):
            return p
    return None


def png_size(data):
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        return None
    return struct.unpack(">II", data[16:24])


def elf_segments_rwx(data):
    """True if some PT_LOAD segment of the ELF file is both writable and executable."""
    if data[:4] != b"\x7fELF":
        return False
    is64 = data[4] == 2
    if is64:
        phoff, = struct.unpack_from("<Q", data, 0x20)
        phentsize, phnum = struct.unpack_from("<HH", data, 0x36)
    else:
        phoff, = struct.unpack_from("<I", data, 0x1C)
        phentsize, phnum = struct.unpack_from("<HH", data, 0x2A)
    for i in range(phnum):
        off = phoff + i * phentsize
        p_type, = struct.unpack_from("<I", data, off)
        flags, = struct.unpack_from("<I", data, off + (4 if is64 else 24))
        if p_type == 1 and (flags & 1) and (flags & 2):
            return True
    return False


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    apk = argv[1]
    universal = "--universal" in argv
    if not os.path.isfile(apk):
        print("no such file: " + apk)
        return 2
    print("apk: %s (%.1f MB)" % (apk, os.path.getsize(apk) / 1048576.0))

    aapt2 = build_tool("aapt2")
    if not aapt2:
        print("aapt2 not found (set ANDROID_HOME to the Android SDK)")
        return 2
    badging = subprocess.check_output([aapt2, "dump", "badging", apk]).decode("utf-8", "replace")
    zf = zipfile.ZipFile(apk)
    names = zf.namelist()

    print("package and SDK levels")
    m = re.search(r"package: name='([^']+)' versionCode='(\d+)' versionName='([^']*)'", badging)
    if m:
        ok("package %s, version %s (code %s)" % (m.group(1), m.group(3), m.group(2)))
    else:
        fail("no package line in the badging")
    m = re.search(r"minSdkVersion:'(\d+)'", badging)
    min_sdk = int(m.group(1)) if m else 0
    if min_sdk == 25:
        ok("minSdkVersion 25 (Fire OS 6, the oldest Fire OS it can run on)")
    elif 0 < min_sdk < 25:
        warn("minSdkVersion %d is below 25: the app has not been tested on Fire OS 5" % min_sdk)
    else:
        fail("minSdkVersion is %d: Fire OS 6 (API 25) would refuse the apk" % min_sdk)
    m = re.search(r"targetSdkVersion:'(\d+)'", badging)
    ok("targetSdkVersion %s" % (m.group(1) if m else "?"))

    print("launcher")
    if "leanback-launchable-activity:" in badging:
        ok("LEANBACK_LAUNCHER activity: the app shows in the Fire TV and Android TV home")
    else:
        fail("no LEANBACK_LAUNCHER activity: a Fire TV or Android TV would not list the app")
    if re.search(r"^launchable-activity:", badging, re.M):
        ok("LAUNCHER activity: the app shows on phones and tablets")
    else:
        fail("no LAUNCHER activity")
    m = re.search(r"leanback-launchable-activity: [^\n]*banner='([^']+)'", badging) or re.search(r"application: [^\n]*banner='([^']+)'", badging)
    if m:
        try:
            size = png_size(zf.read(m.group(1)))
            if size == (320, 180):
                ok("banner %s is 320 x 180" % m.group(1))
            else:
                warn("banner %s is %s, the TV launchers use 320 x 180" % (m.group(1), size))
        except KeyError:
            fail("the banner %s is not in the apk" % m.group(1))
    else:
        fail("no banner: the Android TV and Fire TV home draw a blank tile")
    if re.search(r"application: [^\n]*icon='", badging):
        ok("application icon present")
    else:
        fail("no application icon")

    print("features that must not be required")
    for feat in ("android.hardware.touchscreen", "android.software.leanback"):
        if re.search(r"uses-feature-not-required: name='%s'" % re.escape(feat), badging):
            ok("%s is not required" % feat)
        elif re.search(r"uses-feature: name='%s'" % re.escape(feat), badging):
            fail("%s is REQUIRED: the apk would not install on %s" % (feat, "a Fire TV or Android TV" if "touch" in feat else "a phone"))
        else:
            ok("%s is not declared" % feat)
    for feat in ("android.hardware.camera", "android.hardware.microphone", "android.hardware.bluetooth", "android.hardware.sensor.accelerometer"):
        if re.search(r"uses-feature: name='%s'" % re.escape(feat), badging):
            fail("%s is required and a Fire TV stick does not have it" % feat)

    print("native libraries")
    abis = sorted(set(re.findall(r"lib/([^/]+)/", " ".join(names))))
    if "armeabi-v7a" in abis:
        ok("armeabi-v7a (32 bit ARM, what every Fire TV runs) is there; ABIs: %s" % ", ".join(abis))
    else:
        fail("no armeabi-v7a library: Amazon documents every Fire TV as 32-bit, the app would not install (INSTALL_FAILED_NO_MATCHING_ABIS). ABIs: %s" % ", ".join(abis))
    if universal:
        for want in ("arm64-v8a", "x86_64"):
            if want in abis:
                ok("%s is there (universal apk)" % want)
            else:
                fail("universal apk without %s" % want)
    rwx = []
    for n in names:
        if re.match(r"lib/(armeabi-v7a|arm64-v8a)/[^/]+\.so$", n):
            try:
                if elf_segments_rwx(zf.read(n)):
                    rwx.append(n)
            except Exception as ex:
                warn("could not read %s: %s" % (n, ex))
    if rwx:
        fail("native libraries with a writable and executable segment (Fire OS 7 refuses to load them): " + ", ".join(rwx))
    else:
        ok("no native library has a writable and executable segment")

    print("Google Play services and Cast")
    # "Enviar a la tele" uses the Google Cast SDK (a client of Google Play services) on phones and tablets that have them. The apk carries its code on every device, so what matters is that nothing of it
    # RUNS by itself where there are no Google services (a Fire TV): no content provider and no required library of Google's (those start with the app), and the code that uses it is only reached behind
    # AndroidCast.IsEnabled. The foreground service of the sending needs its type and permissions (Android 14 refuses it otherwise).
    xml = subprocess.check_output([aapt2, "dump", "xmltree", "--file", "AndroidManifest.xml", apk]).decode("utf-8", "replace")
    elements = []
    cur = None
    for line in xml.splitlines():
        m = re.match(r"\s*E: (\S+) ", line)
        if m:
            cur = {"tag": m.group(1), "attrs": {}}
            elements.append(cur)
            continue
        m = re.match(r"\s*A: (?:http://schemas.android.com/apk/res/android:)?([A-Za-z:]+)(?:\(0x[0-9a-f]+\))?=\"?([^\"(]*)\"?", line)
        if m and cur is not None:
            cur["attrs"][m.group(1)] = m.group(2)
    google_start = [e for e in elements if e["tag"] == "provider" and re.match(r"(com\.google\.|com\.firebase|com\.android\.vending)", e["attrs"].get("name", ""))]
    google_libs = [e for e in elements if e["tag"] == "uses-library" and "google" in e["attrs"].get("name", "") and e["attrs"].get("required", "") != "false"]
    if google_start or google_libs:
        fail("something of Google starts with the app or is required (a Fire TV has no Google Play services): " + ", ".join(e["attrs"].get("name", "?") for e in google_start + google_libs))
    else:
        ok("no provider and no required library of Google's: nothing of the Cast SDK runs by itself where there are no Google Play services")
    has_sdk = any(re.match(r"classes\d*\.dex$", n) and b"com/google/android/gms/cast/framework" in zf.read(n) for n in names)
    has_options = any(re.match(r"classes\d*\.dex$", n) and b"org/p/app/CastOptionsProvider" in zf.read(n) for n in names)
    meta = [e for e in elements if e["tag"] == "meta-data" and e["attrs"].get("name") == "com.google.android.gms.cast.framework.OPTIONS_PROVIDER_CLASS_NAME"]
    if has_sdk:
        ok("the Google Cast sender is in the apk (behind AndroidCast.IsEnabled)")
        if meta and meta[0]["attrs"].get("value") == "org.p.app.CastOptionsProvider" and has_options:
            ok("the options provider of the Cast sender is declared and in the apk (Default Media Receiver)")
        else:
            fail("the Cast sender has no options provider (OPTIONS_PROVIDER_CLASS_NAME / org.p.app.CastOptionsProvider)")
    svc = [e for e in elements if e["tag"] == "service" and e["attrs"].get("name") == "org.p.app.CastSendingService"]
    if svc and svc[0]["attrs"].get("foregroundServiceType", "") in ("0x00000002", "0x2", "mediaPlayback") and svc[0]["attrs"].get("exported") in ("false", "0x0", "0"):
        ok("the sending service is a foreground service of type mediaPlayback and is not exported")
    else:
        fail("the sending service (org.p.app.CastSendingService) is missing, not mediaPlayback or exported")
    for perm in ("FOREGROUND_SERVICE", "FOREGROUND_SERVICE_MEDIA_PLAYBACK", "POST_NOTIFICATIONS", "WAKE_LOCK"):
        if "uses-permission: name='android.permission.%s'" % perm in badging:
            ok("permission %s" % perm)
        else:
            fail("permission %s is missing (the sending would not work with the screen off)" % perm)

    print("signature")
    apksigner = build_tool("apksigner")
    if not apksigner:
        warn("apksigner not found: signature not checked")
    else:
        env = dict(os.environ)
        env.setdefault("JAVA_HOME", os.path.expanduser("~/opt/jdk-17"))
        env["PATH"] = os.path.join(env["JAVA_HOME"], "bin") + os.pathsep + env.get("PATH", "")
        try:
            out = subprocess.check_output([apksigner, "verify", "-v", apk], env=env, stderr=subprocess.STDOUT).decode("utf-8", "replace")
            v2 = re.search(r"APK Signature Scheme v2\): (\w+)", out)
            v3 = re.search(r"APK Signature Scheme v3\): (\w+)", out)
            if v2 and v2.group(1) == "true":
                ok("signed with APK Signature Scheme v2" + (" and v3" if v3 and v3.group(1) == "true" else "") + " (Fire OS 8 requires v2)")
            else:
                fail("not signed with APK Signature Scheme v2: Fire OS 8 would refuse it")
        except subprocess.CalledProcessError as ex:
            fail("apksigner says the signature is not valid: " + ex.output.decode("utf-8", "replace").strip()[:200])
        except OSError as ex:
            warn("could not run apksigner (is java installed? JAVA_HOME): %s" % ex)

    print()
    if FAILS:
        print("%d problem(s), %d warning(s)" % (len(FAILS), len(WARNS)))
        return 1
    print("all checks passed" + (" (%d warning(s))" % len(WARNS) if WARNS else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
