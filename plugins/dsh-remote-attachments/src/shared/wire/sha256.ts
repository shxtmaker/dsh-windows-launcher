/**
 * 增量 SHA-256：Web Crypto 可用时用 Web Crypto，不可用时用自带的纯 JS 实现。
 *
 * 背景（主方案 4.2）：HTTP 局域网不是 SecureContext，`crypto.subtle` 可能整体缺失，
 * 但内容完整性校验不能因此关闭。因此这里不引入任何依赖，自己实现 SHA-256 作为
 * 锁定兜底，并保证两条路径对同一输入给出同一结果（由单元测试与门禁脚本用
 * NIST 向量 + coreutils `sha256sum` 交叉验证）。
 *
 * 纯 JS 实现是**增量**的（64 字节块累积，内存 O(1)）；
 * Web Crypto 只有一次性 `digest()`，因此该路径在 `digestHex()` 时对已累积的
 * 分块做一次整体摘要，内存上界为单文件上限（20 MiB）。
 */

/** 摘要后端标识，供测试断言"确实走了哪条路"。 */
export type Sha256Backend = 'webcrypto' | 'pure-js'

/** 增量摘要器。 */
export interface Sha256Hasher {
  readonly backend: Sha256Backend
  /** 追加一段字节；不复制调用方数据。 */
  update(data: Uint8Array): void
  /** 计算摘要（十六进制小写）。可重复调用，不改变已累积状态。 */
  digestHex(): Promise<string>
}

const K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
])

const INITIAL_H = new Uint32Array([
  0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
])

const rotr = (value: number, bits: number): number => ((value >>> bits) | (value << (32 - bits))) >>> 0

const HEX = '0123456789abcdef'

/** 把摘要字节转成十六进制小写。 */
export function toHex(bytes: Uint8Array): string {
  let out = ''
  for (let i = 0; i < bytes.length; i += 1) {
    const byte = bytes[i] ?? 0
    out += HEX[(byte >> 4) & 0x0f]
    out += HEX[byte & 0x0f]
  }
  return out
}

/** 纯 JS 增量 SHA-256（无依赖）。 */
export class PureJsSha256 implements Sha256Hasher {
  readonly backend: Sha256Backend = 'pure-js'

  private readonly state = new Uint32Array(INITIAL_H)
  private readonly block = new Uint8Array(64)
  private readonly words = new Uint32Array(64)
  private blockLength = 0
  private totalBytes = 0
  private finalized = false
  private digestValue: string | null = null

  update(data: Uint8Array): void {
    if (this.finalized) throw new Error('sha256: 摘要已结束，不能继续 update')
    this.totalBytes += data.length
    let offset = 0
    if (this.blockLength > 0) {
      const need = Math.min(64 - this.blockLength, data.length)
      this.block.set(data.subarray(0, need), this.blockLength)
      this.blockLength += need
      offset = need
      if (this.blockLength === 64) {
        this.compress(this.block, 0)
        this.blockLength = 0
      }
    }
    while (offset + 64 <= data.length) {
      this.compress(data, offset)
      offset += 64
    }
    if (offset < data.length) {
      this.block.set(data.subarray(offset), 0)
      this.blockLength = data.length - offset
    }
  }

  private compress(chunk: Uint8Array, offset: number): void {
    const w = this.words
    for (let i = 0; i < 16; i += 1) {
      const j = offset + i * 4
      w[i] = ((chunk[j] ?? 0) << 24) | ((chunk[j + 1] ?? 0) << 16) | ((chunk[j + 2] ?? 0) << 8) | (chunk[j + 3] ?? 0)
    }
    for (let i = 16; i < 64; i += 1) {
      const w15 = w[i - 15] ?? 0
      const w2 = w[i - 2] ?? 0
      const s0 = (rotr(w15, 7) ^ rotr(w15, 18) ^ (w15 >>> 3)) >>> 0
      const s1 = (rotr(w2, 17) ^ rotr(w2, 19) ^ (w2 >>> 10)) >>> 0
      w[i] = ((w[i - 16] ?? 0) + s0 + (w[i - 7] ?? 0) + s1) >>> 0
    }

    const s = this.state
    let a = s[0] ?? 0
    let b = s[1] ?? 0
    let c = s[2] ?? 0
    let d = s[3] ?? 0
    let e = s[4] ?? 0
    let f = s[5] ?? 0
    let g = s[6] ?? 0
    let h = s[7] ?? 0

    for (let i = 0; i < 64; i += 1) {
      const s1 = (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) >>> 0
      const ch = ((e & f) ^ (~e & g)) >>> 0
      const temp1 = (h + s1 + ch + (K[i] ?? 0) + (w[i] ?? 0)) >>> 0
      const s0 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) >>> 0
      const maj = ((a & b) ^ (a & c) ^ (b & c)) >>> 0
      const temp2 = (s0 + maj) >>> 0
      h = g
      g = f
      f = e
      e = (d + temp1) >>> 0
      d = c
      c = b
      b = a
      a = (temp1 + temp2) >>> 0
    }

    s[0] = ((s[0] ?? 0) + a) >>> 0
    s[1] = ((s[1] ?? 0) + b) >>> 0
    s[2] = ((s[2] ?? 0) + c) >>> 0
    s[3] = ((s[3] ?? 0) + d) >>> 0
    s[4] = ((s[4] ?? 0) + e) >>> 0
    s[5] = ((s[5] ?? 0) + f) >>> 0
    s[6] = ((s[6] ?? 0) + g) >>> 0
    s[7] = ((s[7] ?? 0) + h) >>> 0
  }

  /** 同步摘要：纯 JS 后端没有异步约束。首次调用后结果被缓存。 */
  digestHexSync(): string {
    if (this.digestValue !== null) return this.digestValue
    this.finalized = true
    const bitLength = this.totalBytes * 8
    const tail = new Uint8Array(this.blockLength < 56 ? 64 : 128)
    tail.set(this.block.subarray(0, this.blockLength), 0)
    tail[this.blockLength] = 0x80
    const view = new DataView(tail.buffer)
    // 长度字段是 64 位大端；JS 安全整数范围内高 32 位可由除法得到。
    view.setUint32(tail.length - 8, Math.floor(bitLength / 0x100000000), false)
    view.setUint32(tail.length - 4, bitLength >>> 0, false)
    this.compress(tail, 0)
    if (tail.length === 128) this.compress(tail, 64)

    const out = new Uint8Array(32)
    const outView = new DataView(out.buffer)
    for (let i = 0; i < 8; i += 1) outView.setUint32(i * 4, this.state[i] ?? 0, false)
    this.digestValue = toHex(out)
    return this.digestValue
  }

  digestHex(): Promise<string> {
    return Promise.resolve(this.digestHexSync())
  }
}

/** Web Crypto 后端：累积分块，最后一次性摘要。 */
class WebCryptoSha256 implements Sha256Hasher {
  readonly backend: Sha256Backend = 'webcrypto'

  private readonly chunks: Uint8Array[] = []
  private totalBytes = 0
  private finalized = false

  constructor(private readonly subtle: SubtleCrypto) {}

  update(data: Uint8Array): void {
    if (this.finalized) throw new Error('sha256: 摘要已结束，不能继续 update')
    this.chunks.push(data)
    this.totalBytes += data.length
  }

  async digestHex(): Promise<string> {
    this.finalized = true
    const joined = new Uint8Array(this.totalBytes)
    let offset = 0
    for (const chunk of this.chunks) {
      joined.set(chunk, offset)
      offset += chunk.length
    }
    const digest = await this.subtle.digest('SHA-256', joined)
    return toHex(new Uint8Array(digest))
  }
}

/**
 * 创建增量摘要器：能用 Web Crypto 就用，不能就用纯 JS 兜底。
 * @param cryptoLike 可注入的 crypto（测试用）；省略时读取 `globalThis.crypto`。
 */
export function createSha256(cryptoLike?: { subtle?: SubtleCrypto } | undefined): Sha256Hasher {
  const source = cryptoLike ?? (globalThis as { crypto?: { subtle?: SubtleCrypto } }).crypto
  const subtle = source?.subtle
  if (subtle && typeof subtle.digest === 'function') return new WebCryptoSha256(subtle)
  return new PureJsSha256()
}

/** 一次性摘要；与 `createSha256()` 走同一条后端选择逻辑。 */
export async function sha256Hex(data: Uint8Array): Promise<string> {
  const hasher = createSha256()
  hasher.update(data)
  return hasher.digestHex()
}

/** 同步摘要，恒定走纯 JS 实现（供测试与证据脚本做独立比对）。 */
export function sha256HexSync(data: Uint8Array): string {
  const hasher = new PureJsSha256()
  hasher.update(data)
  return hasher.digestHexSync()
}
