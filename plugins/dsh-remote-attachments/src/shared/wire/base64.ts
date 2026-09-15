/**
 * 严格 Base64（RFC 4648 §4，带 padding）。
 *
 * 不使用 `atob`/`Buffer`：前者在浏览器宽松解析（忽略空白与非法字符），
 * 后者不可用于 client 半区。这里只接受规范形式，非法输入一律抛错，
 * 由 codec 转成 `invalid-field-value`。
 */

const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'

const LOOKUP = new Int16Array(128).fill(-1)
for (let i = 0; i < ALPHABET.length; i += 1) LOOKUP[ALPHABET.charCodeAt(i)] = i

/** 规范 Base64 字符集（不含 padding），供模式校验复用。 */
export const BASE64_PATTERN = /^[A-Za-z0-9+/]+={0,2}$/

/** 判断是否是规范 Base64（长度 4 的倍数、padding 正确、无空白）。 */
export function isCanonicalBase64(text: string): boolean {
  if (text.length === 0 || text.length % 4 !== 0) return false
  if (!BASE64_PATTERN.test(text)) return false
  const padding = text.endsWith('==') ? 2 : text.endsWith('=') ? 1 : 0
  // padding 只能出现在末尾，且 '=' 只能出现在最后两个位置（模式已保证）。
  if (text.slice(0, text.length - padding).includes('=')) return false
  return true
}

/** 严格解码；非法输入抛 `TypeError`。 */
export function base64Decode(text: string): Uint8Array {
  if (!isCanonicalBase64(text)) throw new TypeError('非法 Base64')
  const padding = text.endsWith('==') ? 2 : text.endsWith('=') ? 1 : 0
  const out = new Uint8Array((text.length / 4) * 3 - padding)
  let outIndex = 0
  for (let i = 0; i < text.length; i += 4) {
    const c0 = LOOKUP[text.charCodeAt(i)] ?? -1
    const c1 = LOOKUP[text.charCodeAt(i + 1)] ?? -1
    const c2 = text.charCodeAt(i + 2) === 61 ? 0 : (LOOKUP[text.charCodeAt(i + 2)] ?? -1)
    const c3 = text.charCodeAt(i + 3) === 61 ? 0 : (LOOKUP[text.charCodeAt(i + 3)] ?? -1)
    if (c0 < 0 || c1 < 0 || c2 < 0 || c3 < 0) throw new TypeError('非法 Base64')
    const triple = (c0 << 18) | (c1 << 12) | (c2 << 6) | c3
    if (outIndex < out.length) out[outIndex++] = (triple >> 16) & 0xff
    if (outIndex < out.length) out[outIndex++] = (triple >> 8) & 0xff
    if (outIndex < out.length) out[outIndex++] = triple & 0xff
  }
  return out
}

/** 规范编码（带 padding，无换行）。 */
export function base64Encode(bytes: Uint8Array): string {
  let out = ''
  for (let i = 0; i < bytes.length; i += 3) {
    const b0 = bytes[i] ?? 0
    const b1 = bytes[i + 1]
    const b2 = bytes[i + 2]
    out += ALPHABET.charAt(b0 >> 2)
    out += ALPHABET.charAt(((b0 & 0x03) << 4) | ((b1 ?? 0) >> 4))
    out += b1 === undefined ? '=' : ALPHABET.charAt(((b1 & 0x0f) << 2) | ((b2 ?? 0) >> 6))
    out += b2 === undefined ? '=' : ALPHABET.charAt(b2 & 0x3f)
  }
  return out
}
