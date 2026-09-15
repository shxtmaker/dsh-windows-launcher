#!/usr/bin/env bash
# D22 干净安装：在夹具私有 DSH_HOME 里新建一个**此前不存在**的 profile，
# 只装固定的全家桶（@linxin666/dsh-web-all，版本取自 compatibility-lock.json）与候选 tarball。
#
# 为什么必须新建 profile 而不是复用夹具 profile：
#   "交付包在干净的全家桶环境里能跑" 与 "source link / 夹具残留状态能跑" 是两件事。
#   本脚本因此拒绝在已存在的目录上安装（干净性由 D22 门禁在调用前后各断言一次），
#   并复用 install-plugin.sh 的强制重解析（pnpm 会按 tarball 路径缓存 file: 依赖的 integrity，
#   不清旧条目就会静默沿用旧 lib/ —— 这正是 D22 要排除的失效模式）。
#
# 只写夹具自有目录；绝不触碰 $HOME/.dsh 或其它 DSH_HOME。
set -euo pipefail

plugin_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
repo_root="$(cd "$plugin_root/../.." && pwd)"
fixture_root="${DSH_ATTACH_FIXTURE_ROOT:-$repo_root/artifacts/fixture}"
dsh_home="$fixture_root/dsh-home"
profile="${DSH_D22_PROFILE:-dsh-attachments-d22}"
port="${DSH_D22_PORT:-3096}"
stub_port="${DSH_D22_STUB_PORT:-3902}"
dsh_bin="${DSH_BIN:-$HOME/.npm-global/bin/dsh}"
lock="$plugin_root/tests/fixtures/compatibility-lock.json"
tarball="${DSH_D22_TARBALL:-$plugin_root/pack/shxtmaker-dsh-remote-attachments-0.1.0.tgz}"
evidence_dir="$fixture_root/evidence"

log() { printf '[d22-clean-install] %s\n' "$*"; }
fail() { printf '[d22-clean-install] FAIL: %s\n' "$*" >&2; exit 1; }

[[ -x "$dsh_bin" ]] || fail "未找到 dsh CLI：$dsh_bin"
[[ -f "$lock" ]] || fail "缺少版本锁定清单：$lock"
[[ -d "$dsh_home" ]] || fail "夹具 DSH_HOME 不存在：$dsh_home（请先运行 tests/fixtures/setup.sh）"
[[ -f "$dsh_home/profiles/${DSH_ATTACH_FIXTURE_PROFILE:-dsh-attachments-fixture}/package.json" ]] \
  || fail "夹具 profile 不存在（请先运行 tests/fixtures/setup.sh）"
[[ -f "$tarball" ]] || fail "缺少候选 tarball：$tarball（请先 pnpm run build && pnpm run test:pack）"

profile_dir="$dsh_home/profiles/$profile"
[[ -e "$profile_dir" ]] && fail "D22 profile 已存在，干净安装要求从零开始：$profile_dir"

all_version="$(node -e "const d=require('$lock');const p=d.packages.find(x=>x.name==='@linxin666/dsh-web-all');process.stdout.write(p.version)")"
tarball_sha256="$(sha256sum "$tarball" | cut -d' ' -f1)"
lan_address="$(cat "$fixture_root/lan-address.txt" 2>/dev/null || echo 127.0.0.1)"
started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

mkdir -p "$evidence_dir"

log "repo=$repo_root"
log "dsh_home=$dsh_home profile=$profile port=$port stub=$stub_port"
log "tarball=$tarball sha256=$tarball_sha256"
log "固定全家桶：@linxin666/dsh-web-all@$all_version"

# ---- 1) 从 web 模板新建 profile（此前不存在） ----
log "由 web 模板创建 D22 profile"
DSH_HOME="$dsh_home" "$dsh_bin" --profile "$profile" --from-default-profile web --dump-config \
  > "$evidence_dir/d22-default-config.yml" 2>&1 || fail "创建 D22 profile 失败"
[[ -f "$profile_dir/package.json" ]] || fail "D22 profile 未创建：$profile_dir"

# ---- 2) 登记 bundles 与固定 allowBuilds（与 setup.sh 主 profile 同款） ----
log "登记 bundles（含固定的全家桶与本插件）"
node - "$profile_dir" "$all_version" <<'NODE'
const fs = require('node:fs')
const path = require('node:path')
const dir = process.argv[2]
const allVersion = process.argv[3]
const pkgPath = path.join(dir, 'package.json')
const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'))
pkg.dsh ??= {}
pkg.dsh.profile ??= {}
pkg.dsh.profile.bundles = [
  '@deepseek-ai/dsh-base',
  '@deepseek-ai/dsh-web-app',
  '@linxin666/dsh-web-all',
  '@shxtmaker/dsh-remote-attachments'
]
pkg.dependencies ??= {}
pkg.dependencies['@linxin666/dsh-web-all'] = allVersion
delete pkg.pnpm
fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n')

// pnpm 11 从 pnpm-workspace.yaml 读取构建许可，不再读 package.json 的 pnpm 字段。
const wsPath = path.join(dir, 'pnpm-workspace.yaml')
let ws = fs.readFileSync(wsPath, 'utf8')
ws = ws.split('\n').filter((line) => !/^\s*(onlyBuiltDependencies|allowBuilds|  - (cloudflared|cpu-features|node-pty|ssh2))\s*$/.test(line)).join('\n')
ws = ws.replace(/\n{3,}/g, '\n\n').trimEnd()
ws += [
  '',
  '# D22 干净安装夹具：固定允许这些依赖运行安装脚本，避免 pnpm 以 ERR_PNPM_IGNORED_BUILDS 中止。',
  'allowBuilds:',
  '  cloudflared: true',
  '  cpu-features: true',
  '  node-pty: true',
  '  ssh2: true',
  ''
].join('\n')
fs.writeFileSync(wsPath, ws)
NODE

log "安装固定版本依赖（pnpm）"
( cd "$profile_dir" && pnpm install ) || fail "D22 profile 依赖安装失败"

# ---- 3) 夹具 patch：绑定 0.0.0.0（LAN 远程模式）、锁死 /api 栅栏、模型侧指向本地 stub ----
cat > "$profile_dir/cordis.patch.yml" <<YAML
# D22 干净安装 patch（由 tests/fixtures/d22-clean-install.sh 生成，可重复覆盖）。
# 与主夹具 profile 逐字段同款，只有端口不同（${port}）：D22 要证明的是"候选 tarball 在
# 干净的全家桶 profile 里、经真实 /remote 配对通道跑通闭环"，因此必须是非 loopback 绑定，
# 否则 /remote 改写层不会注入，测到的就不是真实远程路径。
- id: webserver
  name: '@deepseek-ai/dsh-host-webserver'
  config:
    host: '0.0.0.0'
    port: ${port}
    compression: gzip
    compressionLevel: 1
    compressionThresholdBytes: 1024

# /api fence lockdown（与生产 profile 的加固同款）：LAN 访问一律走带配对的 /remote 通道。
- id: connection
  name: '@deepseek-ai/dsh-client-connection'
  config:
    trustedHosts: []

# 模型侧离线确定：真实 Harness 的 agent 循环 + 真实工具执行，provider 指向本地 stub。
- id: llm-deepseek
  name: '@deepseek-ai/dsh-llm-deepseek'
  config:
    baseURL: 'http://127.0.0.1:${stub_port}'
    apiKeyEnv: DEEPSEEK_API_KEY
YAML

# ---- 4) 安装候选 tarball（复用 install-plugin.sh：强制重新解析 file: 依赖） ----
log "安装候选 tarball 到 D22 profile"
DSH_ATTACH_FIXTURE_PROFILE="$profile" bash "$plugin_root/tests/fixtures/install-plugin.sh" \
  || fail "D22 profile 安装候选 tarball 失败"

installed="$profile_dir/node_modules/@shxtmaker/dsh-remote-attachments"
[[ -f "$installed/lib/client.js" ]] || fail "安装后缺少 lib/client.js：$installed"
[[ -f "$installed/lib/host.js" ]] || fail "安装后缺少 lib/host.js：$installed"

# ---- 5) 安装回执（供 D22 门禁逐条核对；路径用仓库相对形式，避免绝对家目录进入证据） ----
node - "$fixture_root" "$profile" "$port" "$stub_port" "$all_version" "$tarball" "$tarball_sha256" "$lan_address" "$started_at" <<'NODE'
const fs = require('node:fs')
const path = require('node:path')
const [fixtureRoot, profile, port, stubPort, allVersion, tarball, tarballSha256, lanAddress, startedAt] = process.argv.slice(2)
const repoRoot = path.resolve(fixtureRoot, '../..')
const rel = (value) => path.relative(repoRoot, value)
const receipt = {
  schemaVersion: 1,
  task: 'D22',
  purpose: 'clean-install-receipt',
  profile,
  profileDir: rel(path.join(fixtureRoot, 'dsh-home/profiles', profile)),
  privateDshHome: rel(path.join(fixtureRoot, 'dsh-home')),
  bundles: ['@deepseek-ai/dsh-base', '@deepseek-ai/dsh-web-app', '@linxin666/dsh-web-all', '@shxtmaker/dsh-remote-attachments'],
  fixedBundle: { name: '@linxin666/dsh-web-all', version: allVersion },
  tarball: rel(tarball),
  tarballSha256,
  tarballSpecifier: `file:${tarball}`,
  port: Number(port),
  stubPort: Number(stubPort),
  lanAddress,
  startedAt,
  finishedAt: new Date().toISOString(),
  steps: [
    'web 模板创建 profile（--from-default-profile web）',
    'package.json 登记 bundles 与固定 @linxin666/dsh-web-all 版本',
    'pnpm install（固定版本依赖）',
    '写入 cordis.patch.yml（0.0.0.0 / trustedHosts [] / 本地 provider stub）',
    'install-plugin.sh 安装候选 tarball（清旧 lock 条目与模块后重新解析）'
  ]
}
fs.writeFileSync(path.join(fixtureRoot, 'evidence/d22-clean-install.json'), JSON.stringify(receipt, null, 2) + '\n')
NODE

log "d22-clean-install: OK（profile=$profile_dir）"
