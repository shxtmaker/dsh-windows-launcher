/**
 * D07 单元测试：启动前上传承载的脚本契约。
 *
 * 这里只断言脚本的**文本契约**（不得提前捕获裸 fetch、不得预拼 /remote、
 * 必须用 Headers、必须保留 signal、不得覆盖未知 hook）；行为断言在真实
 * Chromium 中完成（tests/fixtures/upload-gates.mjs）。
 *
 * D13 追加：把脚本放进 `node:vm` 的受控环境里**真的执行**，验证
 *   - 局域网页面没有 remote 改写层时**拒绝**上传（不回退裸 /api，且一次 fetch 都不发）；
 *   - 有改写层时照常发出原始同源 `/api/...`（不预拼 /remote）；
 *   - loopback 页面按本机直连放行；
 *   - 座位的品牌字段能被 host 侧识别为"本插件所有"。
 */

import assert from 'node:assert/strict'
import test from 'node:test'
import vm from 'node:vm'

import {
  CARRIER_BRAND,
  REMOTE_SEAT_GLOBAL,
  UPLOAD_ENDPOINT,
  UPLOAD_HOOK_GLOBAL,
  UPLOAD_HOOK_VERSION,
  buildUploadHookScript,
  classifyHookInstallation
} from '../../lib/host/upload-hook.js'

const script = buildUploadHookScript()

/**
 * 在受控上下文里执行承载脚本，返回页面里的假全局与 fetch 记录。
 * @param {{hostname?: string, seat?: boolean, unknownHook?: unknown}} [options]
 */
function runScript({ hostname = '192.168.3.190', seat = false, unknownHook } = {}) {
  const calls = []
  const sandbox = {
    console,
    URL,
    Headers,
    location: { hostname, href: `http://${hostname}:3099/pair-app` },
    fetch: (input, init) => {
      calls.push({ input: String(input), init })
      return Promise.resolve({ ok: true, status: 200 })
    }
  }
  sandbox.globalThis = sandbox
  if (seat) sandbox[REMOTE_SEAT_GLOBAL] = { restore: () => {}, onUnpaired: null, onPaired: null }
  if (unknownHook !== undefined) sandbox[UPLOAD_HOOK_GLOBAL] = unknownHook
  vm.createContext(sandbox)
  new vm.Script(script, { filename: 'upload-hook.js' }).runInContext(sandbox)
  return { sandbox, calls }
}

test('承载脚本安装到约定的全局名', () => {
  assert.equal(UPLOAD_HOOK_GLOBAL, '__DSH_FILE_UPLOAD__')
  assert.ok(script.includes(JSON.stringify(UPLOAD_HOOK_GLOBAL)))
})

test('上传目标保持原始同源 /api 路径，不预拼 /remote', () => {
  assert.equal(UPLOAD_ENDPOINT, '/api/session/uploadFileBinary')
  // 脚本正文不得出现 /remote 前缀：改写由 remote 的 boot 脚本独占完成，
  // 预拼会导致既不注入设备头、又被重复改写。
  assert.ok(!script.includes('/remote/'))
  assert.ok(!script.includes("'/remote'"))
})

test('调用时才读取 window.fetch，不提前捕获裸 fetch', () => {
  // 必须在函数体内部读取 w.fetch（运行时），而不是在安装时保存一份引用。
  assert.ok(script.includes('var f=w.fetch'))
  const carrierIndex = script.indexOf('function carrier(input,init)')
  const readIndex = script.indexOf('var f=w.fetch')
  const installIndex = script.indexOf('w[G]={fetch:carrier}')
  assert.ok(carrierIndex > 0 && readIndex > carrierIndex, '应在载体函数体内读取 fetch')
  assert.ok(installIndex > readIndex, '安装点只把载体挂上全局')
  // 安装点之后不得再出现对 fetch 的读取：证明安装时没有捕获裸 fetch。
  assert.ok(!script.slice(installIndex).includes('w.fetch'), '安装时不得捕获裸 fetch')
})

test('统一使用 Headers 承载头部（remote 只对 Headers 实例 set 设备头）', () => {
  assert.ok(script.includes('new Headers('))
  assert.ok(script.includes('headers:headers'))
})

test('保留 signal，且未提供时不显式传 undefined', () => {
  assert.ok(script.includes('if(nextInit.signal!==undefined)out.signal=nextInit.signal'))
  assert.ok(script.includes('if(out[k]===undefined)delete out[k]'))
})

test('不解析响应信封：HTTP 200 业务失败交给调用方', () => {
  // 不得出现 json()/ok===false 之类的响应体解析
  assert.ok(!script.includes('.json()'))
  assert.ok(!script.includes('ok===false'))
  assert.ok(!script.includes('ok === false'))
})

test('遇到未知全局时保留原值并登记 conflict，不覆盖', () => {
  assert.ok(script.includes("if(seat!==undefined&&seat!==null)"))
  assert.ok(script.includes("uploadHook='conflict'"))
  assert.ok(script.includes("uploadHook='installed'"))
  // 提前 return，确保不会执行到安装分支
  const conflictIndex = script.indexOf("uploadHook='conflict'")
  const installIndex = script.indexOf('w[G]={fetch:carrier}')
  assert.ok(conflictIndex > 0 && conflictIndex < installIndex, 'conflict 分支必须在安装之前')
})

test('classifyHookInstallation 只在未占用时判定为可安装', () => {
  assert.equal(classifyHookInstallation(undefined), 'install')
  assert.equal(classifyHookInstallation(null), 'install')
  assert.equal(classifyHookInstallation(() => {}), 'conflict')
  assert.equal(classifyHookInstallation({}), 'conflict')
})

test('装成 fetch 形状的载体（消费者读 hook.fetch，不是裸函数）', () => {
  // 固定版本 @deepseek-ai/dsh-client-file-upload@0.1.5-rc.2/lib/client.js:161 读的是
  // hook.fetch；装成裸函数会让 customTransport(undefined)，文本附件上传必然失败（D09 实测）。
  assert.ok(script.includes('w[G]={fetch:carrier}'), '必须安装为 { fetch: carrier }')
  assert.ok(script.includes('function carrier(input,init)'), '载体函数必须存在')
})

test('脚本是自包含 IIFE，可被内联到 head 直接执行', () => {
  assert.ok(script.startsWith('(function(){'))
  assert.ok(script.trimEnd().endsWith('})();'))
  // 不得含模块语法（内联脚本里不可用）
  assert.ok(!/^\s*(import|export)\s/m.test(script))
})

// ————————————————————————————————————————————————————————————
// D13：行为断言（脚本真的在受控上下文里跑）
// ————————————————————————————————————————————————————————————

test('D13 归属：座位带本插件品牌，且 classifyHookInstallation 认得出"自己的"', () => {
  const { sandbox } = runScript({ seat: true })
  assert.equal(sandbox[UPLOAD_HOOK_GLOBAL].brand, CARRIER_BRAND)
  assert.equal(typeof sandbox[UPLOAD_HOOK_GLOBAL].fetch, 'function')
  assert.equal(sandbox.__DSH_ATTACHMENTS_STATUS__.uploadHook, 'installed')
  assert.equal(sandbox.__DSH_ATTACHMENTS_STATUS__.host.hookVersion, UPLOAD_HOOK_VERSION)
  assert.equal(sandbox.__DSH_ATTACHMENTS_STATUS__.host.carrierShape, 'fetch')

  assert.equal(classifyHookInstallation({ fetch: () => {}, brand: CARRIER_BRAND }), 'owned')
  assert.equal(classifyHookInstallation({ fetch: () => {}, brand: 'someone-else/1' }), 'conflict')
  assert.equal(classifyHookInstallation({ fetch: () => {} }), 'conflict')
  assert.equal(classifyHookInstallation(undefined), 'install')
})

test('D13 不回退裸 /api：局域网页面没有改写层时拒绝上传，且一次 fetch 都不发', async () => {
  const { sandbox, calls } = runScript({ hostname: '192.168.3.190', seat: false })
  assert.equal(sandbox[UPLOAD_HOOK_GLOBAL].brand, CARRIER_BRAND)
  await assert.rejects(
    () => sandbox[UPLOAD_HOOK_GLOBAL].fetch(UPLOAD_ENDPOINT, { method: 'POST' }),
    (error) => {
      assert.equal(error.code, 'capability-disabled')
      assert.match(error.message, /capability-disabled/)
      return true
    }
  )
  assert.equal(calls.length, 0, '拒绝路径必须一个请求都不发（没有裸 /api 回退）')
  assert.equal(sandbox.__DSH_ATTACHMENTS_STATUS__.uploadRoute, 'refused-no-remote-channel')
})

test('D13 有改写层时：发出的是原始同源 /api 路径（不预拼 /remote），路由标记为 remote-rewrite', async () => {
  const { sandbox, calls } = runScript({ hostname: '192.168.3.190', seat: true })
  const response = await sandbox[UPLOAD_HOOK_GLOBAL].fetch(UPLOAD_ENDPOINT, { method: 'POST', body: 'x' })
  assert.equal(response.status, 200)
  assert.equal(calls.length, 1)
  assert.equal(calls[0].input, `http://192.168.3.190:3099${UPLOAD_ENDPOINT}`, '必须是原始同源路径')
  assert.ok(!calls[0].input.includes('/remote/'), '不得自行预拼 remote 前缀')
  assert.equal(sandbox.__DSH_ATTACHMENTS_STATUS__.uploadRoute, 'remote-rewrite')
})

test('D13 loopback 页面按本机直连放行（remote 的 boot 脚本在 loopback 上按设计跳过）', async () => {
  const { sandbox, calls } = runScript({ hostname: '127.0.0.1', seat: false })
  const response = await sandbox[UPLOAD_HOOK_GLOBAL].fetch(UPLOAD_ENDPOINT, { method: 'POST' })
  assert.equal(response.status, 200)
  assert.equal(calls.length, 1, 'loopback 上不得因为"没有 seat"而拒绝')
  assert.equal(sandbox.__DSH_ATTACHMENTS_STATUS__.uploadRoute, 'loopback-direct')
})

test('D13 未知 hook：绝不覆盖、绝不删改，只登记 conflict 且不安装品牌', () => {
  const unknown = { fetch: () => Promise.resolve({ ok: true }), note: 'unknown' }
  const { sandbox } = runScript({ unknownHook: unknown })
  assert.equal(sandbox[UPLOAD_HOOK_GLOBAL], unknown)
  assert.equal(sandbox[UPLOAD_HOOK_GLOBAL].brand, undefined, '不得给别人的 hook 贴本插件品牌')
  assert.equal(sandbox.__DSH_ATTACHMENTS_STATUS__.uploadHook, 'conflict')
  assert.equal(sandbox.__DSH_ATTACHMENTS_STATUS__.host, undefined, '冲突时不得声称已安装')
})
