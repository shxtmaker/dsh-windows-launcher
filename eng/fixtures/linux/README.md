# Linux 网络夹具

`fixture-control.sh` 只在显式 RFC1918 地址和端口上启动无凭据测试服务。所有模式均不得用于生产。

## WebUI 兼容夹具

以下模式提供互相独立的描述符场景：

| 模式 | `/.well-known/dsh-webui-compatibility.json` 行为 |
| --- | --- |
| `descriptor-valid` | `200`、严格 `application/json; charset=utf-8`，正文不超过 64 KiB |
| `descriptor-missing` | `404` |
| `descriptor-wrong-content-type` | 正文有效，但以 `text/plain; charset=utf-8` 返回 |
| `descriptor-oversized` | 正文仍是有效 JSON，但恰好为 64 KiB + 1 byte |
| `descriptor-redirect` | `302` 到同源 `/descriptor-redirect-target` |
| `webui-peer` | 与 `descriptor-valid` 相同，用作第二端口或第二测试源 |

有效描述符只声明 `org.dshwindowslauncher.fixture` 测试组件和
`fixture-observation` 测试 adapter key。它不声明或推断任何 dsh-web 版本，也不代表注册表已信任该组件。

同机异端口示例。将 `<LAN-IP>` 换成 Linux 设备的 RFC1918 IPv4：

```bash
./eng/fixtures/linux/fixture-control.sh start descriptor-valid <LAN-IP> 43080
./eng/fixtures/linux/fixture-control.sh start webui-peer <LAN-IP> 43081
```

打开以下页面。主页面会从第二端口加载像素资源：

```text
http://<LAN-IP>:43080/tests/cross-port.html?peerPort=43081
```

逐跳重定向从 `/redirect/hop/1` 开始，依次到 `/redirect/hop/2` 和
`/redirect/arrival`。固定路由的内存计数可通过
`GET /__fixture/counts` 读取，通过无正文的 `POST /__fixture/reset` 清零。
计数键由服务器代码固定，不使用或保存原始请求目标、查询、Cookie、请求体或页面内容。

条件能力探针页面包括：

- `/tests/websocket.html` 和 `/websocket/echo`：最小 RFC 6455 握手、文本/二进制回显及 ping/pong；
- `/tests/worker.html`：同源 Dedicated Worker 和 Shared Worker；
- `/tests/oopif.html?peerPort=43081`：跨源 iframe 到 peer 的 `/tests/oopif-child.html`。

不同端口只保证跨源，不保证 Chromium 一定创建独立 renderer 进程。OOPIF 必须在目标 WebView2/Edge
版本上另行核对 frame/process/CDP 证据。上述 WebSocket、Worker 和 iframe 页面只提供可重复输入，不构成
兼容契约 `1.0.0` 的能力授权、参考适配器通过证据或正式发布证据。

HTTP 模式可使用调用者提供的测试证书切换为 HTTPS。私钥不应提交到仓库：

```bash
DSHWL_FIXTURE_TLS_CERT=/run/user/$UID/dshwl-cert.pem \
DSHWL_FIXTURE_TLS_KEY=/run/user/$UID/dshwl-key.pem \
./eng/fixtures/linux/fixture-control.sh start descriptor-valid <LAN-IP> 43443
```

双 HTTPS 源需要在两个模式和两个端口上使用相同命令分别启动。夹具不会创建、复制或保存证书、token
及其他凭据。控制日志只含固定模式、协议、绑定地址和端口。停止操作可重复执行：

```bash
./eng/fixtures/linux/fixture-control.sh stop descriptor-valid
./eng/fixtures/linux/fixture-control.sh stop webui-peer
```

使用 Python 标准库运行自检：

```bash
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest -v eng/fixtures/linux/test_fixture_server.py
bash -n eng/fixtures/linux/fixture-control.sh
```

自检使用回环地址和临时端口，不启动持久进程，不写 token。实际控制脚本仍拒绝回环、公网、IPv6 和
未显式指定的地址。

## 旧版探测夹具

`legacy-root-0.1.1-rc.2.html` 来自官方 npm 包 `@deepseek-ai/dsh@0.1.1-rc.2` 在全新 `DSH_HOME`、默认 Web profile 下生成的稳定根响应。它只用于识别并拒绝这一固定旧版无认证基线。对应 `/api` 响应为 `404` 和正文 `not found`。

该快照的上游仓库为 `https://github.com/deepseek-ai/deepseek-harness`，许可证见 `LICENSE.deepseek-harness`。
