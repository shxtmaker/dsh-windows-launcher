# 上游 remote 上传脚本探针

该脚本从调用者提供的 dsh-web checkout 读取 remote-channel-rules.ts、remote-channel-boot.ts 和 remote-methods.ts，用 Node VM 执行提取的源码。网络调用是记录用的 fake；不启动 Harness、浏览器或 WebView2，不访问剪贴板和真实凭据。

```bash
node docs/remote-file-paste/probes/probe-remote-upload-boot.mjs /absolute/path/to/dsh-web
```

需要提供 stripTypeScriptTypes 的 Node 版本。先前研究实际执行为 Node v24.19.0；该 API 会输出 ExperimentalWarning，不影响当次退出码。研究源码为 fdb0968b8f620e9c6c15a9ca4cfb1375b48362dd，其 remote 包与 0d78d391b5f67ec4d9f04d47558b76352c74ee48 无差异。

2026-09-13 实际结果：7/7 PASS，退出码 0。宿主为 Windows；脚本未使用 Windows API，这不构成已在 Linux 跑过的证据。

| 用例 | 结果含义 |
| --- | --- |
| plain object headers | 一次路径改写，保留查询、body 和 signal，延迟取得设备凭据 |
| Headers instance | 保留原 Content-Type，正确追加设备头 |
| missing headers | 成功复现原脚本不追加设备头的限制，不是该行为正确 |
| tuple headers | 成功复现数组 headers 被枚举为数字键的限制 |
| pre-rewritten path | 成功复现预拼 /remote 后不再注入设备头的限制 |
| foreign origin | 不向跨源地址追加设备头 |
| addon/remote order | 候选 hook 动态读取 fetch、统一 Headers、保留原 /api 时，两种注入顺序表现一致 |

这个探针不替代正式插件单测、Linux Chromium、真实 Harness 全家桶集成或 Windows 实机测试。生产 addon 必须实现其自身测试，不依赖字符串提取脚本长期代替兼容性验证。
