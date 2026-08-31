# dsh-remote-web-ui 0.3.10 compatibility descriptor

该只读 DSH 插件为 `@linxin666/dsh-remote-web-ui@0.3.10` 提供 Windows 启动器兼容描述符。它不代理业务请求，不读取设置、token、Cookie 或设备凭据。

启动时会核对包名、版本、发布文件数量和完整文件树 SHA-256。任一值不匹配时直接拒绝启动，不发布描述符。升级或修改 `dsh-remote-web-ui` 后必须重新审计并发布新的适配规则，不能修改此基线绕过检查。

将本目录复制到 Linux 主机后，先打包，再在 `web` profile 中安装固定的 tarball。不使用 `link:`，避免 Node 从链接源目录而非 profile 解析 peer package：

```bash
cd "$HOME/src/dsh-remote-web-ui-0.3.10"
npm pack --pack-destination "$HOME/src"

cd "$HOME/src/deepseek-harness-dsh-v0.1.2-alpha.2-archive"
PATH="$HOME/.local/bin:$PATH" \
COREPACK_DEFAULT_TO_LATEST=0 \
pnpm dsh plugin --profile web add \
  "$HOME/src/dsh-windows-launcher-dsh-remote-web-ui-compatibility-1.0.0.tgz"
```

重启 `dsh web` 后，只检查不含凭据的描述符：

```bash
curl --silent --show-error --fail \
  http://127.0.0.1:3080/.well-known/dsh-webui-compatibility.json
```
