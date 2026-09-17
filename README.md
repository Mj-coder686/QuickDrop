# QuickDrop

<img src="src/QuickDrop.App/Assets/QuickDrop-Icon.png" alt="QuickDrop 黑色 M 图标" width="96" />

QuickDrop 是一个面向学生的 Windows 局域网大文件直传工具。它不是网盘：文件从一台电脑直接发送到另一台电脑，不上传到云端，不需要账号。

## 下载

- [下载 QuickDrop v0.1.3 Windows x64](https://github.com/Mj-coder686/QuickDrop/releases/download/v0.1.3/QuickDrop-win-x64.zip)
- [查看最新版本](https://github.com/Mj-coder686/QuickDrop/releases/latest)

下载 ZIP 后解压，直接运行 `QuickDrop.exe`。

## 已实现

- 6 位临时配对码，自动发现局域网内的发送端。
- Windows ↔ Windows，同 Wi-Fi 或允许互访的“网线 + Wi-Fi”校园网。
- 发送端手动确认对方设备名。
- TLS 1.2/1.3 加密，临时证书指纹固定，会话 10 分钟过期。
- 多文件、文件夹和空目录。
- 1 MiB 分块流式传输，适合虚拟机镜像等大文件。
- `.qdpart` 断点续传；取消或断网后，下次使用同一保存位置可继续。
- SHA-256 完整性校验，失败自动从头重试一次。
- 实时进度、速度、剩余时间、取消和会话失败重试。
- 自定义黑色“M”应用图标，显示在窗口、任务栏和可执行文件上。

## 直接使用

1. 在两台 Windows 10/11 电脑上打开 `QuickDrop.exe`。
2. 发送端选择文件/文件夹，点击“生成配对码并开始发送”。
3. 接收端输入 6 位数字，选择保存位置，点击“查找设备并接收”。
4. 发送端核对电脑名后点击“是”。
5. 首次运行如果 Windows 防火墙询问，请只允许“专用网络”。

当前 MVP 没有购买代码签名证书，因此 Windows SmartScreen 可能在首次运行时显示“未知发布者”。正式对外发布前应为 `QuickDrop.exe` 添加 Authenticode 代码签名。

> 如果两台电脑都能上网但一直找不到对方，校园网可能开启了 VLAN/AP 客户端隔离。MVP 不会绕过这种网络限制；后续的公网 P2P/中继路由会通过已预留的 `IRouteProvider` 接口增加。

## 从源码构建

需要 Windows 10/11 和 .NET 8 SDK。

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\smoke-test.ps1
.\scripts\publish.ps1
```

- 构建：`scripts/build.ps1`
- 运行全部自动测试：`scripts/test.ps1`
- 启动并持续观察 UI 进程：`scripts/smoke-test.ps1`
- 生成 Windows x64 自包含单文件版本：`scripts/publish.ps1`
- 从源码启动：`scripts/run.ps1`

技术取舍、协议与安全边界见 [docs/TECHNICAL_PLAN.md](docs/TECHNICAL_PLAN.md)。

## 已知边界

- 当前只实现局域网路由，不含 STUN/TURN/NAT 穿透。
- UDP 发现和 TCP 直连会受校园网隔离策略与 Windows 防火墙影响。
- 配对码是短期可用性凭据，不是长期密码；安全性同时依赖会话过期、失败次数限制和发送方确认。
