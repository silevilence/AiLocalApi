# AiLocalApi

Windows 11 x64 本地语音转写托盘应用。开发不需要安装包、Release、Python、Docker 或 Whisper/FFmpeg CLI。

安装 .NET SDK **10.0.301** 后，在仓库执行：

```powershell
dotnet restore AiLocalApi.slnx --locked-mode
./scripts/setup-native.ps1
./scripts/build.ps1
dotnet test AiLocalApi.slnx --no-build
./src/AiLocalApi.Desktop/bin/Debug/net10.0-windows/AiLocalApi.Desktop.exe
```

宿主、核心应用服务、通信契约、推理 Worker 和测试分别位于 `src/`、`tests/`。Worker 通过当前用户专用命名管道连接，父进程消失时自动退出。`AILOCAL_DATA` 可指定数据绝对路径，`AILOCAL_WORKER` 可覆盖 Worker 可执行文件路径。日志只记录诊断代码和任务 ID，不记录音频、提示或转写文本。

首次运行在「模型」页选择目录，再下载 tiny / tiny.en / base，或导入目录支持的官方 F16 文件。默认源无法连接时，可在「设置与 API」选择 hf-mirror.com 并保存，所有来源都校验固定 SHA-256。设置默认模型后在「转写任务」选择/拖入音频；可取消、重试、导出五种格式。窗口关闭后继续处理，退出请使用托盘「退出」。模型目录更改只影响未来下载，不隐式搬移已安装文件。

开发机需要 VC++ 2015–2022 x64 Redistributable（包含 CRT/OpenMP）；自包含验证包通过 `scripts/publish-sandbox.ps1 -VcRedist <MSVC Redist x64目录>` 把允许分发的 DLL 置于 Worker 目录，Sandbox 无需安装 .NET 或编译工具。默认 CPU/Vulkan 构建，CUDA 构建使用 `./scripts/build.ps1 -EnableCuda`，需要 NVIDIA 驱动；该选项会还原对应精确版本的运行库。

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

验证报告及 Sandbox 重现步骤见 `docs/verification.md`。下载在网络停顿 30 秒、响应头超过 60 秒时报告超时并保留部分文件；可显式设置 `AILOCAL_MODEL_ORIGIN=https://hf-mirror.com` 使用公共镜像，仍按固定 SHA-256 校验。不要把测试模型和 `artifacts/` 提交到 Git。
