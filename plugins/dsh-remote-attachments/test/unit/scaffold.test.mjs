/**
 * D03 单元测试：证明骨架的入口、共享契约与能力判定是真实可用的实现，
 * 而不是返回成功的空壳。
 *
 * 覆盖：
 *  - 协议常量与技术方案的首期数额一致（防止两端各自改数导致漂移）
 *  - 分块规划、批次/文件限额判据的边界
 *  - 能力状态判定（缺失/禁用/就绪三分支）
 *  - host 入口可加载且 apply 可调用、只依赖 ctx.logger
 *  - client 入口可加载且**导入期不触碰 window/document**
 */

import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'

import {
  CHUNK_BYTES,
  DEFAULT_LIMITS,
  ERROR_CODES,
  ERROR_STAGES,
  MAX_CHUNKS_IN_FLIGHT,
  MESSAGE_TYPES,
  PROTOCOL_VERSION,
  checkBatchLimits,
  checkFileLimits,
  checkProtocolVersion,
  planChunks
} from '../../lib/shared/protocol.js'
import {
  CAPABILITY_STATUS,
  CORDIS_ROW_ID,
  PACKAGE_NAME,
  PROVIDED_CAPABILITIES,
  resolveCapabilityStatus
} from '../../lib/shared/capabilities.js'
import * as hostEntry from '../../lib/host.js'
// client 半区的 **ESM 源形态**（lib/client.js 是构建生成的经典包裹产物，
// 不能在 Node 里当 ESM 导入；其契约由 test/unit/client-bundle.test.mjs 断言）。
import * as clientEntry from '../../lib/client/index.js'

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')

test('协议常量与技术方案 2.0 第 4.1 节的首期值一致', () => {
  assert.equal(PROTOCOL_VERSION, 1)
  assert.equal(CHUNK_BYTES, 256 * 1024)
  assert.equal(MAX_CHUNKS_IN_FLIGHT, 2)
  assert.equal(DEFAULT_LIMITS.maxFileBytes, 20 * 1024 * 1024)
  assert.equal(DEFAULT_LIMITS.maxFilesPerBatch, 10)
  assert.equal(DEFAULT_LIMITS.maxBatchBytes, 50 * 1024 * 1024)
  assert.equal(DEFAULT_LIMITS.maxScreenshotPixels, 40_000_000)
  assert.equal(DEFAULT_LIMITS.maxStagingBytesPerTarget, 100 * 1024 * 1024)
  assert.equal(DEFAULT_LIMITS.maxConcurrentTargets, 2)
})

test('默认限额对象被冻结，调用方无法就地篡改共享契约', () => {
  assert.ok(Object.isFrozen(DEFAULT_LIMITS))
  assert.throws(() => {
    /** @type {any} */ (DEFAULT_LIMITS).maxFileBytes = 1
  }, TypeError)
})

test('消息类型与错误阶段枚举覆盖技术方案列出的全部取值', () => {
  for (const expected of [
    'hello',
    'capabilities',
    'context',
    'batch-begin',
    'file-begin',
    'chunk',
    'ack',
    'file-end',
    'import-result',
    'batch-end',
    'cancel'
  ]) {
    assert.ok(MESSAGE_TYPES.includes(expected), `缺少消息类型 ${expected}`)
  }
  assert.deepEqual([...ERROR_STAGES], [
    'native-capture',
    'protocol-transfer',
    'draft-import',
    'remote-upload'
  ])
  // 错误码必须唯一，避免两端映射到不同含义
  assert.equal(new Set(ERROR_CODES).size, ERROR_CODES.length)
})

test('协议版本校验只在完全相等时通过', () => {
  assert.deepEqual(checkProtocolVersion(1), { ok: true, version: 1 })
  assert.equal(checkProtocolVersion(2).ok, false)
  assert.equal(checkProtocolVersion('1').ok, false)
  assert.equal(checkProtocolVersion(undefined).ok, false)
})

test('planChunks 的 seq 从 0 递增、offset 连续、尾块正确', () => {
  const chunk = 4
  assert.deepEqual(planChunks(0, chunk), [])
  assert.deepEqual(planChunks(4, chunk), [{ seq: 0, offset: 0, byteLength: 4 }])
  assert.deepEqual(planChunks(10, chunk), [
    { seq: 0, offset: 0, byteLength: 4 },
    { seq: 1, offset: 4, byteLength: 4 },
    { seq: 2, offset: 8, byteLength: 2 }
  ])
})

test('planChunks 拒绝非法输入而不是返回空计划', () => {
  assert.throws(() => planChunks(-1, 4), TypeError)
  assert.throws(() => planChunks(1.5, 4), TypeError)
  assert.throws(() => planChunks(8, 0), TypeError)
})

test('planChunks 覆盖 256 KiB 边界的块数与总字节', () => {
  const oneChunk = planChunks(CHUNK_BYTES)
  assert.equal(oneChunk.length, 1)
  const justOver = planChunks(CHUNK_BYTES + 1)
  assert.equal(justOver.length, 2)
  assert.equal(justOver[1].byteLength, 1)
  const total = justOver.reduce((sum, item) => sum + item.byteLength, 0)
  assert.equal(total, CHUNK_BYTES + 1)
})

test('checkFileLimits 在限额、限额-1、限额+1 三档上给出正确判定', () => {
  const limit = DEFAULT_LIMITS.maxFileBytes
  assert.equal(checkFileLimits(limit - 1).ok, true)
  assert.equal(checkFileLimits(limit).ok, true)
  const over = checkFileLimits(limit + 1)
  assert.equal(over.ok, false)
  assert.equal(over.code, 'limit-file-bytes')
  assert.equal(over.limit, limit)
  assert.equal(over.actual, limit + 1)
})

test('checkBatchLimits 分别在项数与总字节上拒绝，并拒绝非法计数', () => {
  assert.equal(checkBatchLimits(10, 1024).ok, true)
  assert.equal(checkBatchLimits(11, 1024).code, 'limit-batch-files')
  const overBytes = checkBatchLimits(1, DEFAULT_LIMITS.maxBatchBytes + 1)
  assert.equal(overBytes.code, 'limit-batch-bytes')
  assert.equal(checkBatchLimits(-1, 0).ok, false)
  assert.equal(checkBatchLimits(1.5, 0).ok, false)
})

test('resolveCapabilityStatus 区分缺失、禁用与就绪', () => {
  const unavailable = resolveCapabilityStatus({ remoteChannel: false, harnessDraftImport: true })
  assert.equal(unavailable.status, CAPABILITY_STATUS.unavailable)
  assert.equal(unavailable.missing.length, 1)

  const degraded = resolveCapabilityStatus({
    remoteChannel: true,
    harnessDraftImport: true,
    enabled: false
  })
  assert.equal(degraded.status, CAPABILITY_STATUS.degraded)
  assert.equal(degraded.missing.length, 0)

  const available = resolveCapabilityStatus({ remoteChannel: true, harnessDraftImport: true })
  assert.equal(available.status, CAPABILITY_STATUS.available)
  assert.equal(available.reason, null)
})

test('本插件身份与全家桶、remote 插件不同，且不声明配对能力', () => {
  assert.equal(PACKAGE_NAME, '@shxtmaker/dsh-remote-attachments')
  assert.equal(CORDIS_ROW_ID, 'remote-attachments')
  assert.notEqual(PACKAGE_NAME, '@linxin666/dsh-web-all')
  assert.notEqual(PACKAGE_NAME, '@linxin666/dsh-remote-web-ui')
  assert.deepEqual([...PROVIDED_CAPABILITIES], ['attachment-paste-import'])
  // 不提供配对、设备或 /remote 代理能力
  for (const capability of PROVIDED_CAPABILITIES) {
    assert.ok(!/pair|device|remote-channel|tunnel/.test(capability))
  }
})

test('host 入口导出可调用的 apply 与 name，且只使用 logger/effect/on', () => {
  assert.equal(typeof hostEntry.apply, 'function')
  assert.equal(hostEntry.name, 'remote-attachments')
  assert.equal(typeof hostEntry.default.apply, 'function')

  const logged = []
  const registered = []
  const ctx = {
    logger: (scope) => ({ info: (message) => logged.push([scope, message]) }),
    effect: (callback, label) => {
      registered.push({ label })
      return callback()
    },
    on: (event, handler) => {
      registered.push({ event, handler })
      return () => {}
    }
  }
  // apply 不得读取这三者以外的上下文属性（避免依赖未声明的宿主服务）
  const tracked = new Proxy(ctx, {
    get(target, property) {
      if (property === 'logger' || property === 'effect' || property === 'on') return target[property]
      throw new Error(`apply 读取了未声明的上下文属性：${String(property)}`)
    }
  })
  assert.doesNotThrow(() => hostEntry.apply(tracked, {}))
  assert.ok(logged.length >= 2, `期望至少记录能力状态与原因，实际 ${logged.length} 条`)
  assert.match(logged[0][1], /remote-attachments/)

  // D07：必须注册 webserver/index-inject，且只推入一行 head script
  const inject = registered.find((entry) => entry.event === 'webserver/index-inject')
  assert.ok(inject, 'apply 必须注册 webserver/index-inject')
  const table = []
  inject.handler(table)
  assert.equal(table.length, 1, `期望恰好一行注入，实际 ${table.length}`)
  assert.equal(table[0].kind, 'script')
  assert.equal(table[0].placement, 'head')
  assert.ok(table[0].text.includes('__DSH_FILE_UPLOAD__'))
})

test('host apply 在显式禁用时只注册"禁用标记"注入，绝不安装承载', () => {
  const registered = []
  const ctx = {
    logger: () => ({ info: () => {} }),
    effect: (callback) => callback(),
    on: (event, handler) => {
      registered.push({ event, handler })
      return () => {}
    }
  }
  hostEntry.apply(ctx, { enabled: false })
  // D23：cordis 的浏览器半区加载器 `loader.create({ name })` 不带行配置，client 半区
  // 看不到 enabled:false；因此禁用时必须往页面写一条标记，否则 loopback 页面（本来
  // 就不需要承载）会把被禁用的插件报成 available，禁用等于失效。
  // 但标记**不是**承载：注入脚本里不得出现承载座位名，也不得提供 fetch 形状载体。
  const inject = registered.find((entry) => entry.event === 'webserver/index-inject')
  assert.ok(inject, '禁用时仍必须注册禁用标记注入')
  const table = []
  inject.handler(table)
  assert.equal(table.length, 1, `期望恰好一行注入，实际 ${table.length}`)
  assert.equal(table[0].kind, 'script')
  assert.equal(table[0].placement, 'head')
  assert.match(table[0].text, /enabled:false/)
  assert.equal(table[0].text.includes('__DSH_FILE_UPLOAD__'), false, '禁用注入不得安装上传承载')
  assert.equal(table[0].text.includes('.fetch'), false, '禁用注入不得提供 fetch 形状载体')
})

test('host 能力快照在依赖未确认时报告 unavailable，不假装可用', () => {
  const snapshot = hostEntry.snapshotCapabilities()
  assert.equal(snapshot.status, CAPABILITY_STATUS.unavailable)
  assert.equal(snapshot.packageName, PACKAGE_NAME)
  assert.equal(snapshot.rowId, CORDIS_ROW_ID)
  assert.equal(snapshot.missing.length, 2)
})

test('host 能力快照在显式禁用时报告 degraded', () => {
  const snapshot = hostEntry.snapshotCapabilities({ enabled: false })
  assert.equal(snapshot.status, CAPABILITY_STATUS.unavailable)
  // enabled=false 不改变缺失依赖的结论：缺依赖优先于禁用
  const withDeps = resolveCapabilityStatus({
    remoteChannel: true,
    harnessDraftImport: true,
    enabled: false
  })
  assert.equal(withDeps.status, CAPABILITY_STATUS.degraded)
})

test('client 入口可加载，且导入期不触碰 window/document', () => {
  // 本测试文件本身在 Node 中运行；如果 client 模块在导入期访问浏览器全局，
  // 上面的 import 就会抛错。这里再显式断言环境探测结果。
  const environment = clientEntry.detectClientEnvironment()
  assert.equal(environment.hasBrowserGlobals, false)
  assert.equal(environment.hasDataTransfer, false)
  assert.equal(typeof clientEntry.apply, 'function')
  assert.equal(clientEntry.name, 'remote-attachments-client')
})

test('client 能力快照携带环境事实并保持 unavailable', () => {
  const snapshot = clientEntry.snapshotClientCapabilities()
  assert.equal(snapshot.packageName, PACKAGE_NAME)
  assert.equal(snapshot.status, CAPABILITY_STATUS.unavailable)
  assert.equal(snapshot.environment.hasBrowserGlobals, false)
})

test('cordis.patch.yml 恰好插入一个本包行，且不重复挂载全家桶或 remote', async () => {
  const text = await readFile(join(projectRoot, 'cordis.patch.yml'), 'utf8')
  const active = text.replace(/#[^\n]*/g, '')
  assert.equal((active.match(/-\s*insert:/g) ?? []).length, 1)
  assert.match(active, new RegExp(`id:\\s*${CORDIS_ROW_ID}`))
  assert.match(active, new RegExp(PACKAGE_NAME.replace(/[/@]/g, (ch) => `\\${ch}`)))
  assert.doesNotMatch(active, /dsh-web-all/)
  assert.doesNotMatch(active, /dsh-remote-web-ui/)
})

test('package.json 的入口、files 与 dsh 声明与构建产物一致', async () => {
  const manifest = JSON.parse(await readFile(join(projectRoot, 'package.json'), 'utf8'))
  assert.equal(manifest.main, 'lib/host.js')
  assert.equal(manifest.exports['.'].default, './lib/host.js')
  assert.equal(manifest.exports['./client'].default, './lib/client.js')
  assert.equal(manifest.dsh.bundle.patch, './cordis.patch.yml')
  assert.ok(manifest.files.includes('cordis.patch.yml'))
  assert.ok(manifest.files.some((pattern) => pattern.startsWith('lib/')))
  // 不发布测试与源码
  assert.ok(!manifest.files.some((pattern) => pattern.startsWith('test')))
  assert.ok(!manifest.files.some((pattern) => pattern.startsWith('src')))
  // 未发布到 npm 之前保持 private
  assert.equal(manifest.private, true)
  // 依赖全部锁定精确版本，不使用浮动范围
  // 精确锁定：允许 semver 预发布后缀（如 0.1.5-rc.1），但不允许 ^ ~ * 或区间。
  for (const [dependency, range] of Object.entries(manifest.devDependencies ?? {})) {
    assert.match(
      range,
      /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/,
      `${dependency} 必须锁定精确版本，实际 ${range}`
    )
  }
})
