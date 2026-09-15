#!/usr/bin/env python3
"""在**隔离副本**上应用并记录最小补丁（绝不改已安装包）。

补丁：host-webserver 的 renderRow 把 kind='script-src' 渲染为
      <script src="...">  （经典脚本）
   →  <script type="module" src="...">（模块脚本）

理由：dsh-client-modules 通过 webserver/index-inject 推入的引导行是 kind='script-src'，
而它加载的 /plugins/?? 合并 bundle 含顶层 import/export；按经典脚本解析必然
SyntaxError: Cannot use import statement outside a module，导致 client 模块全部加载失败。

用法：python3 apply-webserver-module-patch.py <目标 package 目录>
"""
import pathlib
import re
import sys

ORIGINAL = 'markup: `<script src="${escapeHtmlAttribute(row.src)}"><\\/script>`'
PATCHED = 'markup: `<script type="module" src="${escapeHtmlAttribute(row.src)}"><\\/script>`'


def main() -> int:
    if len(sys.argv) != 2:
        print('用法: apply-webserver-module-patch.py <目标 package 目录>', file=sys.stderr)
        return 2
    target = pathlib.Path(sys.argv[1])
    index = target / 'lib' / 'index.js'
    if not index.is_file():
        print(f'FAIL: 找不到 {index}', file=sys.stderr)
        return 1

    text = index.read_text(encoding='utf-8')
    if PATCHED in text:
        print(f'已打过补丁，跳过：{index}')
        return 0
    if ORIGINAL not in text:
        print(f'FAIL: 未匹配到待替换片段（上游可能已变更）：{index}', file=sys.stderr)
        return 1

    # 只替换 script-src 分支那一处，并断言恰好一处
    occurrences = text.count(ORIGINAL)
    if occurrences != 1:
        print(f'FAIL: 待替换片段出现 {occurrences} 次，拒绝盲改', file=sys.stderr)
        return 1
    index.write_text(text.replace(ORIGINAL, PATCHED, 1), encoding='utf-8')

    # 自检：补丁后该分支必须含 type="module"
    after = index.read_text(encoding='utf-8')
    if PATCHED not in after:
        print('FAIL: 写入后未观察到补丁内容', file=sys.stderr)
        return 1
    print(f'OK: 已在隔离副本上应用补丁 -> {index}')
    print(f'    原片段: {ORIGINAL}')
    print(f'    补丁后: {PATCHED}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
