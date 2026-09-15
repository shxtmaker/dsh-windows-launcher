# @shxtmaker/dsh-remote-attachments

DSH 远程会话的**附件粘贴附加插件**。把 Windows Launcher 采集的普通文件与截图导入当前可编辑会话的待发送草稿。

## 这个包做什么，不做什么

| | 归属 |
| --- | --- |
| 附件能力（本插件独占） | `attachment-paste-import` |
| 配对令牌、设备凭据、`/api/pair/*`、`/remote` 门控代理 | `@linxin666/dsh-remote-web-ui`（全家桶内，**本包不再装第二份**） |
| 会话草稿、上传端点与 receipt | DeepSeek Harness |

安装本插件不会替换、重新打包或修改 `@linxin666/dsh-web-all` 的聚合子插件清单。禁用本插件只停止附件能力，配对、心跳与设备库保持原状。

## 目录结构

```
src/host.ts          包的 `.` 入口（host 半区）
src/host/index.ts    host 半区实现
src/client.ts        包的 `./client` 入口（浏览器半区）
src/shared/          两端共用的协议契约、限额与能力声明
test/unit/           单元测试（node --test）
scripts/             构建、清理与打包验证脚本
cordis.patch.yml     单一 Cordis 行：id=remote-attachments
```

## 命令

```bash
npm install --frozen-lockfile      # 锁定安装（依赖均为精确版本）
npm run typecheck                  # tsc --noEmit，类型错误即失败
npm run build                      # tsc 生成 lib/ 并复制运行期资源
npm run test:unit                  # node --test
npm run test:pack                  # 生成真实 tarball 并逐条核对内容
npm run verify                     # 以上四步串行
```

`test:pack` 检查的是**实际 tarball 内的字节**，不是工作树，也不是 source link：包身份、双面入口与类型声明、`cordis.patch.yml` 的单行声明、以及"不含凭据/生产数据/本机绝对路径"都在此逐条判定。

## 当前进度

D03 只建立可构建、可打包、可测试的骨架：

- host 与 client 入口导出真实的 `apply`/`name` 与能力快照；
- client 入口在**导入期不访问** `window`/`document`，因此可在 Node 中被打包与单测加载；
- 尚未注册上传承载（D07）与草稿入口（D08），因此能力状态在依赖确认前一律报告 `unavailable`，不假装可用。
