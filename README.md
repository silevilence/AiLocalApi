# AiLocalApi

Windows 11 x64 本地语音转写托盘应用。开发不需要安装包、Release、Python、Docker 或 Whisper/FFmpeg CLI。

统一入口：`scripts/dev.ps1` 启动开发实例，`scripts/test.ps1` 构建并测试，`scripts/publish.ps1` 发布自包含输出，`scripts/pack.ps1` 生成 NSIS / Velopack 双形态产物。固定工具、数据布局、更新策略及 Sandbox 验收命令见 [发行指南](docs/RELEASE.md)。

安装 .NET SDK **10.0.301** 后，在仓库执行：

```powershell
dotnet restore AiLocalApi.slnx --locked-mode
./scripts/setup-native.ps1
./scripts/build.ps1
dotnet test AiLocalApi.slnx --no-build
./src/AiLocalApi.Desktop/bin/Debug/net10.0-windows/AiLocalApi.Desktop.exe
```

宿主、核心应用服务、通信契约、推理 Worker 和测试分别位于 `src/`、`tests/`。Worker 通过当前用户专用命名管道连接，父进程消失时自动退出。`AILOCAL_DATA` 可指定数据绝对路径，`AILOCAL_WORKER` 可覆盖 Worker 可执行文件路径。日志只记录诊断代码和任务 ID，不记录音频、提示或转写文本。

首次运行在「模型」页选择目录，再下载 tiny / tiny.en / base，或导入目录支持的官方 F16 文件。默认源无法连接时，可在「设置」选择 hf-mirror.com，点击“测试下载源”检查后保存，所有来源都校验固定 SHA-256。设置默认模型后在「转写任务」选择/拖入音频；可取消、重试、导出五种格式。窗口关闭后继续处理，退出请使用托盘「退出」。模型目录更改只影响未来下载，不隐式搬移已安装文件。

开发机需要 VC++ 2015–2022 x64 Redistributable（包含 CRT/OpenMP）；自包含验证包通过 `scripts/publish-sandbox.ps1 -VcRedist <MSVC Redist x64目录>` 把允许分发的 DLL 置于 Worker 目录，Sandbox 无需安装 .NET 或编译工具。默认 CPU/Vulkan 构建，CUDA 构建使用 `./scripts/build.ps1 -EnableCuda`，需要 NVIDIA 驱动；该选项会还原对应精确版本的运行库。

「设置」可实际测试所选推理后端；需先安装一个模型，测试使用独立 Worker，结束后释放资源。修改端口后点击“应用端口并重启 API”，不必退出应用；切换到被占用的端口会保留原服务。

「API 密钥」页支持创建多个命名密钥、自定义或自动生成、停用及删除；完整值仅在创建后显示。关闭“允许不带密钥的请求”后，客户端必须带有效 Bearer 密钥。旧单密钥自动迁移并保持原访问策略。

详细说明：[API 与兼容矩阵](docs/API.md)、[数据与恢复](docs/DATA.md)、[托盘生命周期](docs/LIFECYCLE.md)、[第三方依赖](docs/THIRD-PARTY.md)。应用只处理本地文件，不提供 Realtime、说话人分离或浏览器管理站点。

本地验证工具：

```powershell
$env:AILOCAL_WORKER = "$PWD/src/AiLocalApi.Desktop/bin/Debug/net10.0-windows/worker/AiLocalApi.Worker.exe"
# audio.wav 与模型目录由调用者明确提供；不会默认下载到系统模型目录。
dotnet run --project tools/AiLocalApi.Verify -- artifacts/check D:/models audio.wav cpu
# --download 显式下载 tiny；--suite 需要 fixtures 目录中的七格式和 long.wav。
# 从 mono PCM16/16kHz jfk.wav 生成约定格式，仍只调用 FFmpeg 共享库：
dotnet run --project tools/AiLocalApi.Verify -- --fixtures artifacts/fixtures/jfk.wav src/AiLocalApi.Desktop/bin/Debug/net10.0-windows/worker/ffmpeg
dotnet run --project tools/AiLocalApi.Verify -- --seed artifacts/performance
```

四语真实音频与界面回归：

```powershell
# PowerShell 7；下载固定版本的 FLEURS 中、日、英、韩样本（每种最多读取 2 MiB 归档）
./scripts/prepare-multilingual.ps1
# 完整格式测试另运行 ./scripts/prepare-fixtures.ps1
$env:AILOCAL_WORKER = "$PWD/src/AiLocalApi.Desktop/bin/Debug/net10.0-windows/worker/AiLocalApi.Worker.exe"
dotnet run --project tools/AiLocalApi.Verify -- artifacts/multilingual-check artifacts/models artifacts/fixtures/jfk.wav cpu --multilingual --diagnostics
```

`dotnet test` 包含多密钥/迁移、真实 HTTP 端口切换、最小及默认窗口布局和界面状态回归。离屏渲染图片写入 `artifacts/ux-results`；它们不等同于人工桌面交互测试。四语回归检查指定语言、自动检测、Unicode 词与时间边界，同时记录字符错误率；tiny 的识别质量不作为生产精度承诺。新增体验检查及结果见 [体验验收](docs/ux-review-2026-10-28.md)。

验证报告及 Sandbox 重现步骤见 `docs/verification.md`。下载在网络停顿 30 秒、响应头超过 60 秒时报告超时并保留部分文件；可显式设置 `AILOCAL_MODEL_ORIGIN=https://hf-mirror.com` 使用公共镜像，仍按固定 SHA-256 校验。不要把测试模型和 `artifacts/` 提交到 Git。
