#!/usr/bin/env bash
set -euo pipefail

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
readonly SERVER_PATH="${SCRIPT_DIR}/fixture_server.py"
readonly STATE_ROOT="${XDG_RUNTIME_DIR:-/tmp}/dshwl-fixtures-${UID}"

usage() {
  printf '%s\n' \
    'Usage:' \
    '  fixture-control.sh start <mode> <rfc1918-ipv4> <port>' \
    '  fixture-control.sh stop <mode>' \
    '  fixture-control.sh status [mode]' \
    '  fixture-control.sh stop-all' \
    '' \
    'Modes: wrong-200 fence-403 old-unauthenticated redirect-302 notfound-404 non-http timeout'
}

validate_mode() {
  case "$1" in
    wrong-200|fence-403|old-unauthenticated|redirect-302|notfound-404|non-http|timeout) ;;
    *) printf 'Unknown fixture mode: %s\n' "$1" >&2; exit 2 ;;
  esac
}

prepare_state_root() {
  mkdir -p -- "${STATE_ROOT}"
  chmod 700 -- "${STATE_ROOT}"
}

pid_file_for() {
  printf '%s/%s.pid\n' "${STATE_ROOT}" "$1"
}

is_owned_process() {
  local fixture_pid="$1"
  local mode="$2"
  local index
  local saw_server=0
  local saw_mode=0
  local -a arguments=()

  [[ -r "/proc/${fixture_pid}/cmdline" ]] || return 1
  [[ "$(stat -c '%u' -- "/proc/${fixture_pid}")" == "${UID}" ]] || return 1
  mapfile -d '' -t arguments < "/proc/${fixture_pid}/cmdline"
  for ((index = 0; index < ${#arguments[@]}; index++)); do
    [[ "${arguments[index]}" == "${SERVER_PATH}" ]] && saw_server=1
    if [[ "${arguments[index]}" == '--mode' ]] &&
      ((index + 1 < ${#arguments[@]})) &&
      [[ "${arguments[index + 1]}" == "${mode}" ]]; then
      saw_mode=1
    fi
  done
  ((saw_server == 1 && saw_mode == 1))
}

process_has_endpoint() {
  local fixture_pid="$1"
  local bind_address="$2"
  local port="$3"
  local index
  local saw_bind=0
  local saw_port=0
  local -a arguments=()

  mapfile -d '' -t arguments < "/proc/${fixture_pid}/cmdline"
  for ((index = 0; index < ${#arguments[@]}; index++)); do
    if [[ "${arguments[index]}" == '--bind' ]] &&
      ((index + 1 < ${#arguments[@]})) &&
      [[ "${arguments[index + 1]}" == "${bind_address}" ]]; then
      saw_bind=1
    fi
    if [[ "${arguments[index]}" == '--port' ]] &&
      ((index + 1 < ${#arguments[@]})) &&
      [[ "${arguments[index + 1]}" == "${port}" ]]; then
      saw_port=1
    fi
  done
  ((saw_bind == 1 && saw_port == 1))
}

endpoint_is_listening() {
  python3 -c \
    'import socket, sys; sock = socket.create_connection((sys.argv[1], int(sys.argv[2])), timeout=0.25); sock.close()' \
    "$1" "$2" >/dev/null 2>&1
}

start_fixture() {
  local mode="$1"
  local bind_address="$2"
  local port="$3"
  local pid_file
  local existing_pid

  validate_mode "${mode}"
  prepare_state_root
  pid_file="$(pid_file_for "${mode}")"
  if [[ -f "${pid_file}" ]]; then
    existing_pid="$(<"${pid_file}")"
    if kill -0 "${existing_pid}" 2>/dev/null && is_owned_process "${existing_pid}" "${mode}"; then
      if process_has_endpoint "${existing_pid}" "${bind_address}" "${port}"; then
        printf 'Fixture already running: mode=%s pid=%s\n' "${mode}" "${existing_pid}"
        return 0
      fi
      printf 'Fixture mode is already running on a different endpoint; stop it first: mode=%s pid=%s\n' \
        "${mode}" "${existing_pid}" >&2
      return 1
    fi
    rm -f -- "${pid_file}"
  fi

  PYTHONDONTWRITEBYTECODE=1 nohup python3 "${SERVER_PATH}" \
    --mode "${mode}" \
    --bind "${bind_address}" \
    --port "${port}" \
    > "${STATE_ROOT}/${mode}.log" 2>&1 &
  local fixture_pid=$!
  printf '%s\n' "${fixture_pid}" > "${pid_file}"
  chmod 600 -- "${pid_file}" "${STATE_ROOT}/${mode}.log"

  for _ in {1..100}; do
    if ! kill -0 "${fixture_pid}" 2>/dev/null || ! is_owned_process "${fixture_pid}" "${mode}"; then
      printf 'Fixture failed to start. See %s\n' "${STATE_ROOT}/${mode}.log" >&2
      stop_fixture "${mode}" >/dev/null 2>&1 || true
      return 1
    fi
    if endpoint_is_listening "${bind_address}" "${port}"; then
      printf 'Fixture started: mode=%s pid=%s bind=%s port=%s\n' \
        "${mode}" "${fixture_pid}" "${bind_address}" "${port}"
      return 0
    fi
    sleep 0.1
  done
  printf 'Fixture process did not listen within 10 seconds. See %s\n' \
    "${STATE_ROOT}/${mode}.log" >&2
  stop_fixture "${mode}" >/dev/null 2>&1 || true
  return 1
}

stop_fixture() {
  local mode="$1"
  local pid_file
  local fixture_pid

  validate_mode "${mode}"
  prepare_state_root
  pid_file="$(pid_file_for "${mode}")"
  if [[ ! -f "${pid_file}" ]]; then
    printf 'Fixture already stopped: mode=%s\n' "${mode}"
    return 0
  fi

  fixture_pid="$(<"${pid_file}")"
  if kill -0 "${fixture_pid}" 2>/dev/null; then
    if ! is_owned_process "${fixture_pid}" "${mode}"; then
      printf 'PID ownership mismatch; refusing to signal pid=%s\n' "${fixture_pid}" >&2
      return 1
    fi
    kill "${fixture_pid}"
    for _ in {1..50}; do
      kill -0 "${fixture_pid}" 2>/dev/null || break
      sleep 0.1
    done
    if kill -0 "${fixture_pid}" 2>/dev/null; then
      printf 'Fixture did not stop in time; refusing to force-kill pid=%s\n' "${fixture_pid}" >&2
      return 1
    fi
  fi
  rm -f -- "${pid_file}"
  printf 'Fixture stopped: mode=%s\n' "${mode}"
}

fixture_status() {
  local mode="$1"
  local pid_file
  local fixture_pid
  validate_mode "${mode}"
  pid_file="$(pid_file_for "${mode}")"
  if [[ -f "${pid_file}" ]]; then
    fixture_pid="$(<"${pid_file}")"
    if kill -0 "${fixture_pid}" 2>/dev/null && is_owned_process "${fixture_pid}" "${mode}"; then
      printf 'RUNNING mode=%s pid=%s\n' "${mode}" "${fixture_pid}"
      return 0
    fi
  fi
  printf 'STOPPED mode=%s\n' "${mode}"
}

readonly ALL_MODES=(
  wrong-200 fence-403 old-unauthenticated redirect-302 notfound-404 non-http timeout
)

case "${1:-}" in
  start)
    [[ $# -eq 4 ]] || { usage >&2; exit 2; }
    start_fixture "$2" "$3" "$4"
    ;;
  stop)
    [[ $# -eq 2 ]] || { usage >&2; exit 2; }
    stop_fixture "$2"
    ;;
  status)
    [[ $# -le 2 ]] || { usage >&2; exit 2; }
    prepare_state_root
    if [[ $# -eq 2 ]]; then
      fixture_status "$2"
    else
      for mode in "${ALL_MODES[@]}"; do fixture_status "${mode}"; done
    fi
    ;;
  stop-all)
    [[ $# -eq 1 ]] || { usage >&2; exit 2; }
    for mode in "${ALL_MODES[@]}"; do stop_fixture "${mode}"; done
    ;;
  *)
    usage >&2
    exit 2
    ;;
esac
