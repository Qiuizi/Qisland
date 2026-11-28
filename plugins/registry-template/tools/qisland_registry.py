#!/usr/bin/env python3
"""
Qisland 插件索引工具（纯 Python 3.9+ 标准库，零依赖）。

  python tools/qisland_registry.py validate            # 校验 plugins/ themes/ registry.json revoked.json（CI 用）
  python tools/qisland_registry.py validate --strict-sig   # 另外要求两个 .sig 和当前内容一致（main 分支用）
  python tools/qisland_registry.py hash <文件>          # 按 Qisland 的规则算 SHA-256
  python tools/qisland_registry.py fix-hashes          # 维护者：按仓库里的文件重写 registry.json 的 sha256
  python tools/qisland_registry.py sign                # 维护者：用环境变量 QISLAND_REGISTRY_SEED（64 位十六进制）签名
  python tools/qisland_registry.py verify              # 只验签

哈希 / 签名规则和 Qisland 客户端一致（src/Qisland.Core/PluginMarket.cs）：
JSON 文件先去掉 UTF-8 BOM 和所有 \\r 再算；zip 用原始字节。签名是 Ed25519（RFC 8032），.sig 里是一行 Base64。
"""
import base64
import hashlib
import ipaddress
import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OFFICIAL_KEYS = ["1126a0023f533493f8120ec9be40e5831d387c63b520f6359fe4cb1b2f08933d"]
RAW_PREFIX = "https://raw.githubusercontent.com/Qiuizi/qisland-plugins/main/"

# ─────────── L2 安全边界（和客户端 CardLimits 一致） ───────────
MAX_DOMAINS, MAX_SOURCES, MAX_PRIMITIVES, MAX_MANIFEST = 3, 5, 8, 64 * 1024
MIN_INTERVAL = 60
PRIMITIVES = {"header", "metric", "progress", "sparkline", "badge_row", "action_row"}
SOURCE_TYPES = {"https", "static", "countdown", "local"}
FORBIDDEN_L2 = {"entry", "script", "exec", "command", "js", "javascript", "html"}  # 和客户端 PluginManifest 一致
CATEGORIES = {"productivity", "dev", "info", "theme"}
PRIVATE_SUFFIXES = (".localhost", ".local", ".internal", ".lan", ".home.arpa", ".intranet", ".corp", ".home", ".localdomain", ".test", ".invalid")
ID_RX = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
SEMVER_RX = re.compile(r"^v?\d{1,5}(\.\d{1,5}){0,3}(-[0-9A-Za-z.-]{1,32})?$")
LABEL_RX = re.compile(r"^(?!-)[a-z0-9-]{1,63}(?<!-)$")

# ─────────── Ed25519（RFC 8032 第 6 节参考实现） ───────────
P = 2 ** 255 - 19
L = 2 ** 252 + 27742317777372353535851937790883648493
D = -121665 * pow(121666, P - 2, P) % P
SQRT_M1 = pow(2, (P - 1) // 4, P)


def _add(a, b):
    A = (a[1] - a[0]) * (b[1] - b[0]) % P
    B = (a[1] + a[0]) * (b[1] + b[0]) % P
    C = 2 * a[3] * b[3] * D % P
    Dd = 2 * a[2] * b[2] % P
    E, F, G, H = B - A, Dd - C, Dd + C, B + A
    return (E * F % P, G * H % P, F * G % P, E * H % P)


def _mul(s, p):
    q = (0, 1, 1, 0)
    while s > 0:
        if s & 1:
            q = _add(q, p)
        p = _add(p, p)
        s >>= 1
    return q


def _eq(a, b):
    return (a[0] * b[2] - b[0] * a[2]) % P == 0 and (a[1] * b[2] - b[1] * a[2]) % P == 0


def _recover_x(y, sign):
    if y >= P:
        return None
    x2 = (y * y - 1) * pow(D * y * y + 1, P - 2, P) % P
    if x2 == 0:
        return None if sign else 0
    x = pow(x2, (P + 3) // 8, P)
    if (x * x - x2) % P != 0:
        x = x * SQRT_M1 % P
    if (x * x - x2) % P != 0:
        return None
    if (x & 1) != sign:
        x = P - x
    return x


_GY = 4 * pow(5, P - 2, P) % P
_GX = _recover_x(_GY, 0)
G = (_GX, _GY, 1, _GX * _GY % P)


def _compress(p):
    zi = pow(p[2], P - 2, P)
    x, y = p[0] * zi % P, p[1] * zi % P
    return int.to_bytes(y | ((x & 1) << 255), 32, "little")


def _decompress(s):
    if len(s) != 32:
        return None
    y = int.from_bytes(s, "little")
    sign = y >> 255
    y &= (1 << 255) - 1
    x = _recover_x(y, sign)
    if x is None:
        return None
    return (x, y, 1, x * y % P)


def _h(*parts):
    return int.from_bytes(hashlib.sha512(b"".join(parts)).digest(), "little")


def _expand(seed):
    h = hashlib.sha512(seed).digest()
    a = int.from_bytes(h[:32], "little")
    a &= (1 << 254) - 8
    a |= 1 << 254
    return a, h[32:]


def ed25519_public(seed):
    return _compress(_mul(_expand(seed)[0], G))


def ed25519_sign(seed, msg):
    a, prefix = _expand(seed)
    A = _compress(_mul(a, G))
    r = _h(prefix, msg) % L
    R = _compress(_mul(r, G))
    k = _h(R, A, msg) % L
    return R + int.to_bytes((r + k * a) % L, 32, "little")


def ed25519_verify(pub, msg, sig):
    if len(pub) != 32 or len(sig) != 64:
        return False
    A = _decompress(pub)
    R = _decompress(sig[:32])
    if A is None or R is None:
        return False
    s = int.from_bytes(sig[32:], "little")
    if s >= L:
        return False
    k = _h(sig[:32], pub, msg) % L
    return _eq(_mul(s, G), _add(R, _mul(k, A)))


# ─────────── 哈希规则 ───────────
def canonical(b: bytes) -> bytes:
    if b.startswith(b"\xef\xbb\xbf"):
        b = b[3:]
    return b.replace(b"\r", b"")


def package_sha256(b: bytes) -> str:
    return hashlib.sha256(b if b[:4] == b"PK\x03\x04" else canonical(b)).hexdigest()


# ─────────── 校验 ───────────
errors, warnings = [], []


def err(where, msg):
    errors.append(f"{where}: {msg}")


def valid_domain(host):
    host = (host or "").strip().rstrip(".").lower()
    if not 4 <= len(host) <= 253:
        return "长度不对"
    try:
        ipaddress.ip_address(host.strip("[]"))
        return "不允许 IP 字面量"
    except ValueError:
        pass
    if host == "localhost" or host.endswith(PRIVATE_SUFFIXES):
        return "不允许本机 / 内网名字"
    if "*" in host:
        return "不支持通配符"
    labels = host.split(".")
    if len(labels) < 2 or any(not LABEL_RX.match(x) for x in labels) or labels[-1].isdigit():
        return "不是合法域名"
    return None


def https_ok(url):
    m = re.match(r"^https://([^/:@?#]+)(/[^\s]*)?$", url or "")
    if not m:
        return "只允许 https://、默认端口、不带账号密码"
    return valid_domain(m.group(1))


def load_json(path: Path, where):
    raw = path.read_bytes()
    try:
        return json.loads(canonical(raw).decode("utf-8")), raw
    except Exception as e:  # noqa: BLE001
        err(where, f"不是合法 UTF-8 JSON：{e}")
        return None, raw


def check_plugin(path: Path):
    where = str(path.relative_to(ROOT))
    if path.stat().st_size > MAX_MANIFEST:
        err(where, "plugin.json 超过 64 KB")
        return None
    m, _ = load_json(path, where)
    if not isinstance(m, dict):
        err(where, "根节点要是对象")
        return None
    pid = m.get("id", "")
    if not ID_RX.match(str(pid)):
        err(where, "id 不合法")
    if path.parent.name != pid:
        err(where, f"目录名 {path.parent.name} 要和 id {pid} 一致")
    if not str(m.get("name", "")).strip():
        err(where, "缺少 name")
    if not SEMVER_RX.match(str(m.get("version", ""))):
        err(where, "version 要像 1.0.0")
    tier = m.get("tier", "declarative")
    if tier == "process":
        warnings.append(f"{where}: 高级插件（L3）会运行本机程序，CI 只做格式检查，必须由维护者人工审核源码后才能合并")
        return m
    if tier != "declarative":
        err(where, "tier 只能是 declarative 或 process")
    for k in FORBIDDEN_L2 & set(m.keys()):
        err(where, f"L2 声明式卡片不能带 {k}（不运行任何代码）")
    perms = m.get("permissions", {}) or {}
    if not isinstance(perms, dict):
        err(where, "permissions 要是对象")
        perms = {}
    domains = [str(d).strip().rstrip(".").lower() for d in perms.get("domains", []) or []]
    if len(domains) > MAX_DOMAINS:
        err(where, f"最多 {MAX_DOMAINS} 个域名")
    for d in domains:
        why = valid_domain(d)
        if why:
            err(where, f"域名 {d} 不允许：{why}")
    sources = m.get("sources", {}) or {}
    if not isinstance(sources, dict) or len(sources) > MAX_SOURCES:
        err(where, f"sources 要是对象且最多 {MAX_SOURCES} 个")
        sources = {}
    for sid, s in sources.items():
        if not isinstance(s, dict) or s.get("type") not in SOURCE_TYPES:
            err(where, f"数据源 {sid} 的 type 只能是 {' / '.join(sorted(SOURCE_TYPES))}")
            continue
        if s["type"] == "https":
            url = str(s.get("url", ""))
            mm = re.match(r"^https://([^/:@?#{}]+)", url)
            if not mm:
                err(where, f"数据源 {sid} 要以 https://固定域名 开头")
            elif mm.group(1).lower() not in domains:
                err(where, f"数据源 {sid} 的域名 {mm.group(1)} 不在 permissions.domains 白名单里")
            iv = s.get("interval_seconds", 300)
            if isinstance(iv, int) and iv < MIN_INTERVAL:
                warnings.append(f"{where}: 数据源 {sid} 的间隔 {iv} 秒会被客户端钳到 {MIN_INTERVAL} 秒")
    card = m.get("card", [])
    if not isinstance(card, list) or not 1 <= len(card) <= MAX_PRIMITIVES:
        err(where, f"card 要有 1~{MAX_PRIMITIVES} 个组件")
        card = []
    for i, c in enumerate(card):
        if not isinstance(c, dict) or c.get("type") not in PRIMITIVES:
            err(where, f"card[{i}] 的 type 只能是 {' / '.join(sorted(PRIMITIVES))}")
        if isinstance(c, dict) and c.get("type") == "action_row":
            for a in c.get("actions", []) or []:
                if not isinstance(a, dict) or a.get("kind", "open_url") != "open_url":
                    continue
                url = str(a.get("url", ""))
                if not url.startswith("https://") or ("{{" not in url and https_ok(url)):
                    err(where, f"card[{i}] 的按钮只能打开 https 链接")
    return m


def check_theme(path: Path):
    where = str(path.relative_to(ROOT))
    if path.stat().st_size > 32 * 1024:
        err(where, "主题包超过 32 KB")
        return None
    t, _ = load_json(path, where)
    if not isinstance(t, dict):
        err(where, "根节点要是对象")
        return None
    if t.get("format") != "qisland-theme/1":
        err(where, 'format 要是 "qisland-theme/1"')
    if not str(t.get("name", "")).strip():
        err(where, "缺少 name")
    return t


def parse_date(s):
    return isinstance(s, str) and re.match(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", s)


def check_registry(manifests, themes):
    where = "registry.json"
    reg, _ = load_json(ROOT / "registry.json", where)
    if not isinstance(reg, dict):
        return
    if reg.get("schema_version") != 1:
        err(where, "schema_version 要是 1")
    if not parse_date(reg.get("updated_at")):
        err(where, "updated_at 要是 ISO 8601（如 2026-11-28T00:00:00Z）")
    seen = set()
    for i, it in enumerate(reg.get("items", [])):
        w = f"{where} items[{i}]"
        if not isinstance(it, dict):
            err(w, "不是对象")
            continue
        iid = it.get("id", "")
        w = f"{where} {iid}"
        if not ID_RX.match(str(iid)) or iid in seen:
            err(w, "id 不合法或重复")
        seen.add(iid)
        for k in ("name", "version", "tier", "category", "author", "description", "download_url", "sha256"):
            if not str(it.get(k, "")).strip():
                err(w, f"缺少 {k}")
        if it.get("tier") not in ("declarative", "process"):
            err(w, "tier 只能是 declarative / process")
        if it.get("category") not in CATEGORIES:
            err(w, f"category 只能是 {' / '.join(sorted(CATEGORIES))}")
        if it.get("category") == "theme" and it.get("tier") != "declarative":
            err(w, "主题包只能是 declarative")
        if not re.match(r"^[0-9a-f]{64}$", str(it.get("sha256", ""))):
            err(w, "sha256 要是 64 位小写十六进制")
        if it.get("min_app_version") and not SEMVER_RX.match(str(it["min_app_version"])):
            err(w, "min_app_version 不合法")
        url = str(it.get("download_url", ""))
        why = https_ok(url)
        if why:
            err(w, f"download_url {why}")
            continue
        if url.startswith(RAW_PREFIX):
            local = ROOT / url[len(RAW_PREFIX):]
            if not local.is_file():
                err(w, f"download_url 指向的 {url[len(RAW_PREFIX):]} 不在仓库里")
                continue
            got = package_sha256(local.read_bytes())
            if got != it.get("sha256"):
                err(w, f"sha256 和仓库里的文件不一致（文件是 {got}），运行 fix-hashes")
            src = manifests.get(iid) if it.get("category") != "theme" else themes.get(iid)
            if src is None:
                err(w, "索引项在 plugins/ 或 themes/ 里找不到对应文件")
            elif it.get("category") != "theme":
                if str(src.get("version")) != str(it.get("version")):
                    err(w, f"版本 {it.get('version')} 和 plugin.json 的 {src.get('version')} 不一致")
                want = sorted((it.get("permissions") or {}).get("domains", []) or [])
                have = sorted(((src.get("permissions") or {}).get("domains", []) or []))
                if want != have:
                    err(w, f"permissions.domains {want} 要和 plugin.json 的 {have} 完全一致")
                if src.get("tier", "declarative") != it.get("tier"):
                    err(w, "tier 和 plugin.json 不一致")


def check_revoked():
    where = "revoked.json"
    rv, _ = load_json(ROOT / "revoked.json", where)
    if not isinstance(rv, dict):
        return
    if rv.get("schema_version") != 1 or not parse_date(rv.get("updated_at")):
        err(where, "schema_version 要是 1，updated_at 要是 ISO 8601")
    for i, e in enumerate(rv.get("items", [])):
        if not isinstance(e, dict) or not (e.get("id") or e.get("sha256")):
            err(where, f"items[{i}] 至少要写 id 或 sha256")
        if isinstance(e, dict) and not str(e.get("reason", "")).strip():
            err(where, f"items[{i}] 要写 reason，客户端会原样展示给用户")


def check_sigs(strict):
    for name in ("registry.json", "revoked.json"):
        sig = ROOT / (name + ".sig")
        if sig.is_file() and verify_file(ROOT / name, sig):
            continue
        what = "缺少 .sig" if not sig.is_file() else ".sig 和当前内容对不上"
        if strict:
            err(name, what + "，维护者需要重新签名")
        else:
            warnings.append(f"{name}: {what}（合并后由维护者签名即可，PR 不需要签）")


def verify_file(path: Path, sig: Path):
    try:
        s = sig.read_text("utf-8").strip()
        if s.lower().startswith("ed25519:"):
            s = s[8:].strip()
        raw = base64.b64decode(s, validate=True)
    except Exception:  # noqa: BLE001
        return False
    msg = canonical(path.read_bytes())
    return any(ed25519_verify(bytes.fromhex(k), msg, raw) for k in OFFICIAL_KEYS)


def cmd_validate(strict):
    manifests, themes = {}, {}
    for p in sorted((ROOT / "plugins").glob("*/plugin.json")):
        m = check_plugin(p)
        if isinstance(m, dict):
            manifests[m.get("id")] = m
    for p in sorted((ROOT / "themes").glob("*.json")):
        t = check_theme(p)
        if isinstance(t, dict):
            themes[p.stem] = t
    check_registry(manifests, themes)
    check_revoked()
    check_sigs(strict)
    for w in warnings:
        print("::warning::" + w if os.environ.get("GITHUB_ACTIONS") else "警告  " + w)
    for e in errors:
        print("::error::" + e if os.environ.get("GITHUB_ACTIONS") else "错误  " + e)
    print(f"{len(manifests)} 个插件、{len(themes)} 个主题包；{len(errors)} 个错误，{len(warnings)} 个警告")
    return 1 if errors else 0


def cmd_fix_hashes():
    path = ROOT / "registry.json"
    reg = json.loads(canonical(path.read_bytes()).decode("utf-8"))
    for it in reg.get("items", []):
        url = it.get("download_url", "")
        if url.startswith(RAW_PREFIX) and (ROOT / url[len(RAW_PREFIX):]).is_file():
            it["sha256"] = package_sha256((ROOT / url[len(RAW_PREFIX):]).read_bytes())
    path.write_text(json.dumps(reg, ensure_ascii=False, indent=2) + "\n", "utf-8", newline="\n")
    print("已按仓库文件重写 sha256；记得改 updated_at 再签名")
    return 0


def cmd_sign():
    seed_hex = os.environ.get("QISLAND_REGISTRY_SEED", "").strip()
    if not re.match(r"^[0-9a-fA-F]{64}$", seed_hex):
        print("要先设置环境变量 QISLAND_REGISTRY_SEED（32 字节私钥种子的十六进制）")
        return 2
    seed = bytes.fromhex(seed_hex)
    if ed25519_public(seed).hex() not in OFFICIAL_KEYS:
        print("这把私钥对应的公钥不在 OFFICIAL_KEYS 里，客户端不会认")
        return 2
    for name in ("registry.json", "revoked.json"):
        sig = base64.b64encode(ed25519_sign(seed, canonical((ROOT / name).read_bytes()))).decode()
        (ROOT / (name + ".sig")).write_text(sig + "\n", "ascii", newline="\n")
        print(f"已签名 {name}")
    return 0


def cmd_verify():
    bad = [n for n in ("registry.json", "revoked.json") if not (ROOT / (n + ".sig")).is_file() or not verify_file(ROOT / n, ROOT / (n + ".sig"))]
    for n in bad:
        print(f"验签失败：{n}")
    if not bad:
        print("registry.json / revoked.json 签名有效")
    return 1 if bad else 0


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    cmd = argv[0]
    if cmd == "validate":
        return cmd_validate("--strict-sig" in argv)
    if cmd == "hash" and len(argv) == 2:
        print(package_sha256(Path(argv[1]).read_bytes()))
        return 0
    if cmd == "fix-hashes":
        return cmd_fix_hashes()
    if cmd == "sign":
        return cmd_sign()
    if cmd == "verify":
        return cmd_verify()
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
