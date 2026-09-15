#!/usr/bin/env bash
# D05 HTTPS 夹具准备：生成测试 CA 与私网地址服务器证书。
#
# 约束：
#  - 证书必须包含非 loopback 私网地址的 SAN（判据要经该地址访问），
#    并附带 127.0.0.1 便于本机对照。
#  - 私钥只留在夹具自有的 artifacts/fixture/tls 目录内，权限 600，绝不入库。
#  - 不使用全局忽略证书校验；信任由调用方在专用上下文显式指定该 CA。
set -euo pipefail

plugin_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
repo_root="$(cd "$plugin_root/../.." && pwd)"
fixture_root="${DSH_ATTACH_FIXTURE_ROOT:-$repo_root/artifacts/fixture}"
tls_dir="$fixture_root/tls"

log() { printf '[tls-setup] %s\n' "$*"; }
fail() { printf '[tls-setup] FAIL: %s\n' "$*" >&2; exit 1; }

command -v openssl >/dev/null 2>&1 || fail '缺少 openssl，无法生成测试证书'

detect_lan_address() {
  ip -4 -o addr show scope global 2>/dev/null \
    | awk '{print $4}' | cut -d/ -f1 \
    | grep -E '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)' \
    | head -1
}
lan_address="${DSH_ATTACH_LAN_ADDRESS:-$(detect_lan_address)}"
[[ -n "$lan_address" ]] || fail '未探测到非 loopback 私网 IPv4，HTTPS 判据需要它作为 SAN'

mkdir -p "$tls_dir"
rm -f "$tls_dir"/*.pem "$tls_dir"/*.srl "$tls_dir"/*.csr

# CA
openssl req -x509 -newkey rsa:2048 -sha256 -days 2 -nodes \
  -keyout "$tls_dir/ca-key.pem" -out "$tls_dir/ca-cert.pem" \
  -subj "/CN=DSH Attachments Test CA/O=dsh-remote-attachments-fixture" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" >/dev/null 2>&1 \
  || fail '生成测试 CA 失败'

# 服务器证书（SAN 覆盖私网地址与 loopback）
openssl req -newkey rsa:2048 -sha256 -nodes \
  -keyout "$tls_dir/server-key.pem" -out "$tls_dir/server.csr" \
  -subj "/CN=$lan_address/O=dsh-remote-attachments-fixture" >/dev/null 2>&1 \
  || fail '生成服务器 CSR 失败'

cat > "$tls_dir/server.ext" <<EXT
basicConstraints=CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=IP:$lan_address,IP:127.0.0.1,DNS:localhost
EXT

openssl x509 -req -in "$tls_dir/server.csr" -CA "$tls_dir/ca-cert.pem" \
  -CAkey "$tls_dir/ca-key.pem" -CAcreateserial -days 2 -sha256 \
  -extfile "$tls_dir/server.ext" -out "$tls_dir/server-cert.pem" >/dev/null 2>&1 \
  || fail '签发服务器证书失败'

chmod 600 "$tls_dir"/*-key.pem
chmod 644 "$tls_dir"/*-cert.pem
rm -f "$tls_dir/server.csr"

# 自检：证书链可被该 CA 验证，且 SAN 含私网地址
openssl verify -CAfile "$tls_dir/ca-cert.pem" "$tls_dir/server-cert.pem" >/dev/null 2>&1 \
  || fail '服务器证书无法被测试 CA 验证'
openssl x509 -in "$tls_dir/server-cert.pem" -noout -ext subjectAltName \
  | grep -q "$lan_address" || fail '服务器证书 SAN 不含私网地址'

printf '%s\n' "$lan_address" > "$fixture_root/lan-address.txt"

log "测试 CA：$tls_dir/ca-cert.pem"
log "服务器证书：$tls_dir/server-cert.pem（SAN=$lan_address,127.0.0.1,localhost）"
log "tls-setup: OK"
