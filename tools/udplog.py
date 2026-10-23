"""Receives the P! UDP log sent by the TV and writes it to stdout and to a file.

Usage: python3 -I tools/udplog.py [file.log] [port]
"""
import socket
import sys

path = sys.argv[1] if len(sys.argv) > 1 else "tv.log"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 9999
out = open(path, "a", buffering=1, encoding="utf-8")
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.bind(("0.0.0.0", port))
while True:
    data, addr = s.recvfrom(65535)
    line = data.decode("utf-8", "replace")
    out.write(line + "\n")
    print(line, flush=True)
