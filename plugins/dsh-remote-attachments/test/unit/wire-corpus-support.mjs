/**
 * 语料运行器（测试与门禁共用，非测试文件本身）。
 *
 * 这里只做三件事：读 `schemas/remote-attachments/v1` 的语料与 `expected.json`、
 * 驱动**生产 codec**（由调用方注入，绝不在此重写 codec）、按 `expected.json`
 * 逐字段判定。TS 单测与 `scripts/wire-contract-gates.mjs` 走同一份实现，
 * 保证"证据脚本通过"与"单测通过"说的是同一件事。
 */

import { readFile, readdir } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

/** 语料目录绝对路径。 */
export const CORPUS_DIR = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..', 'schemas/remote-attachments/v1')

/** 加载 expected.json。 */
export async function loadExpectations() {
  const text = await readFile(join(CORPUS_DIR, 'expected.json'), 'utf8')
  return JSON.parse(text)
}

/** 读取一个语料文件原文。 */
export async function loadSampleText(relativePath) {
  return readFile(join(CORPUS_DIR, relativePath), 'utf8')
}

/** 列出语料文件（相对于 CORPUS_DIR）。 */
export async function listCorpusFiles() {
  const out = []
  for (const kind of ['golden', 'malicious']) {
    const names = await readdir(join(CORPUS_DIR, kind))
    for (const name of names) if (name.endsWith('.json')) out.push(`${kind}/${name}`)
  }
  return out.sort()
}

/** 结构比较：按键名排序后序列化，两端一致。 */
export function structuralEquals(left, right) {
  return JSON.stringify(sortKeys(left)) === JSON.stringify(sortKeys(right))
}

function sortKeys(value) {
  if (Array.isArray(value)) return value.map(sortKeys)
  if (value !== null && typeof value === 'object') {
    const out = {}
    for (const key of Object.keys(value).sort()) out[key] = sortKeys(value[key])
    return out
  }
  return value
}

/**
 * 运行一个样本：先按 setup 顺序喂给新会话，再解码并（如需要）应用样本本身。
 * @param sample expected.json 中的样本条目
 * @param io `{ decode, createSession }`：生产 codec 的注入点
 */
export async function runSample(sample, io) {
  const observed = {
    id: sample.id,
    file: sample.file,
    kind: sample.kind,
    note: sample.note ?? '',
    decode: null,
    canonical: null,
    apply: 'skip',
    duplicate: false,
    importInvoked: false,
    fileRecord: null
  }

  const text = await loadSampleText(sample.file)
  const decoded = io.decode(text)
  if (decoded.ok) {
    observed.decode = 'accept'
    observed.canonical = decoded.canonical
  } else {
    observed.decode = { code: decoded.code, detail: decoded.detail ?? '' }
  }

  if (sample.expect.apply !== 'skip') {
    const session = io.createSession()
    for (const setupFile of sample.setup ?? []) {
      const setupText = await loadSampleText(setupFile)
      const setupDecoded = io.decode(setupText)
      if (!setupDecoded.ok) {
        observed.apply = { code: 'setup-decode-failed', detail: `${setupFile}: ${setupDecoded.code}` }
        observed.failures = compareExpectation(sample, observed)
        return observed
      }
      const setupApplied = await session.apply(setupDecoded.message)
      if (!setupApplied.ok) {
        observed.apply = { code: 'setup-apply-failed', detail: `${setupFile}: ${setupApplied.code}` }
        observed.failures = compareExpectation(sample, observed)
        return observed
      }
    }
    if (!decoded.ok) {
      observed.apply = 'skip'
    } else {
      const applied = await session.apply(decoded.message)
      if (applied.ok) {
        observed.apply = 'accept'
        observed.duplicate = applied.duplicate
        observed.importInvoked = applied.importInvoked
        observed.fileRecord = applied.file
      } else {
        observed.apply = { code: applied.code, detail: applied.detail }
      }
    }
  }

  observed.failures = compareExpectation(sample, observed)
  return observed
}

/**
 * 按 expected.json 判定一次观测结果，返回失败描述列表（空表示通过）。
 * 这是**唯一**的判定路径：单测、门禁、负向对照都调用它。
 */
export function compareExpectation(sample, observed) {
  const failures = []
  const expect = sample.expect

  if (expect.decode === 'accept') {
    if (observed.decode !== 'accept') {
      failures.push(`decode: 期望接受，实际拒绝 ${observed.decode?.code ?? observed.decode}`)
    } else if (!structuralEquals(observed.canonical, expect.canonical)) {
      failures.push(
        `canonical: 期望 ${JSON.stringify(sortKeys(expect.canonical))}，实际 ${JSON.stringify(sortKeys(observed.canonical))}`
      )
    }
  } else {
    const wanted = expect.decode.reject
    if (observed.decode === 'accept') failures.push(`decode: 期望拒绝 ${wanted}，实际接受`)
    else if (observed.decode.code !== wanted) {
      failures.push(`decode: 期望拒绝 ${wanted}，实际 ${observed.decode.code}`)
    }
  }

  if (expect.apply === 'skip') {
    if (observed.apply !== 'skip') failures.push(`apply: 期望 skip，实际 ${JSON.stringify(observed.apply)}`)
  } else if (expect.apply === 'accept') {
    if (observed.apply !== 'accept') {
      failures.push(`apply: 期望接受，实际 ${JSON.stringify(observed.apply)}`)
    } else {
      if ((expect.duplicate ?? false) !== observed.duplicate) {
        failures.push(`duplicate: 期望 ${expect.duplicate ?? false}，实际 ${observed.duplicate}`)
      }
      if ((expect.importInvoked ?? false) !== observed.importInvoked) {
        failures.push(`importInvoked: 期望 ${expect.importInvoked ?? false}，实际 ${observed.importInvoked}`)
      }
      if (expect.file) {
        if (!observed.fileRecord) {
          failures.push('file: 期望有单文件记录，实际为 null')
        } else {
          for (const [key, value] of Object.entries(expect.file)) {
            if (!structuralEquals(observed.fileRecord[key], value)) {
              failures.push(
                `file.${key}: 期望 ${JSON.stringify(value)}，实际 ${JSON.stringify(observed.fileRecord[key])}`
              )
            }
          }
        }
      }
    }
  } else {
    const wanted = expect.apply.reject
    if (observed.apply === 'accept') failures.push(`apply: 期望拒绝 ${wanted}，实际接受`)
    else if (observed.apply === 'skip') failures.push(`apply: 期望拒绝 ${wanted}，实际未执行`)
    else if (observed.apply.code !== wanted) {
      failures.push(`apply: 期望拒绝 ${wanted}，实际 ${observed.apply.code}`)
    }
  }

  return failures
}

/**
 * 负向对照：故意把期望值改错，返回被改坏的样本副本。
 * 判定路径必须对这些样本报错，否则说明该样本的期望从未被真正比较。
 */
export function mutateExpectation(sample) {
  const mutated = JSON.parse(JSON.stringify(sample))
  const wrongCode = 'version-mismatch'

  if (mutated.expect.decode === 'accept') {
    mutated.expect.decode = { reject: wrongCode }
    if (mutated.expect.canonical !== undefined) {
      mutated.expect.canonical = { ...mutated.expect.canonical, v: 999 }
    }
  } else if (mutated.expect.decode.reject !== wrongCode) {
    mutated.expect.decode = { reject: wrongCode }
  } else {
    mutated.expect.decode = { reject: 'unknown-field' }
  }

  if (mutated.expect.apply === 'accept') {
    mutated.expect.apply = { reject: 'cancelled' }
  } else if (mutated.expect.apply === 'skip') {
    mutated.expect.apply = 'accept'
  } else if (mutated.expect.apply.reject !== 'cancelled') {
    mutated.expect.apply = { reject: 'cancelled' }
  } else {
    mutated.expect.apply = { reject: 'unknown-field' }
  }

  return mutated
}
