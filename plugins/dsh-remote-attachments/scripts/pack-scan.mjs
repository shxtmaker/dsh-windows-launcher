/**
 * tarball 解析与包卫生扫描（D03 test:pack 与 D22 final-tarball-install 共用）。
 *
 * 为什么抽出来：D22 必须用与 D03 **同一条**扫描判定"交付包不含凭据/测试私有数据/绝对家目录"，
 * 而不是在门禁脚本里再抄一份正则（两份正则迟早漂移，漂移后的绿灯没有证明力）。
 * 本模块只提供纯函数：解析 tar、对条目做卫生扫描；判据与证据仍由调用方各自落盘。
 */

import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'

/** 必须与全家桶、remote 插件区分开的身份。 */
export const FORBIDDEN_PACKAGE_NAMES = ['@linxin666/dsh-web-all', '@linxin666/dsh-remote-web-ui']

/** 不得出现在包内的凭据/生产数据形态（对路径与正文同时生效）。 */
export const FORBIDDEN_PATH_PATTERNS = [
  /(^|\/)\.env(\.|$)/i,
  /(^|\/)\.npmrc$/i,
  /credential/i,
  /(^|\/)\.dsh(\/|$)/i,
  /device-?store/i,
  /pairing-?(token|secret|credential)/i,
  /(^|\/)\.git(\/|$)/,
  /(^|\/)node_modules(\/|$)/,
  /\.(pem|key|p12|pfx|jks)$/i
]

export const FORBIDDEN_CONTENT_PATTERNS = [
  // 私钥与常见令牌形态
  /-----BEGIN [A-Z ]*PRIVATE KEY-----/,
  /\bdsh_pair\s*=/,
  /\b(?:ghp|gho|github_pat)_[A-Za-z0-9_]{20,}/,
  /\bnpm_[A-Za-z0-9]{36}\b/,
  // 本机绝对路径（不应进入发布产物）
  /\/home\/[a-z0-9._-]+\//i,
  /[A-Za-z]:\\\\Users\\\\/i
]

/**
 * 对 tarball 条目做包卫生扫描（路径 + 正文）。
 *
 * @param {ReadonlyArray<{ path: string, content: Buffer | null, isFile?: boolean }>} entries
 * @returns {{ offendingPaths: string[], offendingContent: string[], ok: boolean }}
 */
export function scanTarEntries(entries) {
  const files = entries.filter((entry) => entry.isFile !== false && entry.content !== null)
  const offendingPaths = files
    .map((entry) => entry.path)
    .filter((path) => FORBIDDEN_PATH_PATTERNS.some((re) => re.test(path)))
  const offendingContent = []
  for (const file of files) {
    const text = file.content.toString('utf8')
    for (const re of FORBIDDEN_CONTENT_PATTERNS) {
      if (re.test(text)) offendingContent.push(`${file.path} 命中 ${re}`)
    }
  }
  return { offendingPaths, offendingContent, ok: offendingPaths.length === 0 && offendingContent.length === 0 }
}

/** 解析 tar 归档（ustar/pax），返回 [{ path, content(Buffer|null), size, isFile }]。 */
export function parseTar(buffer) {
  const entries = []
  let offset = 0
  let pendingLongName = null
  while (offset + 512 <= buffer.length) {
    const header = buffer.subarray(offset, offset + 512)
    if (header.every((byte) => byte === 0)) break

    const nameField = header.subarray(0, 100).toString('utf8').replace(/\0.*$/, '')
    const sizeField = header.subarray(124, 136).toString('utf8').replace(/\0.*$/, '').trim()
    const typeFlag = String.fromCharCode(header[156] ?? 0)
    const prefix = header.subarray(345, 500).toString('utf8').replace(/\0.*$/, '')
    const size = sizeField === '' ? 0 : parseInt(sizeField, 8)
    assert.ok(Number.isFinite(size) && size >= 0, `tar 头 size 非法：${sizeField}`)

    const dataStart = offset + 512
    const dataEnd = dataStart + size
    const body = buffer.subarray(dataStart, Math.min(dataEnd, buffer.length))

    if (typeFlag === 'L') {
      // GNU long name：正文是下一条目的完整路径
      pendingLongName = body.toString('utf8').replace(/\0.*$/, '')
    } else {
      const rawPath = nameField.startsWith('./') ? nameField.slice(2) : nameField
      const fullPath = pendingLongName ?? (prefix ? `${prefix}/${rawPath}` : rawPath)
      const isFile = typeFlag === '0' || typeFlag === '\0' || typeFlag === ''
      entries.push({
        path: fullPath,
        size,
        isFile,
        content: isFile ? body : null
      })
      pendingLongName = null
    }

    offset = dataStart + Math.ceil(size / 512) * 512
  }
  return entries
}

/** 解压 gzip 并解析 tar。 */
export async function readTarball(tarballPath) {
  const { gunzipSync } = await import('node:zlib')
  const compressed = await readFile(tarballPath)
  return parseTar(gunzipSync(compressed))
}
