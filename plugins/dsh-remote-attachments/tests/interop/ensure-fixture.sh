#!/usr/bin/env bash
# D18 互通夹具准备（幂等）。
#
# 为什么需要它：D18 的互通用例由 `DshLauncher.Core.Tests` 直接驱动，用例必须**自证**
# 它跑在"当前构建产物 + 当前夹具"上，而不是上一轮残留的 tarball（`pack/*.tgz` 陈旧时
# setup.sh 会静默装旧产物——已在 D07/D08 实测）。
#
# 做法：
#   1. 若 tarball 不存在，或 `src/`、`scripts/`、清单文件比 tarball 新 ⇒ 重新 `build` + `test:pack`；
#   2. 若夹具 profile 不存在，或 marker 里记录的 tarball SHA-256 与当前 tarball 不同
#      ⇒ `down.sh` + `setup.sh`（setup.sh 自身会强制重装当前 tarball）；
#   3. 否则复用（打印 reuse 原因），不重复几分钟的 pnpm 安装。
#
# 用法：
#   bash tests/interop/ensure-fixture.sh            # 按需构建/重建夹具
#   bash tests/interop/ensure-fixture.sh --force    # 无条件重建（门禁用，保证"当前产物"）
#   bash tests/interop/ensure-fixture.sh --check    # 只检查，不构建；未就绪则退出码 1
#
# 只写夹具自有目录（默认 artifacts/fixture/），绝不触碰 $HOME/.dsh 或其它 profile。
set -euo pipefail

plugin_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
repo_root="$(cd "$plugin_root/../.." && pwd)"
fixture_root="${DSH_ATTACH_FIXTURE_ROOT:-$repo_root/artifacts/fixture}"
profile="${DSH_ATTACH_FIXTURE_PROFILE:-dsh-attachments-fixture}"
profile_dir="$fixture_root/dsh-home/profiles/$profile"
tarball="${DSH_ATTACH_INTEROP_TARBALL:-$plugin_root/pack/shxtmaker-dsh-remote-attachments-0.1.0.tgz}"
marker="$fixture_root/interop-fixture.json"
pnpm_bin="${DSH_ATTACH_PNPM:-pnpm}"

force=0
check_only=0
for argument in "$@"; do
  case "$argument" in
    --force) force=1 ;;
    --check) check_only=1 ;;
    *) printf '[interop-fixture] FAIL: 未知参数 %s\n' "$argument" >&2; exit 2 ;;
  esac
done

log() { printf '[interop-fixture] %s\n' "$*"; }
fail() { printf '[interop-fixture] FAIL: %s\n' "$*" >&2; exit 1; }

sha256_of() { sha256sum "$1" | cut -d' ' -f1; }

# marker 里记录的 tarball 摘要（没有 marker 时为空）。
marker_sha=""
if [[ -f "$marker" ]]; then
  marker_sha="$(node -e "try{process.stdout.write(String(require(process.argv[1]).tarballSha256))}catch(e){}" "$marker")"
fi

# 源文件是否比 tarball 新：任一命中即视为陈旧。
sources_newer=0
if [[ -f "$tarball" ]]; then
  while IFS= read -r -d '' path; do
    if [[ "$path" -nt "$tarball" ]]; then sources_newer=1; break; fi
  done < <(find "$plugin_root/src" "$plugin_root/scripts" -type f -print0 2>/dev/null)
  for extra in "$plugin_root/package.json" "$plugin_root/tsconfig.json" "$plugin_root/cordis.patch.yml"; do
    if [[ -f "$extra" && "$extra" -nt "$tarball" ]]; then sources_newer=1; break; fi
  done
fi

tarball_sha=""
if [[ -f "$tarball" ]]; then tarball_sha="$(sha256_of "$tarball")"; fi

reuse_reason=""
if [[ "$force" -eq 0 ]]; then
  if [[ ! -f "$tarball" ]]; then
    reuse_reason=""
  elif [[ "$sources_newer" -eq 1 ]]; then
    reuse_reason=""
  elif [[ ! -f "$profile_dir/package.json" ]]; then
    reuse_reason=""
  elif [[ "$marker_sha" != "$tarball_sha" ]]; then
    reuse_reason=""
  else
    reuse_reason="tarball=$tarball_sha 与 marker 一致且源文件未更新、profile 在位"
  fi
fi

if [[ -n "$reuse_reason" ]]; then
  log "复用现有夹具：$reuse_reason"
  node -e "
const { writeFileSync } = require('node:fs')
const marker = process.argv[1]
const record = JSON.parse(require('node:fs').readFileSync(marker, 'utf8'))
record.reusedCount = Number(record.reusedCount ?? 0) + 1
record.lastDecision = 'reuse'
writeFileSync(marker, JSON.stringify(record, null, 2) + '\n')
" "$marker"
  printf 'interop-fixture: reuse marker=%s tarball=%s profile=%s\n' "$marker" "$tarball_sha" "$profile_dir"
  exit 0
fi

if [[ "$check_only" -eq 1 ]]; then
  printf '[interop-fixture] FAIL: 夹具未就绪（--check 不构建）；tarball=%s profile=%s\n' \
    "$([[ -f "$tarball" ]] && echo present || echo missing)" \
    "$([[ -f "$profile_dir/package.json" ]] && echo present || echo missing)" >&2
  exit 1
fi

log "构建并打包当前源码（保证夹具装的是当前产物）"
( cd "$plugin_root" && "$pnpm_bin" run build ) || fail "pnpm run build 失败"
( cd "$plugin_root" && "$pnpm_bin" run test:pack ) || fail "pnpm run test:pack 失败"
[[ -f "$tarball" ]] || fail "打包后仍缺少 tarball：$tarball"
tarball_sha="$(sha256_of "$tarball")"

log "重建夹具 DSH_HOME（down.sh → setup.sh，setup.sh 会强制重装当前 tarball）"
bash "$plugin_root/tests/fixtures/down.sh" || fail "down.sh 失败"
bash "$plugin_root/tests/fixtures/setup.sh" || fail "setup.sh 失败"
[[ -f "$profile_dir/package.json" ]] || fail "setup.sh 之后仍缺少夹具 profile：$profile_dir"

node -e "
const { writeFileSync, existsSync } = require('node:fs')
const [marker, tarballSha, profile, tarball] = process.argv.slice(1)
writeFileSync(marker, JSON.stringify({
  schemaVersion: 1,
  task: 'D18',
  purpose: 'interop-fixture',
  preparedAtUtc: new Date().toISOString(),
  tarball,
  tarballSha256: tarballSha,
  profile,
  profilePresent: existsSync(profile + '/package.json'),
  reusedCount: 0,
  lastDecision: 'rebuild'
}, null, 2) + '\n')
" "$marker" "$tarball_sha" "$profile_dir"

printf 'interop-fixture: rebuild marker=%s tarball=%s profile=%s\n' "$marker" "$tarball_sha" "$profile_dir"
log "OK"
