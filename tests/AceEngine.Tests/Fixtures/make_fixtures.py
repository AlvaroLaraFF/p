#!/usr/bin/env python3
"""
Generates the transport test files with an implementation INDEPENDENT of the C# one
(Python + cryptography + hashlib). The expected values go in expected.json.
No real Ace transport files are available, so these are synthetic: they validate the plumbing
(headers, MD5(key), IV, padding, bencode, SHA-1), not that the keys/IVs used are the real ones.
"""
import hashlib, json, os, struct
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

HERE = os.path.dirname(os.path.abspath(__file__))

KEY_TDEF_V3 = bytes.fromhex("25453028744b38725d384b4b553d63727a21567565783062234929482b21306e7d2566305d4c5f783063682b2b3f2d3c23594877586b764d36554c")
KEY_TSLIVE = b"tslive_key"
KEY_TDEF_V2 = b"=Atl6GD#Vb+#QwW9zJy34lBOcM-7R7G)"
KEY_T1 = bytes.fromhex("7b4d3144587c7e0d72475d4f61534b6b4b505325415f416e6239695f77")
KEY_T2 = bytes.fromhex("3f23684f4277436d515f475e6d29644f694244535b4f4860783e3b673f2f475e63656a297e506d5a246245")

def benc(x):
    if isinstance(x, bool): x = int(x)
    if isinstance(x, int): return b"i%de" % x
    if isinstance(x, str): x = x.encode()
    if isinstance(x, bytes): return b"%d:%s" % (len(x), x)
    if isinstance(x, (list, tuple)): return b"l" + b"".join(benc(i) for i in x) + b"e"
    if isinstance(x, dict):
        # Ace's order: by str((k, v)) -- here the keys are normal ASCII, which is equivalent to the standard one
        items = sorted(((k.encode() if isinstance(k, str) else k), v) for k, v in x.items())
        return b"d" + b"".join(benc(k) + benc(v) for k, v in items) + b"e"
    raise TypeError(type(x))

def pad(b, bs=16):
    n = bs - len(b) % bs
    return b + bytes([n]) * n

def aes_enc(data, key, iv_mode):
    k = hashlib.md5(key).digest()
    if iv_mode == "zero": iv = bytes(16)
    elif iv_mode == "sha1key": iv = hashlib.sha1(key).digest()[:16]
    elif iv_mode == "sha1md5": iv = hashlib.sha1(k).digest()[:16]
    else: raise ValueError(iv_mode)
    c = Cipher(algorithms.AES(k), modes.CBC(iv)).encryptor()
    return c.update(pad(data)) + c.finalize()

def sha1(b): return hashlib.sha1(b).hexdigest()

def write(name, data):
    with open(os.path.join(HERE, name), "wb") as f: f.write(data)
    return data

# ---- Synthetic data of the VOD torrent (3 files; only 2 are video) ----
piece_len = 16384
files = [
    ("Carpeta/pelicula.mp4", bytes((i * 7 + 3) % 256 for i in range(40000))),
    ("Carpeta/subs.srt", b"1\n00:00:01,000 --> 00:00:02,000\nhola\n"),
    ("extra.mkv", bytes((i * 13 + 5) % 256 for i in range(20000))),
]
content = b"".join(d for _, d in files)
pieces = b"".join(hashlib.sha1(content[i:i + piece_len]).digest() for i in range(0, len(content), piece_len))
info = {
    "name": "Pack de prueba",
    "piece length": piece_len,
    "pieces": pieces,
    "files": [{"length": len(d), "path": p.split("/")} for p, d in files],
}
torrent = {
    "announce": "http://tracker.example.test/announce",
    "announce-list": [["http://tracker.example.test/announce"], ["udp://t2.example.test:6969"]],
    "info": info,
    "x-ts-properties": {"bitrate": 1000000},
    "nodes": [["router.example.test", 6881]],
}
plain = benc(torrent)
exp = {}
exp["torrent"] = {
    "infohash": sha1(benc(info)),
    "name": "Pack de prueba",
    "piece_length": piece_len,
    "piece_count": len(pieces) // 20,
    "total_length": len(content),
    "files": [{"path": p, "length": len(d)} for p, d in files],
}

fx = {}
fx["plain.torrent"] = write("plain.torrent", plain)
fx["legacy_v3.acetorrent"] = write("legacy_v3.acetorrent", b"\x01\x02\x03\x04\x11\x02e." + aes_enc(plain, KEY_TDEF_V3, "zero"))
fx["legacy_tslive.acetorrent"] = write("legacy_tslive.acetorrent", b"\x01\x02\x03\x04" + aes_enc(plain, KEY_TSLIVE, "zero"))
fx["legacy_v2.acetorrent"] = write("legacy_v2.acetorrent", b"\x11\x02e." + aes_enc(plain, KEY_TDEF_V2, "zero"))
# Variant with IV = SHA1(MD5(key)) to check that Auto finds it
fx["legacy_v3_ivsha1md5.acetorrent"] = write("legacy_v3_ivsha1md5.acetorrent", b"\x01\x02\x03\x04\x11\x02e." + aes_enc(plain, KEY_TDEF_V3, "sha1md5"))
for n, d in fx.items():
    exp[n] = {"content_id": sha1(d)}

# ---- BT live stream in AceStreamTransport ----
pubkey = bytes(range(1, 100))   # a valid RSA key is not needed to test the infohash
live = {
    "name": "Canal de prueba",
    "authmethod": "RSA",
    "pubkey": pubkey,
    "piece_length": 262144,
    "bitrate": 500000,
    "trackers": ["http://live-tracker.example.test/announce"],
    "meta_trackers": ["http://mt.example.test/meta"],
    "private": 0,
    "access": {"b": 1, "a": "x"},
}
HASH_FIELDS = ["name", "authmethod", "pubkey", "piece_length", "chunk_length", "bitrate", "premium", "commerce",
               "provider_key", "provider_sid", "access", "sharing", "private", "tns", "license"]
items = [[k, live[k]] for k in HASH_FIELDS if k in live]
live_infohash = sha1(benc(items))
exp["live"] = {"infohash": live_infohash, "name": "Canal de prueba", "piece_length": 262144, "bitrate": 500000,
               "pubkey_hex": pubkey.hex()}

live_plain = benc(live)
fx["live_v1.acelive"] = write("live_v1.acelive", b"AceStreamTransport" + struct.pack(">H", 1) + aes_enc(live_plain, KEY_T1, "sha1key"))
fx["live_v2.acelive"] = write("live_v2.acelive", b"AceStreamTransport" + struct.pack(">H", 2) + aes_enc(live_plain, KEY_T2, "sha1key"))
fx["live_v2_ivzero.acelive"] = write("live_v2_ivzero.acelive", b"AceStreamTransport" + struct.pack(">H", 2) + aes_enc(live_plain, KEY_T2, "zero"))
for n in ("live_v1.acelive", "live_v2.acelive", "live_v2_ivzero.acelive"):
    exp[n] = {"content_id": sha1(fx[n])}

# ---- Multi-quality: qualities with 'data' = nested transport ----
lowq = dict(live, name="Canal baja", bitrate=250000, piece_length=131072)
inner_hi = fx["live_v2.acelive"]
inner_lo = b"AceStreamTransport" + struct.pack(">H", 2) + aes_enc(benc(lowq), KEY_T2, "sha1key")
multi = {"name": "Canal multi", "qualities": [{"name": "alta", "data": inner_hi}, {"name": "baja", "data": inner_lo}]}
fx["multi_v2.acelive"] = write("multi_v2.acelive", b"AceStreamTransport" + struct.pack(">H", 2) + aes_enc(benc(multi), KEY_T2, "sha1key"))
exp["multi_v2.acelive"] = {"content_id": sha1(fx["multi_v2.acelive"]), "qualities": [
    {"name": "alta", "infohash": live_infohash},
    {"name": "baja", "infohash": sha1(benc([[k, lowq[k]] for k in HASH_FIELDS if k in lowq]))}]}

# ---- HLS descriptor ----
hls = {"type": "hls", "name": "Canal HLS", "manifest_url": "http://example.test/live.m3u8"}
fx["hls_v1.acelive"] = write("hls_v1.acelive", b"AceStreamTransport" + struct.pack(">H", 1) + aes_enc(benc(hls), KEY_T1, "sha1key"))
exp["hls_v1.acelive"] = {"content_id": sha1(fx["hls_v1.acelive"]), "name": "Canal HLS", "type": "hls"}

# Content data for piece tests
write("pack_content.bin", content)

with open(os.path.join(HERE, "expected.json"), "w") as f:
    json.dump(exp, f, indent=2, sort_keys=True)
print("ok", sorted(exp.keys()))
