#!/usr/bin/env bash
# 把当前构建出的 tarball 装进夹具 profile。
#
# 必要性：pnpm 会按 tarball 路径缓存 file: 依赖的 integrity，重新打包后同名路径
# 不会被重新解析（实测会继续用旧的 lib/）。因此这里显式移除旧 lock 条目与模块，
# 再让 dsh plugin add 重新安装，确保夹具里跑的是**当前**构建产物。
set -euo pipefail

plugin_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
repo_root="$(cd "$plugin_root/../.." && pwd)"
fixture_root="${DSH_ATTACH_FIXTURE_ROOT:-$repo_root/artifacts/fixture}"
dsh_home="$fixture_root/dsh-home"
profile="${DSH_ATTACH_FIXTURE_PROFILE:-dsh-attachments-fixture}"
dsh_bin="${DSH_BIN:-$HOME/.npm-global/bin/dsh}"
tarball="$plugin_root/pack/shxtmaker-dsh-remote-attachments-0.1.0.tgz"

log() { printf '[install-plugin] %s\n' "$*"; }
fail() { printf '[install-plugin] FAIL: %s\n' "$*" >&2; exit 1; }

[[ -f "$tarball" ]] || fail "缺少 tarball：$tarball（请先 pnpm run build && pnpm run test:pack）"

profile_dir="$dsh_home/profiles/$profile"
[[ -f "$profile_dir/package.json" ]] || fail "夹具 profile 不存在：$profile_dir（请先运行 setup.sh）"

log "移除旧 lock 条目与已装模块，强制重新解析 tarball"
node - "$profile_dir" <<'NODE'
const fs = require('node:fs')
const path = require('node:path')
const dir = process.argv[2]
const lockPath = path.join(dir, 'pnpm-lock.yaml')
let text = fs.readFileSync(lockPath, 'utf8')
const lines = text.split('\n')
const out = []
let skipping = false
for (const line of lines) {
  if (/^  .*dsh-remote-attachments@/.test(line)) { skipping = true; continue }
  if (skipping) {
    if (/^  \S/.test(line)) skipping = false
    else continue
  }
  out.push(line)
}
fs.writeFileSync(lockPath, out.join('\n'))
NODE
rm -rf "$profile_dir/node_modules/@shxtmaker" "$profile_dir/node_modules/.pnpm/"*dsh-remote-attachments* 2>/dev/null || true

DSH_HOME="$dsh_home" "$dsh_bin" plugin --profile "$profile" add "$tarball" >/dev/null || fail "安装 tarball 失败"

installed="$profile_dir/node_modules/@shxtmaker/dsh-remote-attachments"
[[ -f "$installed/lib/host/upload-hook.js" ]] || fail "夹具内缺少 upload-hook.js（安装的可能是旧产物）"
log "已安装并校验：$installed"
log "install-plugin: OK"
