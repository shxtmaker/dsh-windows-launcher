#!/usr/bin/env bash
# D04 夹具准备：在私有 DSH_HOME 中创建 fixture profile，安装固定的全家桶与本插件 tarball。
#
# 设计约束：
#  - 只写夹具自有目录（默认 artifacts/fixture/），绝不触碰 $HOME/.dsh 或生产 profile
#  - 版本全部取自 tests/fixtures/compatibility-lock.json，不使用浮动 latest
#  - 依赖 D03 的 tarball；缺失时明确失败，不静默改用 source link
set -euo pipefail

plugin_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
repo_root="$(cd "$plugin_root/../.." && pwd)"
fixture_root="${DSH_ATTACH_FIXTURE_ROOT:-$repo_root/artifacts/fixture}"
dsh_home="$fixture_root/dsh-home"
profile="${DSH_ATTACH_FIXTURE_PROFILE:-dsh-attachments-fixture}"
lock="$plugin_root/tests/fixtures/compatibility-lock.json"
dsh_bin="${DSH_BIN:-$HOME/.npm-global/bin/dsh}"

log() { printf '[setup] %s\n' "$*"; }
fail() { printf '[setup] FAIL: %s\n' "$*" >&2; exit 1; }

[[ -x "$dsh_bin" ]] || fail "未找到 dsh CLI：$dsh_bin"
[[ -f "$lock" ]] || fail "缺少版本锁定清单：$lock"

read_json() { node -e "const d=require('$lock');process.stdout.write(String(eval('d.'+process.argv[1])))" "$1"; }

harness_version="$(read_json harness.version)"
all_spec="$(read_json "packages.find(p=>p.name==='@linxin666/dsh-web-all').name")@$(read_json "packages.find(p=>p.name==='@linxin666/dsh-web-all').version")"
tarball_rel="$(read_json "packages.find(p=>p.name==='@shxtmaker/dsh-remote-attachments').tarball")"
tarball="$repo_root/$tarball_rel"

log "repo=$repo_root"
log "dsh_home=$dsh_home"
log "profile=$profile"
log "harness=$harness_version all=$all_spec"

[[ -f "$tarball" ]] || fail "缺少 D03 tarball：$tarball（请先在插件目录执行 pnpm run build && pnpm run test:pack）"

installed_dsh="$("$dsh_bin" --version 2>/dev/null | head -1)"
[[ "$installed_dsh" == "$harness_version" ]] || fail "dsh CLI 版本不符：实际 $installed_dsh，锁定 $harness_version"

# 只清理夹具自有目录；不触碰任何其他 profile。
log "清理夹具自有目录"
rm -rf "$dsh_home" "$fixture_root/workspace" "$fixture_root/evidence"
mkdir -p "$dsh_home" "$fixture_root/workspace" "$fixture_root/evidence"

log "由 web 模板创建 fixture profile"
DSH_HOME="$dsh_home" "$dsh_bin" --profile "$profile" --from-default-profile web --dump-config > "$fixture_root/evidence/profile-default-config.yml" 2>&1 \
  || fail "创建 fixture profile 失败"

profile_dir="$dsh_home/profiles/$profile"
[[ -f "$profile_dir/package.json" ]] || fail "profile 未创建：$profile_dir"

log "登记 bundles（含本插件）并固定 allowBuilds"
node - "$profile_dir" <<'NODE'
const fs = require('node:fs')
const path = require('node:path')
const dir = process.argv[2]
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
pkg.dependencies['@linxin666/dsh-web-all'] = '0.3.20'
delete pkg.pnpm
fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n')

// pnpm 11 从 pnpm-workspace.yaml 读取构建许可，不再读 package.json 的 pnpm 字段。
const wsPath = path.join(dir, 'pnpm-workspace.yaml')
let ws = fs.readFileSync(wsPath, 'utf8')
ws = ws.split('\n').filter((line) => !/^\s*(onlyBuiltDependencies|allowBuilds|  - (cloudflared|cpu-features|node-pty|ssh2))\s*$/.test(line)).join('\n')
ws = ws.replace(/\n{3,}/g, '\n\n').trimEnd()
ws += [
  '',
  '# 夹具：固定允许这些依赖运行安装脚本，避免 pnpm 以 ERR_PNPM_IGNORED_BUILDS 中止。',
  'allowBuilds:',
  '  cloudflared: true',
  '  cpu-features: true',
  '  node-pty: true',
  '  ssh2: true',
  ''
].join('\n')
fs.writeFileSync(wsPath, ws)
NODE

log "安装固定版本的依赖（pnpm，frozen 由 profile 锁文件保证）"
( cd "$profile_dir" && pnpm install ) || fail "profile 依赖安装失败"

# 非 loopback 绑定：D05 需要真实 remote 模式，loopback 不能代替。
#
# 两个约束共同决定了做法：
#   1) CLI 的 `--host 0.0.0.0` 被 dsh-web-app 显式拒绝（安全考虑），因此不能用命令行参数；
#   2) webserver 的 config.host schema 只接受 '127.0.0.1' | '0.0.0.0'，不接受具体网卡地址。
# 所以夹具在 profile patch 中写入 host: '0.0.0.0'（与生产 profile 的 managed block 同款），
# 由 webserver 绑定全网卡。这只在一次性测试夹具里使用，且服务随时可被 fixture:down 停止。
# 探测到的私网地址仅用于**访问**（凭据断言走该地址而非 loopback）。
detect_lan_address() {
  ip -4 -o addr show scope global 2>/dev/null \
    | awk '{print $4}' | cut -d/ -f1 \
    | grep -E '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)' \
    | head -1
}
lan_address="${DSH_ATTACH_LAN_ADDRESS:-$(detect_lan_address)}"
if [[ -z "$lan_address" ]]; then
  log "警告：未探测到非 loopback 私网 IPv4，夹具将只绑定 loopback（D05 的非 loopback 判据会失败）"
  lan_address="127.0.0.1"
fi
log "webserver 绑定 0.0.0.0；访问地址（非 loopback）= $lan_address"

cat > "$profile_dir/cordis.patch.yml" <<YAML
# 夹具 patch 层（由 tests/fixtures/setup.sh 生成，可重复覆盖）。
# 只做一件事：把 webserver 绑定到 0.0.0.0，使 /remote 与配对走真实的 LAN 模式
# 而不是 loopback 短路。生产 profile 不受影响（本文件只在夹具 profile 内）。
- id: webserver
  name: '@deepseek-ai/dsh-host-webserver'
  config:
    host: '0.0.0.0'
    port: ${DSH_ATTACH_FIXTURE_PORT:-3099}
    compression: gzip
    compressionLevel: 1
    compressionThresholdBytes: 1024

# /api fence lockdown（与生产 profile 的加固同款，D05 必需）：
# 绑定 0.0.0.0 会让 SDK 在 /api 的 Host 栅栏里自动信任私网 IP 字面量，
# 于是未配对的 LAN 客户端可以直连整套桌面 API。生产 profile 用下面这一行把
# trustedHosts 覆盖为 []，要求 LAN 访问一律走带配对的 /remote 通道。
# 夹具必须复现这一加固，否则 D05 的"未配对不能读取工作区"判据会因夹具放水而失败。
# 同 id 的 patch 行整体替换 bundle 层的 config，因此这里只重述需要覆盖的字段。
- id: connection
  name: '@deepseek-ai/dsh-client-connection'
  config:
    trustedHosts: []

# D09 需要"真实 Harness 发送 + 真实工具读回"，模型侧必须离线确定（与无头 profile 同款）。
# 不指向 stub 时 web profile 会用默认的线上 DeepSeek 端点，判据既不确定也依赖真实账户。
- id: llm-deepseek
  name: '@deepseek-ai/dsh-llm-deepseek'
  config:
    baseURL: 'http://127.0.0.1:3901'
    apiKeyEnv: DEEPSEEK_API_KEY
YAML
printf '%s\n' "$lan_address" > "$fixture_root/lan-address.txt"

log "安装插件 tarball 到 profile（含强制重新解析，避免用旧产物）"
# pnpm 按 tarball 路径缓存 file: 依赖的 integrity，重新打包后同名路径不会被重新解析。
# 因此统一走 install-plugin.sh：它先清掉旧 lock 条目与模块，再安装并校验 upload-hook.js。
bash "$plugin_root/tests/fixtures/install-plugin.sh" || fail "安装本插件 tarball 失败"

log "设置 completions 以外的唯一可写项：无（profile 目录由 pnpm 管理）"
# ---- D06：无头 profile（headless 模板 + 本地 provider stub） ----
# 判据需要真实 Harness 的 agent 循环执行工具，因此额外准备一个 headless profile。
# 它只挂 base + headless，不挂 Host/HTTP/浏览器。
headless_profile="${DSH_ATTACH_HEADLESS_PROFILE:-dsh-attachments-headless}"
headless_dir="$dsh_home/profiles/$headless_profile"

log "准备无头 profile：$headless_profile"
DSH_HOME="$dsh_home" "$dsh_bin" --profile "$headless_profile" --from-default-profile headless --dump-config \
  > "$fixture_root/evidence/headless-default-config.yml" 2>&1 \
  || fail "创建无头 profile 失败"

node - "$headless_dir" <<'NODE'
const fs = require('node:fs')
const path = require('node:path')
const dir = process.argv[2]
const pkgPath = path.join(dir, 'package.json')
const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'))
pkg.dependencies = {
  '@deepseek-ai/dsh-base': '0.1.5-rc.1',
  '@deepseek-ai/dsh-headless': '0.1.5-rc.1'
}
fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n')

// pnpm 11 从 pnpm-workspace.yaml 读取构建许可。
fs.writeFileSync(
  path.join(dir, 'pnpm-workspace.yaml'),
  [
    'packages:',
    '  - .',
    '',
    'nodeLinker: hoisted',
    'autoInstallPeers: false',
    '',
    'allowBuilds:',
    '  cloudflared: true',
    '  cpu-features: true',
    '  node-pty: true',
    '  ssh2: true',
    "  '@deepseek-ai/dsh-subprocess-local': true",
    "  '@google/genai': true",
    '  koffi: true',
    '  protobufjs: true',
    ''
  ].join('\n')
)

// provider 指向本地确定性 stub：模型侧离线，工具仍由真实 Harness 执行。
fs.writeFileSync(
  path.join(dir, 'cordis.patch.yml'),
  [
    '# D06 无头夹具：headless 模板 + 本地确定性 provider stub（由 setup.sh 生成）。',
    '# headless 不挂 Host/HTTP/浏览器，只跑一次性任务，用于验证真实工具执行。',
    '- id: llm-deepseek',
    "  name: '@deepseek-ai/dsh-llm-deepseek'",
    '  config:',
    "    baseURL: 'http://127.0.0.1:3901'",
    '    apiKeyEnv: DEEPSEEK_API_KEY',
    ''
  ].join('\n')
)
NODE

( cd "$headless_dir" && pnpm install ) || fail "无头 profile 依赖安装失败"
printf '%s\n' "$headless_profile" > "$fixture_root/headless-profile.txt"
log "无头 profile 就绪：$headless_dir"

# ---- D13：远程**降级** profile ----
# 目的：造出"全家桶 remote 的客户端通道层没有安装"的真实配置，用来验证
#   附件能力明确不可用，而配对/心跳/设备库**不受影响**。
#
# 做法：remote 的启动前 boot 脚本只在 `enabled && requirePairingForLan` 时才注入
# （见 @linxin666/dsh-remote-web-ui 的 webserver/index-inject 监听）。把
# `requirePairingForLan` 置为 false 后：
#   - 客户端 `/remote` 改写层不存在 ⇒ 附件上传没有门控通道可用；
#   - 而配对路由（/api/pair/*）仍按 `enabled` 挂载 ⇒ 配对与心跳照常工作。
# 这正是方案要求的"remote 降级但配对不被关闭"的场景。
#
# 与主 profile 的唯一差别还有：**不**覆盖 connection.trustedHosts。降级 profile 里
# 没有 /remote 改写层，若再保留 `trustedHosts: []` 的 LAN 栅栏，页面本身都起不来，
# 就测不出"附件能力不可用"了。该 profile 只用于夹具，不进入生产。
degraded_profile="${DSH_ATTACH_DEGRADED_PROFILE:-dsh-attachments-degraded}"
degraded_port="${DSH_ATTACH_DEGRADED_PORT:-3098}"
degraded_dir="$dsh_home/profiles/$degraded_profile"

log "准备远程降级 profile：$degraded_profile（端口 $degraded_port）"
DSH_HOME="$dsh_home" "$dsh_bin" --profile "$degraded_profile" --from-default-profile web --dump-config \
  > "$fixture_root/evidence/degraded-default-config.yml" 2>&1 \
  || fail "创建降级 profile 失败"
[[ -f "$degraded_dir/package.json" ]] || fail "降级 profile 未创建：$degraded_dir"

log "登记降级 profile 的 bundles 与固定 allowBuilds"
node - "$degraded_dir" <<'NODE'
const fs = require('node:fs')
const path = require('node:path')
const dir = process.argv[2]
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
pkg.dependencies['@linxin666/dsh-web-all'] = '0.3.20'
delete pkg.pnpm
fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n')

const wsPath = path.join(dir, 'pnpm-workspace.yaml')
let ws = fs.readFileSync(wsPath, 'utf8')
ws = ws.split('\n').filter((line) => !/^\s*(onlyBuiltDependencies|allowBuilds|  - (cloudflared|cpu-features|node-pty|ssh2))\s*$/.test(line)).join('\n')
ws = ws.replace(/\n{3,}/g, '\n\n').trimEnd()
ws += [
  '',
  '# D13 降级夹具：固定允许这些依赖运行安装脚本，避免 pnpm 以 ERR_PNPM_IGNORED_BUILDS 中止。',
  'allowBuilds:',
  '  cloudflared: true',
  '  cpu-features: true',
  '  node-pty: true',
  '  ssh2: true',
  ''
].join('\n')
fs.writeFileSync(wsPath, ws)
NODE

( cd "$degraded_dir" && pnpm install ) || fail "降级 profile 依赖安装失败"

# 与主 profile 的差别还有 connection 栅栏：降级 profile 里没有 /remote 改写层，
# 若仍把 LAN 访问全部拒掉，页面自己都起不来，就测不出"附件能力明确不可用"。
# 因此这里把**本夹具探测到的 LAN 地址**列入 trustedHosts（仅此一次性夹具 profile，
# 生产 profile 不受影响）。于是裸 /api 其实是可达的——正因为可达，
# "承载拒绝上传、一个请求都不发"才是对本插件**主动拒绝回退**的真实判据，
# 而不是被服务端 401 掩盖的假象。
cat > "$degraded_dir/cordis.patch.yml" <<YAML
# D13 降级夹具 patch（由 tests/fixtures/setup.sh 生成，可重复覆盖）。
# 只做四件事：绑定 0.0.0.0、关掉 remote 的局域网通道层、放行本夹具 LAN 地址、指向离线 stub。
- id: webserver
  name: '@deepseek-ai/dsh-host-webserver'
  config:
    host: '0.0.0.0'
    port: ${degraded_port}
    compression: gzip

# 关键：remote 仍在（enabled 默认 true），但不再注入客户端 /remote 改写层。
# 配对路由与心跳仍按 enabled 挂载，因此"配对不受影响"是可断言的。
- id: web-ui-remote-web-ui
  name: '@linxin666/dsh-web-all/remote-web-ui'
  config:
    plugin: '@linxin666/dsh-remote-web-ui'
    config:
      requirePairingForLan: false

- id: connection
  name: '@deepseek-ai/dsh-client-connection'
  config:
    trustedHosts:
      - '${lan_address}'

- id: llm-deepseek
  name: '@deepseek-ai/dsh-llm-deepseek'
  config:
    baseURL: 'http://127.0.0.1:3901'
    apiKeyEnv: DEEPSEEK_API_KEY
YAML

log "把当前 tarball 装进降级 profile"
DSH_ATTACH_FIXTURE_PROFILE="$degraded_profile" bash "$plugin_root/tests/fixtures/install-plugin.sh" \
  || fail "降级 profile 安装本插件失败"
node -e "
const fs=require('node:fs');
fs.writeFileSync(process.argv[1], JSON.stringify({profile:process.argv[2],port:Number(process.argv[3])},null,2)+'\n');
" "$fixture_root/degraded-profile.json" "$degraded_profile" "$degraded_port"
log "降级 profile 就绪：$degraded_dir"

# ---- D20：第二个**并存**的 web target（同一 DSH_HOME，另一个端口） ----
# 目的：同一主机上同时跑两个目标（不同来源），用来证明附件归属按 target×session 隔离：
# 一个 target 页面上的导入绝不能出现在另一个 target 的草稿里，跨 target 的导航/重放同样不得投递。
#
# 与主 profile 的差别**只有** webserver.port（其余 bundle/插件/加固/离线 provider 完全同款），
# 且两者共享同一个 DSH_HOME ⇒ 会话集合相同。这样"投递到哪个 target 的哪个会话"才有判别力：
# 两个 target 看得到同一批会话，归属只能由页面身份与操作身份决定，而不是靠会话可见性。
secondary_profile="${DSH_ATTACH_SECONDARY_PROFILE:-dsh-attachments-fixture-b}"
secondary_port="${DSH_ATTACH_SECONDARY_PORT:-3097}"
secondary_dir="$dsh_home/profiles/$secondary_profile"

log "准备第二个 web target：$secondary_profile（端口 $secondary_port）"
DSH_HOME="$dsh_home" "$dsh_bin" --profile "$secondary_profile" --from-default-profile web --dump-config \
  > "$fixture_root/evidence/secondary-default-config.yml" 2>&1 \
  || fail "创建第二个 web profile 失败"
[[ -f "$secondary_dir/package.json" ]] || fail "第二个 web profile 未创建：$secondary_dir"

node - "$secondary_dir" <<'NODE'
const fs = require('node:fs')
const path = require('node:path')
const dir = process.argv[2]
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
pkg.dependencies['@linxin666/dsh-web-all'] = '0.3.20'
delete pkg.pnpm
fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n')

const wsPath = path.join(dir, 'pnpm-workspace.yaml')
let ws = fs.readFileSync(wsPath, 'utf8')
ws = ws.split('\n').filter((line) => !/^\s*(onlyBuiltDependencies|allowBuilds|  - (cloudflared|cpu-features|node-pty|ssh2))\s*$/.test(line)).join('\n')
ws = ws.replace(/\n{3,}/g, '\n\n').trimEnd()
ws += [
  '',
  '# D20 第二 target 夹具：固定允许这些依赖运行安装脚本，避免 pnpm 以 ERR_PNPM_IGNORED_BUILDS 中止。',
  'allowBuilds:',
  '  cloudflared: true',
  '  cpu-features: true',
  '  node-pty: true',
  '  ssh2: true',
  ''
].join('\n')
fs.writeFileSync(wsPath, ws)
NODE

( cd "$secondary_dir" && pnpm install ) || fail "第二个 web profile 依赖安装失败"

cat > "$secondary_dir/cordis.patch.yml" <<YAML
# D20 第二 target 夹具 patch（由 tests/fixtures/setup.sh 生成，可重复覆盖）。
# 与主夹具 profile 逐字段同款，只有端口不同（3097），因此两个 target 的差异只有来源。
- id: webserver
  name: '@deepseek-ai/dsh-host-webserver'
  config:
    host: '0.0.0.0'
    port: ${secondary_port}
    compression: gzip
    compressionLevel: 1
    compressionThresholdBytes: 1024

- id: connection
  name: '@deepseek-ai/dsh-client-connection'
  config:
    trustedHosts: []

- id: llm-deepseek
  name: '@deepseek-ai/dsh-llm-deepseek'
  config:
    baseURL: 'http://127.0.0.1:3901'
    apiKeyEnv: DEEPSEEK_API_KEY
YAML

log "把当前 tarball 装进第二个 web target"
DSH_ATTACH_FIXTURE_PROFILE="$secondary_profile" bash "$plugin_root/tests/fixtures/install-plugin.sh" \
  || fail "第二个 web profile 安装本插件失败"
node -e "
const fs=require('node:fs');
fs.writeFileSync(process.argv[1], JSON.stringify({profile:process.argv[2],port:Number(process.argv[3])},null,2)+'\n');
" "$fixture_root/secondary-profile.json" "$secondary_profile" "$secondary_port"
log "第二个 web target 就绪：$secondary_dir（端口 $secondary_port）"

log "setup: OK"
printf 'fixture_root=%s\nprofile_dir=%s\nsecondary_profile_dir=%s\n' "$fixture_root" "$profile_dir" "$secondary_dir"
