# 应用图标

`dsh-app.ico` 由 `dsh-hub-icon-v3-minimal.png` 转换，包含 16、20、24、32、40、48、64、128、256 像素帧。

ICO 必须使用 32 位 RGBA DIB 位图帧。曾使用 RGB PNG 压缩帧的版本能够被 Windows Shell 显示，但 WPF 在窗口初始化时会抛出 `FileFormatException`。

使用 Pillow 重新生成：

```python
from PIL import Image

image = Image.open("assets/icons/dsh-hub-icon-v3-minimal.png").convert("RGBA")
image.save(
    "assets/icons/dsh-app.ico",
    format="ICO",
    bitmap_format="bmp",
    sizes=[(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)],
)
```

`DesktopStartupTests` 在 STA 线程实际初始化管理窗口和远程窗口，验证 WPF 能加载打包后的图标资源。
