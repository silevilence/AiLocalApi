# 本地开发、打包与发行验收

开发使用 Windows x64、.NET SDK 10.0.301，完全不依赖 GitHub Release。PowerShell 7：

```powershell
dotnet restore AiLocalApi.slnx --locked-mode
./scripts/setup-native.ps1
./scripts/dev.ps1 -DataDirectory D:/AiLocalApi-dev
./scripts/test.ps1 -Configuration Release
```

测试和开发可以使用不同配置，避免覆盖运行中的程序。`--tray` 仅启动托盘；`--exit` 让同一发行副本正常退出；`--update` 请求同一副本检查并安全升级。不同副本各有单实例范围，但同一端口仍只能被一个 API 使用。

## 固定工具与本地产物

工具版本见 `global.json`、`.config/dotnet-tools.json`、`packaging/tools.json` 和 `Directory.Packages.props`：Velopack SDK/CLI 1.2.161、NSIS 3.12、MSVC CRT/OpenMP 14.44.35112。NSIS 编译器默认位于 `C:/Program Files (x86)/NSIS/makensis.exe`，可用 `-MakeNsis` 指定同版本路径。MSVC 运行库目录通过 `-VcRedist` 提供；发行物包含这些可分发 DLL，不要求用户安装 SDK、编译器或 FFmpeg 命令行。

```powershell
./scripts/publish.ps1 -Version 0.1.0 -OutputDirectory artifacts/publish/test-01 -UpdateSource C:/ReleaseFeed
./scripts/pack.ps1 -Version 0.1.0 -PublishDirectory artifacts/publish/test-01 -OutputDirectory artifacts/releases/test-01
```

每次发布使用新的输出目录，防止混入历史 DLL。第二版用 0.1.1 和新的目录；将第二版完整包与 `releases.win.json` 放入 Sandbox 内 `C:/ReleaseFeed`。本地更新源也可通过仅用于测试的 `AILOCAL_UPDATE_SOURCE` 覆盖。`AILOCAL_DISABLE_UPDATE_CHECK=1` 关闭定时检查，手动检查仍然可用，便于验收时控制触发时机。

用户下载 `AiLocalApi-<版本>-win-x64-Setup.exe` 或 `AiLocalApi-win-Portable.zip`。前者是 NSIS 引导，内嵌的 `AiLocalApi-win-Setup.exe` 只作为中间载荷；后者是 Velopack 真正的便携布局，包含 `.portable`、`AiLocalApi.exe`、`Update.exe` 和 `current/`。完整 `nupkg` 与 `releases.win.json` 用于更新。`assets.json` 记录对外资产的大小与 SHA-256。可选 `-SignTemplate` 接入外部签名工具，不把证书或凭据保存进仓库。

## 数据、自启与更新

安装版首次运行显示数据目录选择窗口，数据必须放在安装目录外；模型仍需明确选择或导入才会下载。绿色版默认保存到发行根目录 `Data/`，不在可替换的 `current/` 内。每个副本的 `instance.json` 保存数据位置和自启标识；绿色版移动后在启动调度前备份数据库，并以事务重定位原数据目录内部的模型、输入路径，外部目录不改动。

两种形式都默认关闭登录自启。显式开启时使用发行根目录的 `AiLocalApi.exe --tray`；移动后的绿色版仅修复自己已启用的启动项。卸载钩子只清理该副本自启项；安装版外部数据保留。删除绿色版前应关闭自启并保留需要的 `Data/`。

发行版每次启动 30 秒后、之后每六小时检查对应 `win` 通道正式版本。下载与校验完成后暂停接单和领取任务，等待当前转写持久化完成，退出 Worker，再启动 Velopack 更新器并重启。队列在原数据库中恢复。开发运行不调用更新源；检查和下载失败只显示诊断，不影响 API。更新不调用 NSIS、不重选路径。

## Windows Sandbox 验证

干净环境使用 Windows Sandbox，禁用 vGPU 验证 CPU 基线；真实 GPU 验证保留在宿主机。映射产物、模型及音频为只读，单独映射结果目录为可写。不在 Sandbox 安装 SDK。

```powershell
wsb start --config '<Configuration><vGPU>Disable</vGPU><Networking>Enable</Networking><MemoryInMB>8192</MemoryInMB></Configuration>'
wsb connect --id <ID>
wsb share --id <ID> -f "$PWD/artifacts" -s C:\Input
wsb share --id <ID> -f "$PWD/artifacts/release-results" -s C:\Results --allow-write
wsb exec --id <ID> -r ExistingLogin -c 'C:\Input\publish\test-01\verify\AiLocalApi.Verify.exe --sandbox-drive'
```

T: 使用 Sandbox 内 `subst` 映射测试目录，用于验证非 C: 路径，不代表第二块物理硬盘。首次安装从 NSIS 开始，绿色版解压到另外一个中文空格路径。图形验收检查选择目录、取消、权限错误、首次数据选择与重复安装；程序化验收命令如下（`--release` 仅允许 Sandbox 用户）：

```text
AiLocalApi.Verify.exe --release "T:\安装 中文" "T:\持久 数据" C:\Input\models C:\Input\fixtures\jfk.wav 0.1.0 installed 0.1.1 C:\ReleaseFeed --report C:\Results\installed-upgrade.txt
AiLocalApi.Verify.exe --release "T:\绿色 中文" "T:\绿色 中文\Data" C:\Input\models C:\Input\fixtures\jfk.wav 0.1.0 portable 0.1.1 C:\ReleaseFeed --report C:\Results\portable-upgrade.txt
```

该命令为自动化测试准备独立数据配置和模型，不能替代首次运行窗口验收。它验证真实转写、单实例、长转写期间升级、排队任务恢复、数据与模式保持、稳定自启路径及正常退出。另需检查空闲更新、断网/损坏包、绿色目录移动、安装版卸载记录与快捷方式，并保留真实结果；未运行的场景不能视为通过。

SDK API 与打包参数依据 [Velopack Setup 参数](https://docs.velopack.io/reference/cli/content/setup-windows)、固定 1.2.161 NuGet XML API 文档及实际 `vpk pack --help`；目录页依据 [NSIS Modern UI](https://nsis.sourceforge.io/Docs/Modern%20UI%202/Readme.html)。
