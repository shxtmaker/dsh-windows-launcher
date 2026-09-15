/**
 * D22 单元测试：交付包卫生扫描（scripts/pack-scan.mjs）是**可证伪**的。
 *
 * 为什么需要它：D22 的门禁要用"与 D03 test:pack 同一条"扫描来断言交付包不含
 * 凭据/测试私有数据/绝对家目录。如果扫描本身恒真，那道门禁就只是装饰。
 * 因此这里逐类给出**反例 canary**：把敏感形态放进合成的 tar 条目里，判定必须命中；
 * 干净条目必须通过。合成 tar 由本文件自建（不引第三方），避免依赖 pack/ 下是否有产物。
 */

import assert from 'node:assert/strict'
import { gzipSync } from 'node:zlib'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'

import {
  FORBIDDEN_CONTENT_PATTERNS,
  FORBIDDEN_PACKAGE_NAMES,
  FORBIDDEN_PATH_PATTERNS,
  parseTar,
  readTarball,
  scanTarEntries
} from '../../scripts/pack-scan.mjs'

/** 生成一条 ustar 头（name/size 足够用；不做 long name）。 */
function tarHeader(name, size) {
  const header = Buffer.alloc(512)
  header.write(name, 0, 100, 'utf8')
  header.write('0000644\0', 100, 8, 'utf8')
  header.write('0000000\0', 108, 8, 'utf8')
  header.write('0000000\0', 116, 8, 'utf8')
  header.write(`${size.toString(8).padStart(11, '0')}\0`, 124, 12, 'utf8')
  header.write('00000000000\0', 136, 12, 'utf8')
  header.write('        ', 148, 8, 'utf8')
  header.write('0', 156, 1, 'utf8')
  header.write('ustar\0', 257, 6, 'utf8')
  header.write('00', 263, 2, 'utf8')
  let sum = 0
  for (const byte of header) sum += byte
  header.write(`${sum.toString(8).padStart(6, '0')}\0 `, 148, 8, 'utf8')
  return header
}

/** 把 [{ path, content }] 打成未压缩 tar。 */
function buildTar(files) {
  const chunks = []
  for (const file of files) {
    const body = Buffer.isBuffer(file.content) ? file.content : Buffer.from(file.content, 'utf8')
    chunks.push(tarHeader(file.path, body.length), body, Buffer.alloc((512 - (body.length % 512)) % 512))
  }
  chunks.push(Buffer.alloc(1024))
  return Buffer.concat(chunks)
}

const entry = (path, content) => ({ path, content: Buffer.from(content, 'utf8'), isFile: true, size: content.length })

test('scanTarEntries：干净的运行期文件必须通过（不是恒真判定的假象）', () => {
  const result = scanTarEntries([
    entry('package/lib/client.js', 'window.__ModuleLoader__.load({ id: "@shxtmaker/dsh-remote-attachments" })'),
    entry('package/package.json', '{"name":"@shxtmaker/dsh-remote-attachments","version":"0.1.0"}'),
    entry('package/README.md', '# 附件插件\n不含任何机密\n')
  ])
  assert.equal(result.ok, true)
  assert.deepEqual(result.offendingPaths, [])
  assert.deepEqual(result.offendingContent, [])
})

test('canary：本机绝对家目录路径必须被同一判定命中', () => {
  const result = scanTarEntries([entry('package/lib/host.js', 'const p = "/home/someuser/.dsh/profiles/web"')])
  assert.equal(result.ok, false)
  assert.equal(result.offendingContent.length, 1)
  assert.match(result.offendingContent[0], /^package\/lib\/host\.js 命中 /)
  assert.match(result.offendingContent[0], /home/)
})

test('canary：配对 cookie / 私钥 / npm token 形态必须被命中', () => {
  const canaries = [
    ['dsh_pair=0123456789abcdef0123456789abcdef', 'cookie'],
    ['-----BEGIN RSA PRIVATE KEY-----', 'private-key'],
    ['token = npm_abcdefghijklmnopqrstuvwxyz0123456789', 'npm-token'],
    ['const t = "ghp_abcdefghijklmnopqrstuvwxyz0123"', 'github-token']
  ]
  for (const [text, label] of canaries) {
    const result = scanTarEntries([entry('package/lib/client.js', text)])
    assert.equal(result.ok, false, `canary ${label} 未被卫生扫描命中`)
    assert.ok(result.offendingContent.length >= 1, `canary ${label} 缺少命中明细`)
  }
})

test('canary：凭据/私有数据类路径必须被命中（含 device-store、pairing-token、.npmrc、私钥后缀）', () => {
  const canaries = [
    'package/.env.local',
    'package/.npmrc',
    'package/device-store.json',
    'package/pairing-token.txt',
    'package/certs/client.pem',
    'package/node_modules/left-pad/index.js',
    'package/lib/credential-cache.js',
    'package/.git/config'
  ]
  for (const path of canaries) {
    const result = scanTarEntries([entry(path, 'clean')])
    assert.equal(result.ok, false, `canary 路径未被命中：${path}`)
    assert.ok(result.offendingPaths.includes(path), `canary 路径缺少命中明细：${path}`)
  }
})

test('scanTarEntries：目录等非文件条目不参与正文扫描', () => {
  const result = scanTarEntries([
    { path: 'package/node_modules/', content: null, isFile: false, size: 0 },
    entry('package/lib/host.js', 'export const name = "x"')
  ])
  assert.equal(result.ok, true)
})

test('parseTar/readTarball：真实 gzip tar 的路径、大小与正文逐条可读', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'dsh-pack-scan-'))
  try {
    const tarball = join(dir, 'sample.tgz')
    await writeFile(tarball, gzipSync(buildTar([
      { path: 'package/package.json', content: '{"name":"x"}' },
      { path: 'package/lib/client.js', content: 'a'.repeat(1200) }
    ])))
    const entries = await readTarball(tarball)
    const files = entries.filter((item) => item.isFile)
    assert.equal(files.length, 2)
    assert.deepEqual(files.map((item) => item.path).sort(), ['package/lib/client.js', 'package/package.json'])
    assert.equal(files.find((item) => item.path === 'package/lib/client.js').content.length, 1200)
    assert.equal(files.find((item) => item.path === 'package/package.json').content.toString('utf8'), '{"name":"x"}')
    // 同一份字节走 parseTar 与 readTarball 必须一致（gzip 只做封装）。
    const raw = buildTar([{ path: 'package/a.js', content: 'export {}' }])
    assert.equal(parseTar(raw)[0].path, 'package/a.js')
  } finally {
    await rm(dir, { recursive: true, force: true })
  }
})

test('常量与身份约定：全家桶/remote 包名在禁止清单里，且正则清单非空（防止清理后门禁变空转）', () => {
  assert.deepEqual(FORBIDDEN_PACKAGE_NAMES, ['@linxin666/dsh-web-all', '@linxin666/dsh-remote-web-ui'])
  assert.ok(FORBIDDEN_PATH_PATTERNS.length >= 8)
  assert.ok(FORBIDDEN_CONTENT_PATTERNS.length >= 6)
})
