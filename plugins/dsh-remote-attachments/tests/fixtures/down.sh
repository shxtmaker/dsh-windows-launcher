#!/usr/bin/env bash
# D04 夹具清理：只停止并删除夹具自有资源。
#
# 安全约束：
#  - 只按夹具记录的 pid 文件停止进程，绝不做全局 pkill
#  - 只删除夹具自有目录（默认 artifacts/fixture/），且拒绝删除仓库根、$HOME 与其父目录
set -euo pipefail

plugin_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
repo_root="$(cd "$plugin_root/../.." && pwd)"
fixture_root="${DSH_ATTACH_FIXTURE_ROOT:-$repo_root/artifacts/fixture}"
profile_name="${DSH_ATTACH_FIXTURE_PROFILE:-dsh-attachments-fixture}"
dsh_home="$fixture_root/dsh-home"
profile_dir="$dsh_home/profiles/$profile_name"

log() { printf '[down] %s\n' "$*"; }
fail() { printf '[down] FAIL: %s\n' "$*" >&2; exit 1; }

pid_file="$fixture_root/service.pid"

# D20：第二个并存的 web target 把 pid 写在 service-b.pid。两个文件在这里一起收口，
# 因此 down.sh 之后不会有任何夹具服务残留（两个 profile 名都以 dsh-attachments-fixture 开头，
# 下面的归属校验对二者同样成立）。
pid_files=("$fixture_root/service.pid" "$fixture_root/service-b.pid")

# 判断一个 pid 是否确实属于本夹具：命令行含 fixture profile，且其 DSH_HOME 指向夹具目录。
# 两个条件必须同时满足，避免误杀同名的其他进程。
is_fixture_process() {
  local pid="$1"
  [[ -r "/proc/$pid/cmdline" ]] || return 1
  local cmdline env_home
  cmdline="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)"
  env_home="$(tr '\0' '\n' < "/proc/$pid/environ" 2>/dev/null | sed -n 's/^DSH_HOME=//p' | head -1)"
  [[ "$cmdline" == *"$profile_name"* ]] || return 1
  [[ -n "$env_home" && "$env_home" == *"fixture"*"dsh-home"* ]] || return 1
  return 0
}

# 1) 停止夹具服务：只认 pid 文件，并核对命令行与 DSH_HOME 双重归属
for pid_file in "${pid_files[@]}"; do
  if [[ -f "$pid_file" ]]; then
    pid="$(cat "$pid_file" 2>/dev/null || true)"
    if [[ -n "${pid:-}" ]] && kill -0 "$pid" 2>/dev/null; then
      if is_fixture_process "$pid"; then
        log "停止夹具服务 pid=$pid（$(basename "$pid_file")）"
        kill "$pid" 2>/dev/null || true
        for _ in $(seq 1 20); do
          kill -0 "$pid" 2>/dev/null || break
          sleep 0.5
        done
        if kill -0 "$pid" 2>/dev/null; then
          log "进程未退出，发送 SIGKILL"
          kill -9 "$pid" 2>/dev/null || true
        fi
      else
        # 归属无法确认时不得继续删除目录，否则会留下指向已删除 profile 的孤儿进程。
        fail "pid=$pid 不属于本夹具（命令行或 DSH_HOME 不匹配），拒绝停止并拒绝清理"
      fi
    else
      log "pid 文件存在但进程已不在：$pid"
    fi
    rm -f "$pid_file"
  else
    log "无 pid 文件，跳过停止：$pid_file"
  fi
done

# 1b) 兜底：清理 pid 文件丢失后的残留夹具进程（仍需双重归属确认）
leftovers=0
for candidate in $(pgrep -f -- "profile $profile_name" 2>/dev/null || true); do
  if is_fixture_process "$candidate"; then
    log "清理残留夹具进程 pid=$candidate"
    kill "$candidate" 2>/dev/null || true
    leftovers=$((leftovers + 1))
  fi
done
if [[ "$leftovers" -gt 0 ]]; then
  sleep 2
  for candidate in $(pgrep -f -- "profile $profile_name" 2>/dev/null || true); do
    if is_fixture_process "$candidate"; then
      kill -9 "$candidate" 2>/dev/null || true
    fi
  done
fi

# 2) 删除夹具自有目录，并做路径安全校验
if [[ -d "$fixture_root" ]]; then
  resolved="$(cd "$fixture_root" && pwd -P)"
  case "$resolved" in
    "/"|"$HOME"|"$repo_root"|"$repo_root/"|"$HOME/.dsh"|"$HOME/.dsh/"|"")
      fail "拒绝删除受保护路径：$resolved" ;;
  esac
  # 必须位于仓库的 artifacts/ 之下
  case "$resolved" in
    "$repo_root"/artifacts/*) ;;
    *) fail "夹具目录不在仓库 artifacts/ 之下，拒绝删除：$resolved" ;;
  esac
  log "删除夹具目录 $resolved"
  rm -rf "$resolved"
else
  log "夹具目录不存在，跳过删除"
fi

log "down: OK（未触碰生产 profile 或其它 DSH_HOME）"
