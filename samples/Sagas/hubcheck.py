#!/usr/bin/env python3
"""BC-9: what a browser does with the message hub, with the standard library only: a WebSocket to
<hub>?access_token=<token> (browsers cannot set headers on a WebSocket), the SignalR JSON handshake, Subscribe(<topic>),
then it prints the first pushed message of that topic as JSON (or 'timeout').
usage: hubcheck.py <ws url of the hub> <token> <topic> <seconds> -- <command run after subscribing, e.g. a curl that bids>"""
import base64, json, os, socket, subprocess, sys, time, urllib.parse

url, token, topic, seconds = sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4])
after = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
u = urllib.parse.urlparse(url)
s = socket.create_connection((u.hostname, u.port or 80), timeout=seconds)
key = base64.b64encode(os.urandom(16)).decode()
path = u.path + "?access_token=" + urllib.parse.quote(token)
s.sendall((f"GET {path} HTTP/1.1\r\nHost: {u.hostname}:{u.port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
           f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nOrigin: http://localhost:5173\r\n\r\n").encode())
head = b""
while b"\r\n\r\n" not in head:
    head += s.recv(1)
status = head.split(b"\r\n")[0].decode()
if " 101 " not in status:
    print("upgrade refused: " + status)
    sys.exit(1)

def send(text):
    data = text.encode()
    mask = os.urandom(4)
    header = bytes([0x81]) + (bytes([0x80 | len(data)]) if len(data) < 126 else bytes([0x80 | 126]) + len(data).to_bytes(2, "big"))
    s.sendall(header + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))

buffer = b""
def records():
    global buffer
    while True:
        first = s.recv(2)
        if len(first) < 2:
            return
        length = first[1] & 0x7F
        if length == 126:
            length = int.from_bytes(s.recv(2), "big")
        elif length == 127:
            length = int.from_bytes(s.recv(8), "big")
        payload = b""
        while len(payload) < length:
            payload += s.recv(length - len(payload))
        buffer += payload
        while b"\x1e" in buffer:
            record, buffer = buffer.split(b"\x1e", 1)
            yield json.loads(record or b"{}")

stream = records()
send(json.dumps({"protocol": "json", "version": 1}) + "\x1e")
next(stream)                                                       # {} : the handshake
send(json.dumps({"type": 1, "invocationId": "1", "target": "Subscribe", "arguments": [topic]}) + "\x1e")
deadline = time.time() + seconds
try:
    for message in stream:
        if message.get("type") == 3:
            if message.get("error"):
                print("subscribe refused: " + message["error"])
                sys.exit(1)
            if after:
                subprocess.run(after, check=False, stdout=subprocess.DEVNULL)
        elif message.get("type") == 1 and message.get("target") == "message":
            print(json.dumps(message["arguments"][0]))
            sys.exit(0)
        if time.time() > deadline:
            break
except (socket.timeout, TimeoutError):
    pass
except (ConnectionResetError, BrokenPipeError):
    print("closed by the server")
    sys.exit(2)
print("timeout")
sys.exit(1)
