# AiLocalApi

Windows 11 x64 本地语音转写托盘应用。开发不需要安装包、Release、Python、Docker 或 Whisper/FFmpeg CLI。

安装 .NET SDK **10.0.301** 后，在仓库执行：

```powershell
dotnet restore AiLocalApi.slnx --locked-mode
./scripts/build.ps1
dotnet test AiLocalApi.slnx --no-build
./src/AiLocalApi.Desktop/bin/Debug/net10.0-windows/AiLocalApi.Desktop.exe
```

宿主、核心应用服务、通信契约、推理 Worker 和测试分别位于 `src/`、`tests/`。Worker 通过当前用户专用命名管道连接，父进程消失时自动退出。`AILOCAL_DATA` 可指定数据绝对路径，`AILOCAL_WORKER` 可覆盖 Worker 可执行文件路径。日志只记录诊断代码和任务 ID，不记录音频、提示或转写文本。
