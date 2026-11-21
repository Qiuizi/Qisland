# -*- coding: utf-8 -*-
"""WinIslands SDK 示例：查询当前活跃卡片（Python 标准库）。

用法:
    python pull.py [port] [token]
"""
import json
import os
import sys
import urllib.request


def _token(arg):
    """Token：命令行参数 > 环境变量 QISLAND_TOKEN > %APPDATA%\\Qisland\\api-token.txt（没有就不带）。"""
    if arg:
        return arg
    if os.environ.get("QISLAND_TOKEN"):
        return os.environ["QISLAND_TOKEN"].strip()
    path = os.environ.get("QISLAND_API_TOKEN_FILE") or os.path.join(os.environ.get("APPDATA", ""), "Qisland", "api-token.txt")
    try:
        with open(path, encoding="utf-8") as f:
            return f.read().strip()
    except OSError:
        return ""

HOST = "127.0.0.1"
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 9840
TOKEN = _token(sys.argv[2] if len(sys.argv) > 2 else "")

req = urllib.request.Request(
    "http://%s:%d/v1/island/active" % (HOST, PORT),
    method="GET",
)
if TOKEN:
    req.add_header("X-Qisland-Token", TOKEN)
    req.add_header("X-WinIslands-Token", TOKEN)

with urllib.request.urlopen(req, timeout=5) as resp:
    cards = json.loads(resp.read().decode("utf-8"))

print("活跃卡片 %d 条:" % len(cards))
for i, c in enumerate(cards, 1):
    print("%d. [%s] %s  (position=%d, expires_at=%s)" % (
        i, c.get("priority", "normal"), c.get("title"), i, c.get("expires_at")))
