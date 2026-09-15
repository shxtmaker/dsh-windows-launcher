#!/usr/bin/env node
/**
 * 最小打包验证（D03）。
 *
 * 目标：证明**实际 tarball**（不是 source link、不是工作树）包含运行入口与必要资源，
 * 且不含凭据或生产数据。判定全部基于 tarball 内的真实字节，不基于 npm 的 include 规则。
 *
 * 判据：
 *   P01 npm pack 成功且产出唯一 tarball
 *   P02 包身份与全家桶、remote 插件不同，且未声明第二份配对服务
 *   P03 包清单声明了 host 与 client 双面入口
 *   P04 tarball 内实际存在 host 入口（非空、可被 Node 以 ESM 加载）
 *   P05 tarball 内实际存在 client 入口（非空、可被 Node 加载且导入期不触碰 window）
 *   P06 tarball 内含 cordis.patch.yml，且恰好插入一个本包行
 *   P07 tarball 不含凭据、生产数据或本机绝对路径
 *   P08 tarball 不含测试文件与源码目录（只发布运行期需要的文件）
 */

import assert from 'node:assert/strict'
import { execFile } from 'node:child_process'
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { evaluateClientBundle } from '../test/support/client-bundle.mjs'
import { promisify } from 'node:util'
// 卫生扫描与 tar 解析抽到 scripts/pack-scan.mjs：D22 的交付包门禁必须用**同一条**判定，
// 不能在门禁脚本里再抄一份正则（两份正则漂移后，D03 的绿灯就不再对 D22 有证明力）。
import { FORBIDDEN_PACKAGE_NAMES, parseTar, readTarball, scanTarEntries } from './pack-scan.mjs'

const execFileAsync = promisify(execFile)
const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const packDir = join(projectRoot, 'pack')

const results = []
function record(id, description, ok, detail = '') {
  results.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

/** 在隔离目录中把 tarball 内的文件还原到磁盘，供真实 import 使用。 */
async function extractTo(entries, targetDir) {
  for (const entry of entries) {
    if (!entry.isFile) continue
    const destination = join(targetDir, entry.path)
    assert.ok(
      resolve(destination).startsWith(resolve(targetDir) + '/'),
      `拒绝越界条目：${entry.path}`
    )
    await mkdir(dirname(destination), { recursive: true })
    await writeFile(destination, entry.content)
  }
}

async function main() {
  console.log('test:pack —— 实际 tarball 验证')
  await rm(packDir, { recursive: true, force: true })
  await mkdir(packDir, { recursive: true })

  // P01 —— 生成 tarball
  let tarballName
  try {
    const { stdout } = await execFileAsync('npm', ['pack', '--pack-destination', packDir, '--json'], {
      cwd: projectRoot,
      maxBuffer: 10 * 1024 * 1024
    })
    const parsed = JSON.parse(stdout)
    assert.ok(Array.isArray(parsed) && parsed.length === 1, 'npm pack 必须产出唯一 tarball')
    tarballName = parsed[0].filename
    record('P01', 'npm pack 成功且产出唯一 tarball', true, tarballName)
  } catch (error) {
    record('P01', 'npm pack 成功且产出唯一 tarball', false, error.message)
    return finish()
  }

  const tarballPath = join(packDir, tarballName)
  const entries = await readTarball(tarballPath)
  const files = entries.filter((entry) => entry.isFile)
  const paths = files.map((entry) => entry.path)
  const byPath = new Map(files.map((entry) => [entry.path, entry]))

  // P02 —— 包身份
  const manifestEntry = byPath.get('package/package.json')
  const manifest = manifestEntry ? JSON.parse(manifestEntry.content.toString('utf8')) : null
  if (!manifest) {
    record('P02', '包身份与全家桶、remote 插件不同', false, 'tarball 内缺少 package.json')
  } else {
    const identityOk =
      typeof manifest.name === 'string' &&
      manifest.name !== '' &&
      !FORBIDDEN_PACKAGE_NAMES.includes(manifest.name) &&
      manifest.name.includes('attachment')
    // 不声明第二份配对/设备服务：不得出现配对路由或设备库相关入口。
    const declaresPairing =
      JSON.stringify(manifest.dsh ?? {}).includes('pair') ||
      JSON.stringify(manifest.exports ?? {}).includes('pair')
    record(
      'P02',
      '包身份与全家桶、remote 插件不同，且未声明第二份配对服务',
      identityOk && !declaresPairing,
      `name=${manifest.name ?? '(缺失)'} declaresPairing=${declaresPairing}`
    )
  }

  // P03 —— 双面入口声明
  // npm 对 `main` 不加 ./ 前缀，对 `exports` 目标要求 ./ 前缀；比较前统一归一化。
  const normalize = (value) => (typeof value === 'string' ? value.replace(/^\.\//, '') : value)
  const declaredMain = normalize(manifest?.main)
  const declaredClient = normalize(manifest?.exports?.['./client']?.default)
  const declaredTypes = normalize(manifest?.exports?.['.']?.types)
  const declaredClientTypes = normalize(manifest?.exports?.['./client']?.types)
  const entryDeclarationOk =
    declaredMain === 'lib/host.js' &&
    declaredClient === 'lib/client.js' &&
    typeof declaredTypes === 'string' &&
    typeof declaredClientTypes === 'string'
  // 声明的入口与类型文件必须真的在这个 tarball 里，避免清单指向不存在的产物。
  const declaredFilesPresent =
    byPath.has(`package/${declaredMain}`) &&
    byPath.has(`package/${declaredClient}`) &&
    byPath.has(`package/${declaredTypes}`) &&
    byPath.has(`package/${declaredClientTypes}`)
  record(
    'P03',
    '包清单声明 host 与 client 双面入口及类型，且指向的文件确实在包内',
    entryDeclarationOk && declaredFilesPresent,
    `main=${declaredMain} client=${declaredClient} types=${declaredTypes} clientTypes=${declaredClientTypes} present=${declaredFilesPresent}`
  )

  // P04/P05 —— 入口在 tarball 内真实存在且可加载
  const workDir = await mkdtemp(join(tmpdir(), 'dsh-attach-pack-'))
  try {
    await extractTo(entries, workDir)

    {
      const relative = 'package/lib/host.js'
      const entry = byPath.get(relative)
      if (!entry || entry.content.length === 0) {
        record('P04', 'tarball 内 host 入口存在且可被 Node 作为 ESM 加载', false, `缺少或为空的 ${relative}`)
      } else {
        try {
          // 真实加载：证明入口不是空壳，且导入期不依赖浏览器全局。
          const moduleUrl = pathToFileURL(join(workDir, relative)).href
          const loaded = await import(moduleUrl)
          const hasApply = typeof loaded.apply === 'function' || typeof loaded.default?.apply === 'function'
          const hasName = typeof loaded.name === 'string' || typeof loaded.default?.name === 'string'
          record('P04', 'tarball 内 host 入口存在且可被 Node 作为 ESM 加载', hasApply && hasName, `apply=${hasApply} name=${hasName}`)
        } catch (error) {
          record('P04', 'tarball 内 host 入口存在且可被 Node 作为 ESM 加载', false, `加载失败：${error.message}`)
        }
      }
    }

    // P05 —— client 入口必须是 **经典脚本包裹形态**，并且在只有 __ModuleLoader__ 的环境里
    // 完成注册而不触碰 window/document/navigator（D08 根因回归门；详见 R09-D08.md）。
    {
      const description = 'tarball 内 client 入口是 __ModuleLoader__.load 经典包裹，注册期不触碰浏览器全局'
      const entry = byPath.get('package/lib/client.js')
      if (!entry || entry.content.length === 0) {
        record('P05', description, false, '缺少或为空的 package/lib/client.js')
      } else {
        try {
          const code = entry.content.toString('utf8')
          const topLevelEsm = code
            .split('\n')
            .filter((line) => /^(import|export)[\s{*]/.test(line)).length
          const registration = evaluateClientBundle(code, manifest?.name)
          record(
            'P05',
            description,
            topLevelEsm === 0 && registration.ok,
            `顶层ESM语句=${topLevelEsm} 注册id=${registration.id ?? '(无)'} ${registration.detail}`
          )
        } catch (error) {
          record('P05', description, false, `求值失败：${error.message}`)
        }
      }
    }

    // 类型声明必须随包提供，否则消费方无法做类型核对。
    const typesOk =
      byPath.has('package/lib/types/host.d.ts') && byPath.has('package/lib/types/client/index.d.ts')
    record('P03b', 'tarball 内含 host/client 类型声明', typesOk,
      `host=${byPath.has('package/lib/types/host.d.ts')} client=${byPath.has('package/lib/types/client/index.d.ts')}`)

    // P06 —— cordis patch
    const patchEntry = byPath.get('package/cordis.patch.yml')
    if (!patchEntry) {
      record('P06', 'tarball 内含 cordis.patch.yml 且恰好插入一个本包行', false, '缺少 cordis.patch.yml')
    } else {
      const patchText = patchEntry.content.toString('utf8')
      const insertCount = (patchText.match(/^\s*-\s*insert:/gm) ?? []).length
      const rowMatches = [...patchText.matchAll(/id:\s*(\S+)[\s\S]*?name:\s*(\S+)/g)]
      const rows = rowMatches.map((match) => ({ id: match[1], name: match[2] }))
      const single = insertCount === 1 && rows.length === 1
      // 该行不得指向全家桶或 remote 插件，也不得重复挂载它们。
      const rowIsOwn = single && rows[0] && !FORBIDDEN_PACKAGE_NAMES.includes(rows[0].name)
      const namesOtherBundle = /dsh-web-all|dsh-remote-web-ui/.test(patchText.replace(/#[^\n]*/g, ''))
      const ok = Boolean(single && rowIsOwn && !namesOtherBundle)
      record(
        'P06',
        'tarball 内含 cordis.patch.yml 且恰好插入一个本包行',
        ok,
        `insert=${insertCount} rows=${JSON.stringify(rows)} namesOtherBundle=${namesOtherBundle}`
      )
      // 行 id 必须与声明的常量一致
      if (rows[0]) {
        record('P06b', 'patch 行 id 与共享常量一致', rows[0].id === 'remote-attachments', `id=${rows[0].id}`)
      }
    }
  } finally {
    await rm(workDir, { recursive: true, force: true })
  }

  // P07 —— 无凭据、生产数据或本机绝对路径
  const hygiene = scanTarEntries(entries)
  record(
    'P07',
    'tarball 不含凭据、生产数据或本机绝对路径',
    hygiene.ok,
    [...hygiene.offendingPaths, ...hygiene.offendingContent].slice(0, 5).join('; ')
  )

  // P08 —— 只发布运行期文件
  const forbiddenInPack = paths.filter((path) =>
    /^package\/(test|src|scripts)\//.test(path) ||
    /^package\/(tsconfig\.json|package-lock\.json|\.npmignore)$/.test(path)
  )
  record(
    'P08',
    'tarball 只含运行期文件（不含 test/src/scripts/构建配置）',
    forbiddenInPack.length === 0,
    forbiddenInPack.slice(0, 5).join(', ')
  )

  console.log(`\ntarball 文件数：${files.length}`)
  return finish()
}

function finish() {
  const failed = results.filter((result) => !result.ok)
  console.log(`\ntest:pack —— 判据 ${results.length - failed.length}/${results.length} 通过`)
  if (failed.length > 0) {
    console.error('失败判据：')
    for (const result of failed) console.error(`  - ${result.id} ${result.description}：${result.detail}`)
    process.exitCode = 1
    return
  }
  console.log('test:pack: PASS')
}

await main()
