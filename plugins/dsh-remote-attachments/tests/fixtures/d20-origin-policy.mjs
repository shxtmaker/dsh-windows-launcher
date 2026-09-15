/**
 * D20：来源规则（D15）的 JavaScript 移植 —— 只做**来源判定**，不碰任何浏览器状态。
 *
 * 为什么要有这个文件：D15 的规则实现在 `src/DshLauncher.Core/Remote/RemoteOrigin.cs`
 * 与 `RemotePageOriginPolicy.cs`（C#，Windows 侧的 WebView2 事件判定）。D20 的判据要在
 * Linux 上跑，而"跨来源导航不得把文件能力带过去"这条结论**完全取决于来源判定本身**，
 * 因此把同一套规则逐条移植到这里，用同一批伪装形态（userinfo / 后缀主机 / IPv6 字面量 /
 * 默认端口 / 结尾根点 / 外部链接）钉住它。
 *
 * 移植的**忠实性**要求（与 C# 逐条对应，稳定码字符串也刻意保持一致）：
 *   1. 只接受 http/https，scheme 小写化；
 *   2. authority 带 userinfo（`http://evil@our.host/`）一律拒绝 —— userinfo 不是来源的一部分，
 *      但带凭据的 URL 视觉上就是伪装，不能当可信页面；
 *   3. 主机小写化并去掉结尾根点；
 *   4. IPv6 字面量按地址规范化后加方括号（`[0:0:0:0:0:0:0:1]` 与 `[::1]` 相同）；
 *   5. 默认端口（http 80 / https 443）不写进规范化形式；
 *   6. 比较永远是 `Normalized` 的**逐字比较**，绝不做前缀/后缀匹配；
 *   7. 主机必须对照**原始 authority 文本**复核 —— 解析器会悄悄丢掉越界内容
 *      （实测 `http://[::1].evil.example/` 的 `host` 就是 `[::1]`），只信解析结果会被绕过。
 *
 * 与 C# 的**已知差异**（都是解析器差异，不影响判定结论，D20 用向量钉住的是结论）：
 *   - `Uri.TryCreate` 接受一些 WHATWG URL 会拒绝/改写的形态（如 `http://a_b/`）。
 *     本模块对这类"两边都不确定"的形态直接判 `origin-invalid`（失败关闭），
 *     因此不会出现"JS 认为可信、C# 认为不可信"的放水方向。
 */

/** 稳定码：与 `RemoteOriginCodes` 逐字一致。只在本机使用，绝不写进线协议报文。 */
export const ORIGIN_CODES = Object.freeze({
  valid: 'origin-valid',
  invalid: 'origin-invalid',
  unsupportedScheme: 'origin-unsupported-scheme',
  userInfoRejected: 'origin-userinfo-rejected',
  trusted: 'origin-trusted',
  external: 'origin-external',
  externalNavigation: 'origin-external-navigation',
  notMainDocument: 'origin-not-main-document'
})

/** 导航种类：与 `RemoteNavigationKind` 一致。 */
export const NAVIGATION_KIND = Object.freeze({
  mainDocument: 'MainDocument',
  childFrame: 'ChildFrame',
  subresource: 'Subresource',
  externalLink: 'ExternalLink'
})

const DEFAULT_PORTS = Object.freeze({ http: 80, https: 443 })

/** 取出 URL 里 scheme 之后的原始 authority 文本（到第一个 `/ ? #` 为止）。 */
function extractAuthority(url) {
  const separator = url.indexOf('://')
  if (separator < 0) return null
  const start = separator + 3
  let end = url.length
  for (const stop of ['/', '?', '#']) {
    const at = url.indexOf(stop, start)
    if (at >= 0 && at < end) end = at
  }
  const authority = url.slice(start, end)
  return authority.length > 0 ? authority : null
}

/**
 * 把 IPv6 字面量规范化成 RFC 5952 形态（小写、最长零串压缩、内嵌 IPv4 保留点分形态）。
 * 与 .NET `IPAddress.ToString()` 的输出一致。失败返回 null。
 */
export function canonicalizeIpv6(literal) {
  const text = String(literal)
  if (text.length === 0) return null
  // 内嵌 IPv4 尾段（::ffff:1.2.3.4）
  let head = text
  let v4 = null
  const lastColon = text.lastIndexOf(':')
  if (lastColon >= 0 && text.slice(lastColon + 1).includes('.')) {
    const tail = text.slice(lastColon + 1)
    const octets = tail.split('.')
    if (octets.length !== 4) return null
    const values = octets.map((octet) => (/^\d{1,3}$/.test(octet) ? Number(octet) : NaN))
    if (values.some((value) => !Number.isInteger(value) || value < 0 || value > 255)) return null
    v4 = values
    head = text.slice(0, lastColon)
  }
  const rawGroups = head.split('::')
  if (rawGroups.length > 2) return null
  const parseGroups = (segment) => {
    if (segment === '') return []
    const groups = segment.split(':')
    const values = []
    for (const group of groups) {
      if (!/^[0-9a-fA-F]{1,4}$/.test(group)) return null
      values.push(parseInt(group, 16))
    }
    return values
  }
  const left = parseGroups(rawGroups[0] ?? '')
  const right = rawGroups.length === 2 ? parseGroups(rawGroups[1] ?? '') : []
  if (left === null || right === null) return null
  const total = left.length + right.length + (v4 === null ? 0 : 2)
  let groups
  if (rawGroups.length === 2) {
    if (total > 8) return null
    groups = [...left, ...Array(8 - total).fill(0), ...right]
  } else {
    if (total !== 8) return null
    groups = [...left]
  }
  if (v4 !== null) {
    groups = groups.slice(0, 6)
    if (groups.length !== 6) return null
  }
  // 最长零串压缩（长度 >= 2 才压缩；与 RFC 5952 一致，.NET 亦同）
  let bestStart = -1
  let bestLength = 0
  let start = -1
  for (let index = 0; index <= groups.length; index += 1) {
    if (index < groups.length && groups[index] === 0) {
      if (start < 0) start = index
      continue
    }
    if (start >= 0) {
      const length = index - start
      if (length > bestLength) {
        bestLength = length
        bestStart = start
      }
      start = -1
    }
  }
  const parts = []
  for (let index = 0; index < groups.length; index += 1) {
    if (bestLength > 1 && index === bestStart) {
      parts.push('')
      index += bestLength - 1
      continue
    }
    parts.push(groups[index].toString(16))
  }
  let normalized = parts.join(':')
  if (bestLength > 1) {
    if (normalized.startsWith(':')) normalized = `:${normalized}`
    if (normalized.endsWith(':')) normalized = `${normalized}:`
  }
  if (v4 !== null) {
    const tail = v4.join('.')
    normalized = normalized === '' ? `::${tail}` : `${normalized}${normalized.endsWith(':') ? '' : ':'}${tail}`
  } else if (normalized === '') {
    // 全零地址：'::' 的压缩结果是空串，必须补回来（与 .NET IPAddress.IPv6Any.ToString() 一致）。
    normalized = '::'
  }
  return normalized
}

/**
 * 解析一个绝对 URL 并规范化其来源。
 * 失败返回确定码而不是抛异常（与 C# 的 `RemoteOriginParse` 同款）。
 * @returns {{ ok: boolean, code: string, origin: { scheme: string, host: string, port: number, normalized: string } | null }}
 */
export function parseRemoteOrigin(url) {
  if (typeof url !== 'string' || url.trim().length === 0) {
    return { ok: false, code: ORIGIN_CODES.invalid, origin: null }
  }
  const candidate = url.trim()
  let parsed
  try {
    parsed = new URL(candidate)
  } catch {
    return { ok: false, code: ORIGIN_CODES.invalid, origin: null }
  }
  const scheme = parsed.protocol.replace(/:$/, '').toLowerCase()
  if (scheme !== 'http' && scheme !== 'https') {
    return { ok: false, code: ORIGIN_CODES.unsupportedScheme, origin: null }
  }
  // 先按解析结果判 userinfo，再回原始 authority 复核：两者都必须干净。
  if (parsed.username !== '' || parsed.password !== '') {
    return { ok: false, code: ORIGIN_CODES.userInfoRejected, origin: null }
  }
  const authority = extractAuthority(candidate)
  if (authority === null || authority.includes('@')) {
    return { ok: false, code: ORIGIN_CODES.invalid, origin: null }
  }
  const host = canonicalizeHost(parsed, authority)
  if (host === null) return { ok: false, code: ORIGIN_CODES.invalid, origin: null }
  const port = parsed.port === '' ? DEFAULT_PORTS[scheme] : Number(parsed.port)
  if (!Number.isInteger(port) || port < 0 || port > 65535) {
    return { ok: false, code: ORIGIN_CODES.invalid, origin: null }
  }
  const normalized = port === DEFAULT_PORTS[scheme] ? `${scheme}://${host}` : `${scheme}://${host}:${port}`
  return { ok: true, code: ORIGIN_CODES.valid, origin: { scheme, host, port, normalized } }
}

/** 主机规范化；任何"解析器丢掉了字符"的情况都返回 null（失败关闭）。 */
function canonicalizeHost(parsed, authority) {
  if (authority.startsWith('[')) {
    // IPv6 字面量必须是 "[<literal>]" 或 "[<literal>]:<port>"，后面不允许再有任何字符。
    const closing = authority.indexOf(']')
    if (closing < 0) return null
    const rest = authority.slice(closing + 1)
    const portText = rest.length > 1 ? rest.slice(1) : ''
    if (rest.length > 0 && (rest[0] !== ':' || portText.length === 0 || !/^\d+$/.test(portText))) return null
    const literal = authority.slice(1, closing)
    // 规范化失败（不是合法 IPv6 字面量）一律拒绝；内嵌 IPv4 形态（::ffff:1.2.3.4）
    // 在两侧都算 IPv6，canonicalizeIpv6 已处理。
    const normalized = canonicalizeIpv6(literal)
    if (normalized === null) return null
    if (!parsed.hostname.startsWith('[')) return null
    return `[${normalized}]`
  }
  if (authority.includes('[') || authority.includes(']')) return null
  const hostname = parsed.hostname
  if (hostname.length === 0) return null
  // 解析器已经把主机归一成小写 punycode/点分形态；这里再排除不该出现的形态。
  if (hostname.includes('@') || hostname.includes('/') || hostname.includes('%')) return null
  const candidate = hostname.toLowerCase().replace(/\.+$/, '')
  if (candidate.length === 0) return null
  // 不变量：规范化结果必须是 authority 文本去掉大小写、端口与结尾根点后的忠实形态。
  const colon = authority.lastIndexOf(':')
  const rawHost = (colon < 0 ? authority : authority.slice(0, colon)).toLowerCase().replace(/\.+$/, '')
  if (rawHost !== candidate) return null
  if (!/^[a-z0-9.-]+$/.test(candidate)) return null
  return candidate
}

/** 逐字比较两个规范化来源。 */
export function originMatches(left, right) {
  if (left === null || right === null || left === undefined || right === undefined) return false
  return left.normalized === right.normalized
}

/**
 * 判定一次导航/请求是否可作为可信主文档来源。
 * 与 `RemotePageOriginPolicy.Evaluate` 逐条对应。
 * @returns {{ trusted: boolean, mainDocument: boolean, code: string, origin: object | null }}
 */
export function evaluateNavigation(trustedOrigin, uri, kind = NAVIGATION_KIND.mainDocument) {
  if (kind === NAVIGATION_KIND.childFrame || kind === NAVIGATION_KIND.subresource) {
    return { trusted: false, mainDocument: false, code: ORIGIN_CODES.notMainDocument, origin: null }
  }
  const parsed = parseRemoteOrigin(uri)
  if (!parsed.ok || parsed.origin === null) {
    return { trusted: false, mainDocument: true, code: parsed.code, origin: null }
  }
  if (kind === NAVIGATION_KIND.externalLink) {
    return { trusted: false, mainDocument: true, code: ORIGIN_CODES.externalNavigation, origin: parsed.origin }
  }
  if (trustedOrigin === null || trustedOrigin === undefined) {
    return { trusted: false, mainDocument: true, code: ORIGIN_CODES.invalid, origin: parsed.origin }
  }
  const trusted = originMatches(trustedOrigin, parsed.origin)
  return {
    trusted,
    mainDocument: true,
    code: trusted ? ORIGIN_CODES.trusted : ORIGIN_CODES.external,
    origin: parsed.origin
  }
}

/** 便捷入口：只判定顶层文档 URL 是否等于可信来源。 */
export function isTrustedMainDocument(trustedOrigin, documentUri) {
  return evaluateNavigation(trustedOrigin, documentUri, NAVIGATION_KIND.mainDocument).trusted
}

/**
 * D20 的来源向量表。`trustedOriginUrl` 是本次运行的真实夹具来源（LAN:3099）。
 *
 * `hostShape` 说明该向量在哪种主机形态下**有定义**：
 *   - `dns`：主机是 DNS 名（`our.host`）。前缀伪装 `evil-our.host`、结尾根点
 *     `our.host.` 只有在这种形态下才是合法 URL；
 *   - `ip`：主机是 IP 字面量（夹具探测到的 `192.168.x.y`）。WHATWG URL 对
 *     `evil-192.168.3.190`（以数字结尾）会走 IPv4 解析并整体拒绝，因此那条向量
 *     在 IP 形态下**没有定义**，必须显式跳过而不是改判成另一个码。
 *   - `any`：与主机形态无关。
 *
 * 每条都给**结论**（ok / code / normalized / trusted），而不是只给形态描述 ——
 * 这样"结论变了"能立刻在证据里看出来。
 */
export function originVectors(trustedOriginUrl) {
  const trusted = parseRemoteOrigin(trustedOriginUrl).origin
  const host = trusted?.host ?? 'our.host'
  const port = String(trusted?.port ?? 3099)
  const base = `${trusted?.scheme ?? 'http'}://${host}:${port}`
  const kind = NAVIGATION_KIND
  return [
    // --- 正向对照：真实的配对来源必须被认成可信主文档 ---
    { id: 'V01-trusted-origin-exact', hostShape: 'any', url: `${base}/pair-app?device=x`, kind: kind.mainDocument, ok: true, normalized: `${base}`, trusted: true, code: ORIGIN_CODES.trusted },
    // --- userinfo 伪装：凭据不是来源的一部分，但一律不当可信页面 ---
    { id: 'V02-userinfo-evil-at-our-host', hostShape: 'any', url: `http://evil.example@${host}:${port}/pair-app`, kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.userInfoRejected, trusted: false },
    { id: 'V03-userinfo-with-password', hostShape: 'any', url: `http://evil:secret@${host}:${port}/`, kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.userInfoRejected, trusted: false },
    { id: 'V04-userinfo-uppercase-scheme', hostShape: 'any', url: `HTTP://evil@${host}:${port}/`, kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.userInfoRejected, trusted: false },
    // --- 后缀 / 前缀主机伪装：绝不做前缀或后缀匹配 ---
    { id: 'V05-suffix-host', hostShape: 'any', url: `http://${host}.evil.example:${port}/`, kind: kind.mainDocument, ok: true, normalized: `http://${host}.evil.example:${port}`, trusted: false, code: ORIGIN_CODES.external },
    { id: 'V06-prefix-host', hostShape: 'dns', url: `http://evil-${host}:${port}/`, kind: kind.mainDocument, ok: true, normalized: `http://evil-${host}:${port}`, trusted: false, code: ORIGIN_CODES.external },
    { id: 'V07-userinfo-lookalike-host', hostShape: 'any', url: `http://${host}.evil.example@${host}:${port}/`, kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.userInfoRejected, trusted: false },
    // --- IPv6 字面量：规范化后比较，越界内容一律拒绝 ---
    { id: 'V08-ipv6-expanded-loopback', hostShape: 'any', url: 'http://[0:0:0:0:0:0:0:1]:3099/', kind: kind.mainDocument, ok: true, normalized: 'http://[::1]:3099', trusted: false, code: ORIGIN_CODES.external },
    { id: 'V09-ipv6-compressed-loopback', hostShape: 'any', url: 'http://[::1]:3099/', kind: kind.mainDocument, ok: true, normalized: 'http://[::1]:3099', trusted: false, code: ORIGIN_CODES.external },
    { id: 'V10-ipv6-bracket-then-suffix', hostShape: 'any', url: 'http://[::1].evil.example/', kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.invalid, trusted: false },
    { id: 'V11-ipv6-bracket-then-host', hostShape: 'any', url: 'http://[::1]evil.example/', kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.invalid, trusted: false },
    { id: 'V12-ipv6-bad-port-text', hostShape: 'any', url: 'http://[::1]:abc/', kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.invalid, trusted: false },
    { id: 'V13-ipv6-unclosed-bracket', hostShape: 'any', url: 'http://[::1/', kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.invalid, trusted: false },
    { id: 'V14-ipv6-mapped-v4-canonical', hostShape: 'any', url: 'http://[0:0:0:0:0:ffff:1.2.3.4]:3099/', kind: kind.mainDocument, ok: true, normalized: 'http://[::ffff:1.2.3.4]:3099', trusted: false, code: ORIGIN_CODES.external },
    // --- 端口 / 协议 / 结尾根点 ---
    { id: 'V15-default-port-elided', hostShape: 'any', url: `http://${host}:80/`, kind: kind.mainDocument, ok: true, normalized: `http://${host}`, trusted: false, code: ORIGIN_CODES.external },
    { id: 'V16-other-port-is-other-origin', hostShape: 'any', url: `http://${host}:3097/`, kind: kind.mainDocument, ok: true, normalized: `http://${host}:3097`, trusted: false, code: ORIGIN_CODES.external },
    { id: 'V17-scheme-change-is-other-origin', hostShape: 'any', url: `https://${host}:${port}/`, kind: kind.mainDocument, ok: true, normalized: `https://${host}:${port}`, trusted: false, code: ORIGIN_CODES.external },
    { id: 'V18-trailing-root-dot-same', hostShape: 'dns', url: `http://${host}.:${port}/`, kind: kind.mainDocument, ok: true, normalized: `${base}`, trusted: true, code: ORIGIN_CODES.trusted },
    // --- 非 http/https 与相对 URL ---
    { id: 'V19-file-scheme', hostShape: 'any', url: 'file:///etc/passwd', kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.unsupportedScheme, trusted: false },
    { id: 'V20-javascript-scheme', hostShape: 'any', url: 'javascript:alert(1)', kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.unsupportedScheme, trusted: false },
    { id: 'V21-about-blank', hostShape: 'any', url: 'about:blank', kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.unsupportedScheme, trusted: false },
    { id: 'V22-relative-url', hostShape: 'any', url: '/pair-app?device=x', kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.invalid, trusted: false },
    { id: 'V23-empty-url', hostShape: 'any', url: '', kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.invalid, trusted: false },
    { id: 'V24-data-url', hostShape: 'any', url: 'data:text/html,<script>1</script>', kind: kind.mainDocument, ok: false, code: ORIGIN_CODES.unsupportedScheme, trusted: false },
    // --- 非主文档：既不授予也不撤销能力 ---
    { id: 'V25-child-frame-same-origin', hostShape: 'any', url: `${base}/pair-app`, kind: kind.childFrame, ok: false, code: ORIGIN_CODES.notMainDocument, trusted: false, mainDocument: false },
    { id: 'V26-subresource-same-origin', hostShape: 'any', url: `${base}/assets/app.js`, kind: kind.subresource, ok: false, code: ORIGIN_CODES.notMainDocument, trusted: false, mainDocument: false },
    { id: 'V27-child-frame-evil-host', hostShape: 'any', url: 'http://evil.example/', kind: kind.childFrame, ok: false, code: ORIGIN_CODES.notMainDocument, trusted: false, mainDocument: false },
    // --- 外部链接导航：即便落在同一来源也不继承文件能力 ---
    { id: 'V28-external-link-same-origin', hostShape: 'any', url: `${base}/pair-app`, kind: kind.externalLink, ok: true, normalized: `${base}`, trusted: false, code: ORIGIN_CODES.externalNavigation },
    { id: 'V29-external-link-userinfo', hostShape: 'any', url: `http://evil@${host}:${port}/`, kind: kind.externalLink, ok: false, code: ORIGIN_CODES.userInfoRejected, trusted: false },
    // --- 目标地址本身不可信时失败关闭 ---
    { id: 'V30-null-trusted-origin', hostShape: 'any', url: `${base}/pair-app`, kind: kind.mainDocument, trustedOrigin: null, ok: true, normalized: `${base}`, trusted: false, code: ORIGIN_CODES.invalid }
  ]
}

/** 主机形态：DNS 名 vs IP 字面量。决定哪些向量有定义。 */
export function hostShapeOf(host) {
  return /^[0-9.]+$/.test(host) ? 'ip' : 'dns'
}

/**
 * 对一条向量跑判定，返回可机器比对的结论。
 * `hostShape` 不匹配时返回 `skipped`（不参与成败），reason 写清"为什么这条形态下没定义"。
 */
export function runOriginVector(vector, fallbackTrustedOriginUrl) {
  const trustedUrl = vector.trustedOrigin === null ? null : (vector.trustedOrigin ?? fallbackTrustedOriginUrl)
  const shape = hostShapeOf(parseRemoteOrigin(trustedUrl ?? fallbackTrustedOriginUrl).origin?.host ?? '')
  if (vector.hostShape !== 'any' && vector.hostShape !== shape) {
    return {
      id: vector.id,
      url: vector.url,
      kind: vector.kind,
      skipped: true,
      reason: `该向量只在 ${vector.hostShape} 主机形态下有定义，本次可信来源主机形态是 ${shape}`,
      ok: true
    }
  }
  const trusted = trustedUrl === null ? null : parseRemoteOrigin(trustedUrl).origin
  const verdict = evaluateNavigation(trusted, vector.url, vector.kind)
  const expectedMainDocument = vector.mainDocument ?? true
  const checks = {
    ok: verdict.origin !== null,
    code: verdict.code,
    normalized: verdict.origin?.normalized ?? null,
    trusted: verdict.trusted,
    mainDocument: verdict.mainDocument
  }
  const expected = {
    ok: vector.ok,
    code: vector.code,
    normalized: vector.normalized ?? null,
    trusted: vector.trusted,
    mainDocument: expectedMainDocument
  }
  // 只比较该向量声明过的字段：未声明的字段不做断言，避免"顺手断言了没设计的语义"。
  const compared = ['code', 'trusted', 'mainDocument']
  if (vector.ok !== undefined) compared.push('ok')
  if (vector.normalized !== undefined) compared.push('normalized')
  const mismatches = compared.filter((key) => checks[key] !== expected[key])
  return { id: vector.id, url: vector.url, kind: vector.kind, skipped: false, checks, expected, compared, mismatches, ok: mismatches.length === 0 }
}
