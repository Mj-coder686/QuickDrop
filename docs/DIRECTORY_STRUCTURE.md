# 目录结构

```text
QuickDrop/
├─ QuickDrop.sln
├─ README.md
├─ Directory.Build.props
├─ docs/
│  ├─ TECHNICAL_PLAN.md
│  └─ DIRECTORY_STRUCTURE.md
├─ src/
│  ├─ QuickDrop.Core/
│  │  ├─ Models/
│  │  ├─ Security/
│  │  ├─ Files/
│  │  └─ Abstractions/
│  ├─ QuickDrop.Network/
│  │  ├─ Discovery/
│  │  ├─ Protocol/
│  │  ├─ Security/
│  │  └─ Transfer/
│  └─ QuickDrop.App/
│     ├─ Assets/
│     │  ├─ QuickDrop.ico
│     │  └─ QuickDrop-Icon.png
│     ├─ ViewModels/
│     ├─ Views/
│     ├─ Converters/
│     └─ MainWindow.xaml
├─ tests/
│  └─ QuickDrop.Tests/
├─ packaging/
│  └─ 使用说明.txt
├─ scripts/
│  ├─ build.ps1
│  ├─ test.ps1
│  ├─ smoke-test.ps1
│  └─ publish.ps1
└─ artifacts/
   └─ win-x64/      # 生成物，不提交
```

分层规则：

- `Core` 不引用 WPF 或网络具体类型，可被未来 Android 或其他 UI 复用。
- `Network` 实现局域网路由和传输协议，只通过模型、事件和接口与 UI 交互。
- `App` 只负责用户流程、文件选择、设备确认和状态展示。
- `Tests` 使用自带的轻量测试运行器，避免因 NuGet 不可用导致无法验证。
- `packaging` 保存与发布版一起交付的非技术使用说明。
