/**
 * D08 根因回归门：client 入口产物的**服务形态契约**。
 *
 * 背景（详见 docs/remote-file-paste/execution/rounds/R09-D08.md）：client-modules 把同一
 * phase 的各包 client.js **原始字节**首尾相接（`comboSource()` 不做 ESM→CJS 变换），再由
 * `defaultLoadBundle()` 以**经典脚本**动态加载。所以入口顶层出现任何 `import`/`export`
 * 都会让**整批**（实测 14.6 MB、56 个包）变成语法错误，页面直接
 * "Failed to load plugins"，composer 永不渲染——这正是 D08 被阻断的真实原因。
 *
 * 这里断言构建产物本身满足契约，而不是断言"构建脚本跑了"。
 */

import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { createRequire } from 'node:module'
import { dirname, join, resolve } from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import { evaluateClientBundle } from '../support/client-bundle.mjs'

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const require = createRequire(import.meta.url)
const manifest = require(join(projectRoot, 'package.json'))

const bundle = await readFile(join(projectRoot, 'lib/client.js'), 'utf8')

test('client 入口产物顶层不出现任何 import/export（否则会污染整批）', () => {
  const offenders = bundle
    .split('\n')
    .map((text, index) => ({ line: index + 1, text }))
    .filter((row) => /^(import|export)[\s{*]/.test(row.text))
  assert.deepEqual(
    offenders.map((row) => `${row.line}: ${row.text.slice(0, 80)}`),
    []
  )
})

test('client 入口产物恰好注册一次，且 id 等于包名', () => {
  const registrations = bundle
    .split('\n')
    .filter((line) => line.startsWith('window.__ModuleLoader__.load(')).length
  assert.equal(registrations, 1)
  const result = evaluateClientBundle(bundle, manifest.name)
  assert.equal(result.registrations, 1)
  assert.equal(result.id, manifest.name)
  assert.equal(result.ok, true, result.detail)
})

test('注册期不触碰 document/navigator，且在无外部依赖时也能物化出插件面', () => {
  const result = evaluateClientBundle(bundle, manifest.name)
  assert.equal(result.ok, true, result.detail)
  assert.equal(typeof result.face.apply, 'function')
  assert.equal(result.face.name, 'remote-attachments-client')
  // 物化必须自洽：本包不向外层 loader 要任何未内联的依赖。
  assert.match(result.detail, /外泄依赖=0/)
})

test('物化出的导出面与 ESM 源形态逐键一致（构建不是空壳）', async () => {
  const result = evaluateClientBundle(bundle, manifest.name)
  const esm = await import(join(projectRoot, 'lib/client/index.js'))
  assert.deepEqual(Object.keys(result.face).sort(), Object.keys(esm).sort())
  for (const key of Object.keys(esm)) {
    if (typeof esm[key] === 'function') assert.equal(typeof result.face[key], 'function', key)
  }
})

test('物化出的客户端能力快照与源形态结果相同（真的执行了模块体）', async () => {
  const result = evaluateClientBundle(bundle, manifest.name)
  const esm = await import(join(projectRoot, 'lib/client/index.js'))
  // 产物在 vm 的另一 realm 里物化，原型不同，因此比较序列化后的形状。
  assert.equal(JSON.stringify(result.face.apply({})), JSON.stringify(esm.apply({})))
})

test('产物能被 JS 引擎解析（语法自检）', () => {
  const vm = require('node:vm')
  assert.doesNotThrow(() => new vm.Script(bundle, { filename: 'lib/client.js' }))
})

test('按 cordis 约定调用 apply(ctx, config)：ctx 代理上的未知属性不得被读取', () => {
  const result = evaluateClientBundle(bundle, manifest.name)
  // 复刻 cordis 的 ctx 代理行为：未知属性抛 `cannot get property "…" without inject`。
  // 曾经的缺陷是把第一个参数当 config 用，于是 apply(ctx, …) 会去读 ctx.enabled 并抛错，
  // 导致整个 loader entry 挂载失败（D08 实测）。
  // 注意 `get` 是 cordis ctx 上**合法存在**的方法（服务解析入口），必须保留；只有未知属性才抛。
  const strictCtx = new Proxy(
    { logger: undefined, get: () => undefined },
    {
      get(target, property) {
        if (property === 'logger' || property === 'get' || typeof property === 'symbol') {
          return Reflect.get(target, property)
        }
        throw new Error(`cannot get property "${String(property)}" without inject`)
      }
    }
  )
  assert.doesNotThrow(() => result.face.apply(strictCtx))
  // 配置来自第二个参数：显式传入时与缺省结果形状一致（当前依赖缺失，enabled 尚不改变结论）。
  assert.equal(
    JSON.stringify(result.face.apply(strictCtx, { enabled: false })),
    JSON.stringify(result.face.apply(strictCtx))
  )
})
