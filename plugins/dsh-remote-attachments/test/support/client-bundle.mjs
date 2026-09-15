/**
 * client 入口产物（`lib/client.js`）的求值夹具：在 Node 里模拟 client-modules 的加载环境。
 *
 * 契约（D08 根因，见 docs/remote-file-paste/execution/rounds/R09-D08.md）：client 入口必须是
 * 经典脚本，顶层只做一次
 *
 * ```js
 * window.__ModuleLoader__.load({ id: '<包名>', factory: (require) => … })
 * ```
 *
 * 注册，绝对不能在顶层出现 `import`/`export`——否则它会把 client-modules 拼出的**整批**
 * 变成语法错误。这里提供最小的 `window.__ModuleLoader__`，**不**提供 document/navigator，
 * 因此任何注册期触碰浏览器全局的行为都会以 ReferenceError 暴露出来。
 */

import vm from 'node:vm'

/**
 * 在最小加载环境里求值 client 入口产物。
 * @param {string} code - `lib/client.js` 的文本。
 * @param {string | undefined} expectedId - 期望的注册 id（通常是包名）。
 * @returns {{ ok: boolean, id: string | null, registrations: number, face: Record<string, unknown> | null, detail: string }}
 */
export function evaluateClientBundle(code, expectedId) {
  const captured = []
  const sandbox = { window: { __ModuleLoader__: { load: (registration) => captured.push(registration) } } }
  vm.createContext(sandbox)
  new vm.Script(code, { filename: 'lib/client.js' }).runInContext(sandbox)

  if (captured.length !== 1) {
    return {
      ok: false,
      id: captured[0]?.id ?? null,
      registrations: captured.length,
      face: null,
      detail: `注册次数应为 1，实际 ${captured.length}`
    }
  }
  const registration = captured[0]
  const idMatches = expectedId === undefined || registration.id === expectedId
  if (typeof registration.factory !== 'function') {
    return { ok: false, id: registration.id, registrations: 1, face: null, detail: 'factory 不是函数' }
  }

  // 本包不依赖任何外部 client 包；一旦 factory 向外层 loader 要东西就说明构建漏内联了。
  const leaked = []
  let face
  try {
    face = registration.factory((specifier) => {
      leaked.push(specifier)
      throw new Error(`client bundle 向外层 loader 请求了未内联的依赖：${specifier}`)
    })
  } catch (error) {
    return { ok: false, id: registration.id, registrations: 1, face: null, detail: `factory 求值失败：${error.message}` }
  }

  const hasApply = typeof face?.apply === 'function'
  const hasName = typeof face?.name === 'string'
  const ok = idMatches && leaked.length === 0 && hasApply && hasName
  return {
    ok,
    id: registration.id,
    registrations: 1,
    face,
    detail: `id匹配=${idMatches} apply=${hasApply} name=${hasName} 外泄依赖=${leaked.length}`
  }
}
