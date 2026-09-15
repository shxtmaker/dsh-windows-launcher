#!/usr/bin/env node
// 构建：用 tsc 生成 lib/（JS + 声明 + sourcemap），并复制运行期需要的静态资源。
// 不做绕过类型检查的转译；类型错误会让构建失败。

import { cp, mkdir, readdir, rm, stat } from 'node:fs/promises'
import { spawn } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const libDir = join(projectRoot, 'lib')

/** 运行期需要随包携带、但不经 tsc 处理的资源。 */
const staticAssets = [['cordis.patch.yml', 'cordis.patch.yml']]

async function exists(path) {
  try {
    await stat(path)
    return true
  } catch {
    return false
  }
}

function run(command, args, cwd) {
  return new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(command, args, { cwd, stdio: 'inherit', shell: false })
    child.on('error', rejectPromise)
    child.on('exit', (code) => {
      if (code === 0) resolvePromise()
      else rejectPromise(new Error(`${command} ${args.join(' ')} 退出码 ${code}`))
    })
  })
}

async function main() {
  await rm(libDir, { recursive: true, force: true })

  const tscBin = join(projectRoot, 'node_modules', 'typescript', 'bin', 'tsc')
  if (!(await exists(tscBin))) {
    throw new Error('缺少本地 typescript 依赖；请先执行 npm install --no-save 或 pnpm install --frozen-lockfile。')
  }
  await run(process.execPath, [tscBin, '-p', 'tsconfig.json'], projectRoot)

  for (const [from, to] of staticAssets) {
    const source = join(projectRoot, from)
    if (!(await exists(source))) throw new Error(`缺少构建资源：${from}`)
    const target = join(libDir, to)
    await mkdir(dirname(target), { recursive: true })
    await cp(source, target)
  }

  const emitted = await readdir(libDir, { recursive: true })
  // tsc 产出 host.js；client 半区的 ESM 形态落在 lib/client/index.js（供单测）。
  const entryPoints = ['host.js', join('client', 'index.js')]
  for (const entry of entryPoints) {
    if (!emitted.includes(entry)) throw new Error(`构建未生成入口：lib/${entry}`)
  }

  // client 半区：真正被 client-modules 服务出去的是 lib/client.js，它必须是
  // __ModuleLoader__.load({id, factory}) 经典包裹形态（D08 根因；见该脚本头注释）。
  await run(process.execPath, [join(projectRoot, 'scripts', 'build-client-bundle.mjs')], projectRoot)
  if (!(await exists(join(libDir, 'client.js')))) {
    throw new Error('构建未生成 client 入口：lib/client.js')
  }

  console.log(`build: OK（lib/ 已生成，入口 host.js, client.js）`)
}

main().catch((error) => {
  console.error(`build: FAIL — ${error.message}`)
  process.exitCode = 1
})
