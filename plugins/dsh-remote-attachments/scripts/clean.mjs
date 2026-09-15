#!/usr/bin/env node
// 清理构建产物。只删除本包内的 lib/ 与 pack/，不触碰仓库其他位置。
import { rm } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')

for (const relative of ['lib', 'pack']) {
  await rm(join(projectRoot, relative), { recursive: true, force: true })
  console.log(`clean: 已删除 ${relative}/`)
}
