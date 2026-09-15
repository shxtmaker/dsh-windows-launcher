#!/usr/bin/env node
/**
 * 生成 client 半区的**服务形态**：`lib/client.js`。
 *
 * ## 为什么必须做这一步（D08 实测根因）
 *
 * `@deepseek-ai/dsh-client-modules` 把同一 phase 的各包 `client.js` **原始字节**用 combo
 * 路由首尾相接（`comboSource()` 只剥 sourcemap 尾注，不做任何 ESM→CJS 变换），再由
 * `defaultLoadBundle()` 以**经典脚本**动态加载。因此契约是：每个包磁盘上的 client 入口
 * 必须已经是
 *
 * ```js
 * window.__ModuleLoader__.load({ id: '<包名>', factory: (require) => { …; return module.exports } })
 * ```
 *
 * 官方包（`dsh-client-hmr`、`dsh-client-ui-conversation` …）顶层 `import`/`export` 计数**为 0**。
 * 若入口仍是裸 ESM，它会把**所在整批**（实测 14.6 MB、56 条）变成语法错误，页面因此
 * "Failed to load plugins"、composer 永不渲染。D08 曾误判为上游缺陷，实际是本包的构建缺陷。
 *
 * ## 做法
 *
 * 用**已有的 typescript 依赖**（`ts.transpileModule` → CommonJS）把 client 半区各模块逐个
 * 编译成 CJS，再套一层本包自己的极小模块注册表与 `__ModuleLoader__.load` 包裹。不引入新
 * 依赖，也不手写 ESM→CJS 改写（那属于易错的重复造轮子）。
 *
 * 相对依赖全部内联；**裸模块名**（如 `@deepseek-ai/cordis`）转发给外层 loader 的 `require`，
 * 与官方包一致。
 *
 * ## 失败即大声
 *
 * 任何一个模块出现无法解析的相对依赖、或输出里残留顶层 `import`/`export`、或最终产物不是
 * 恰好一次注册，构建都会失败，而不是产出坏包。
 */

import { readFile, writeFile } from 'node:fs/promises'
import { createRequire } from 'node:module'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const require = createRequire(import.meta.url)

/** client 半区入口（ESM 源形态）。 */
const CLIENT_ENTRY = join(projectRoot, 'src/client/index.ts')
/** 产物：被 client-modules 服务出去的经典包裹脚本。 */
const OUTPUT = join(projectRoot, 'lib/client.js')

const ts = require('typescript')

const TRANSPILE_OPTIONS = {
  // 必须与主 tsconfig 的类型检查保持一致；这里只负责产出，类型由 tsc -p 把关。
  module: ts.ModuleKind.CommonJS,
  target: ts.ScriptTarget.ES2023,
  // 产物是经典脚本，不产出 ESM 互操作辅助（我们的源码不使用 default 导入外部包）。
  esModuleInterop: false,
  verbatimModuleSyntax: false,
  sourceMap: false,
  inlineSourceMap: false,
  removeComments: false
}

/** 相对依赖的静态 specifier（`from '…'`、`import('…')`、`export … from '…'`）。 */
function relativeSpecifiers(source, fileName) {
  const sourceFile = ts.createSourceFile(fileName, source, ts.ScriptTarget.ES2023, true)
  const found = new Set()
  const visit = (node) => {
    const specifier =
      (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) && node.moduleSpecifier
        ? node.moduleSpecifier
        : ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword
          ? node.arguments[0]
          : undefined
    if (specifier && ts.isStringLiteral(specifier) && specifier.text.startsWith('.')) {
      found.add(specifier.text)
    }
    ts.forEachChild(node, visit)
  }
  visit(sourceFile)
  return [...found]
}

/** 把 TS 源里的相对 specifier 解析为绝对路径（输出保留 `.js` 形态）。 */
function resolveSpecifier(fromFile, specifier) {
  const guess = resolve(dirname(fromFile), specifier)
  return guess.endsWith('.js') ? `${guess.slice(0, -3)}.ts` : `${guess}.ts`
}

/** 模块在产物里的注册键：相对 projectRoot/src 的 POSIX 路径，扩展名为 `.js`。 */
function moduleKey(file) {
  const rel = relative(join(projectRoot, 'src'), file).split(sep).join('/')
  return `./${rel.replace(/\.ts$/, '.js')}`
}

/** 收集从入口可达的 client 半区模块（深度优先，后序 → 依赖先于使用者）。 */
async function collectModules() {
  const order = []
  const seen = new Set()
  const visit = async (file) => {
    if (seen.has(file)) return
    seen.add(file)
    const source = await readFile(file, 'utf8')
    for (const specifier of relativeSpecifiers(source, file)) {
      const dependency = resolveSpecifier(file, specifier)
      await visit(dependency)
    }
    order.push({ file, source })
  }
  await visit(CLIENT_ENTRY)
  return order
}

/** 编译单个模块为 CJS 函数体，并把相对 require 重写成注册表查找。 */
function compileModule({ file, source }) {
  const output = ts.transpileModule(source, {
    compilerOptions: TRANSPILE_OPTIONS,
    fileName: file
  })
  const diagnostics = (output.diagnostics ?? []).filter(
    (item) => item.category === ts.DiagnosticCategory.Error
  )
  if (diagnostics.length > 0) {
    const text = diagnostics
      .map((item) => ts.flattenDiagnosticMessageText(item.messageText, ' '))
      .join('; ')
    throw new Error(`${moduleKey(file)} 转译失败：${text}`)
  }
  return output.outputText
}

async function main() {
  const modules = await collectModules()
  const entryKey = moduleKey(CLIENT_ENTRY)
  const keys = modules.map((module) => moduleKey(module.file))
  if (!keys.includes(entryKey)) throw new Error(`入口未进入模块表：${entryKey}`)

  const packageName = JSON.parse(
    await readFile(join(projectRoot, 'package.json'), 'utf8')
  ).name

  const body = modules
    .map((module) => {
      const key = moduleKey(module.file)
      const compiled = compileModule(module).trimEnd()
      // 每个模块一个函数作用域，因此模块之间的顶层声明不会互相污染。
      // 第三个形参**必须**叫 `require`：tsc 的 CJS 产物以自由变量 `require(…)` 调用依赖，
      // 靠这个形参遮蔽外层 loader 的 require，相对依赖才会走本包的模块表。
      return `\t__define(${JSON.stringify(key)}, function (module, exports, require) {\n${compiled}\n\t});`
    })
    .join('\n')

  const bundle = `/**
 * 生成产物，请勿手改。源文件：src/client/**、src/shared/**；由 scripts/build-client-bundle.mjs 生成。
 *
 * 形态要求见文件头注释与 docs/remote-file-paste/execution/rounds/R09-D08.md：
 * client-modules 把各包 client.js 原始字节拼接后以**经典脚本**加载，所以本文件顶层不得出现
 * 任何 import / export，只能是下面这一次 __ModuleLoader__.load 注册。
 */
window.__ModuleLoader__.load({
\tid: ${JSON.stringify(packageName)},
\tfactory: (require) => {
\t\tvar __defs = {};
\t\tvar __cache = {};
\t\tfunction __define(key, factory) { __defs[key] = factory; }
\t\tfunction __requireFrom(fromKey, specifier) {
\t\t\tif (specifier.charAt(0) !== '.') return require(specifier);
\t\t\tvar base = fromKey.slice(0, fromKey.lastIndexOf('/') + 1);
\t\t\tvar parts = (base + specifier).split('/');
\t\t\tvar stack = [];
\t\t\tfor (var i = 0; i < parts.length; i += 1) {
\t\t\t\tvar part = parts[i];
\t\t\t\tif (part === '' || part === '.') continue;
\t\t\t\tif (part === '..') { stack.pop(); continue; }
\t\t\t\tstack.push(part);
\t\t\t}
\t\t\treturn __load('./' + stack.join('/'), fromKey);
\t\t}
\t\tfunction __load(key, fromKey) {
\t\t\tif (Object.prototype.hasOwnProperty.call(__cache, key)) return __cache[key].exports;
\t\t\tvar factory = __defs[key];
\t\t\tif (factory === undefined) {
\t\t\t\tthrow new Error('remote-attachments: client module ' + JSON.stringify(key) + ' is not bundled' + (fromKey === undefined ? '' : ' (required from ' + JSON.stringify(fromKey) + ')'));
\t\t\t}
\t\t\tvar module = { exports: {} };
\t\t\t__cache[key] = module;
\t\t\tfactory(module, module.exports, function (specifier) { return __requireFrom(key, specifier); });
\t\t\treturn module.exports;
\t\t}
${body}
\t\treturn __load(${JSON.stringify(entryKey)});
\t}
});
`

  // 硬校验：顶层不得残留 ESM 语句；必须恰好一次注册；且能被 JS 引擎解析。
  const offenders = bundle
    .split('\n')
    .map((line, index) => ({ line: index + 1, text: line }))
    .filter((row) => /^(import|export)[\s{*]/.test(row.text))
  if (offenders.length > 0) {
    throw new Error(
      `产物顶层残留 ESM 语句 ${offenders.length} 条，首个在第 ${offenders[0].line} 行：${offenders[0].text.slice(0, 120)}`
    )
  }
  // 只统计行首注册（文件头注释里也出现了该字符串，不能直接数子串）。
  const registrations = bundle
    .split('\n')
    .filter((line) => line.startsWith('window.__ModuleLoader__.load(')).length
  if (registrations !== 1) throw new Error(`产物注册次数应为 1，实际 ${registrations}`)
  try {
    new (require('node:vm').Script)(bundle, { filename: 'lib/client.js' })
  } catch (error) {
    throw new Error(`产物语法检查失败：${error.message}`)
  }

  await writeFile(OUTPUT, bundle)
  console.log(
    `build-client-bundle: OK（${modules.length} 个模块 → lib/client.js，${Buffer.byteLength(bundle)} 字节，顶层 ESM 语句 0）`
  )
}

main().catch((error) => {
  console.error(`build-client-bundle: FAIL — ${error.message}`)
  process.exitCode = 1
})
