# 发行验收记录

日期：2026-10-28。Windows 11 x64，.NET SDK 10.0.301，Velopack 1.2.161，NSIS 3.12。干净环境为 Windows Sandbox，8 GiB 内存、禁用 vGPU，未安装 .NET SDK；T: 为 Sandbox 内 subst 映射。

## 已执行结果

- Release 构建：0 警告、0 错误；23 项 xUnit 测试通过，包含路径迁移与更新暂停领取任务的新增回归。
- 自包含 publish 与 vpk/NSIS pack：本地 0.1.0 和 0.1.1 均成功。两版具有 NSIS 安装 EXE、Portable ZIP、完整 nupkg 与 releases.win.json；原始 Velopack Setup 保留为中间载荷。
- 干净 Sandbox CPU 核心回归：真实模型校验、转写、同模型复用、空闲卸载、七格式、44 秒全局时间戳、取消、重试、Worker 崩溃恢复、损坏音频隔离通过。证据：`artifacts/release-results/baseline-core.txt`。
- 干净 Sandbox HTTP 回归：官方 .NET SDK 中日英韩转写、五格式、时间戳、鉴权、异步任务、单实例、宿主中断后的 Worker 回收与持久任务恢复、正常退出和端口释放通过。托盘至健康接口约 4.3 秒；证据：`artifacts/release-results/baseline-http.txt`。

## 本地双形态验收

- NSIS 图形首屏直接选择 `T:\安装 中文 空格`，无第二套安装向导；安装结果、注册位置、稳定入口和唯一卸载器核对通过。首次运行窗口实际选择 `T:\持久 数据`，确认后启动 API。
- 安装版和绿色版各自完成 0.1.0 → 0.1.1 升级。在 660 秒测试音频已经运行时请求更新，下载校验后等待转写完成；活动任务 attempts=1，排队任务重启后完成。两种模式、原位置、模型、配置与已完成结果保留，升级后再次真实转写成功。证据：`installed-upgrade.txt`、`portable-upgrade.txt`。
- 绿色版由 `T:\绿色 中文 空格` 移至 `T:\移动后 绿色 中文`，不重新导入模型；启动时修复模型路径、历史输入路径与已有自启项，转写通过。证据：`portable-move-network.txt`。
- 安装版、绿色版分别测试更新源连接拒绝和摘要/长度不符的损坏 nupkg。错误可诊断，原版本 0.1.1 与 accepting API 保持不变，模型、历史结果保留，之后转写成功。证据：`installed-network.txt`、`installed-corrupt.txt`、`portable-move-network.txt`、`portable-corrupt.txt`。连接拒绝不冒充断流下载测试。
- 重复 NSIS 安装返回 2，拒绝覆盖或降级。原安装升级后系统仍为一条 0.1.1 卸载记录，原目录不变；NSIS 临时 Payload.exe 无残留。由系统记录中的 Update.exe 卸载返回 0，卸载记录不存在、自身 Run 项已删除，外部数据库和 ggml-tiny.bin 均保留。证据：`upgraded-registry.json`、`uninstall-result.json`。
- 不可写的 Sandbox 只读映射目标被拒绝，返回 2。替换内嵌载荷为无法独立运行的测试 apphost，模拟内层安装失败，外层返回 1，没有卸载记录；未显示安装成功。证据：`readonly-install-exit.txt`、`failed-inner-exit.txt`、`failed-inner-registry.txt`。
- 图形目录页点击取消并确认退出，回到 Sandbox 桌面；默认安装目录与卸载记录均不存在。卸载后开始菜单快捷方式为 0、Worker 为 0，另一绿色副本的 Run 项保持原路径。证据：`cancel-and-cleanup.json`。
- 全新安装与解压副本分别在无排队、无运行任务时完成 0.1.0 → 0.1.1 升级，保留模型、配置和历史结果，升级后再次转写通过。证据：`installed-idle.txt`、`portable-idle.txt`。
- 两种模式均由测试 TCP 服务提供有效更新索引，在 nupkg 开始传输 4096 字节后重置连接；确认实际包下载被中断、更新失败被记录，旧版 0.1.1 继续接单且再次转写成功。证据：`installed-interrupted.txt`、`portable-interrupted.txt`。该场景独立于完整损坏包与连接拒绝。
- 安装版与绿色版分别单独启用本副本 Run 项，在 Sandbox 内实际执行注销后通过 `wsb connect` 重新登录；未手动启动应用，均出现单个宿主进程、MainWindowHandle=0、对应模式/version=0.1.1 的健康接口与 accepting=true。为让测试盘跨会话可见，另在 Sandbox System 上下文将 T: 映射到 C:\TestDrive。证据：`installed-logon.json`、`portable-logon.json`。这补齐此前验证报告中未实测的注销后登录边界。
- 宿主使用 local-04 发行 Worker 与原生库实测 Intel Graphics Vulkan，实际设备 Vulkan0，转写、同模型复用、TTL 卸载及 Worker 释放通过。证据：`host-vulkan.txt`；没有 CUDA 硬件，未声称 CUDA 真机通过。

## GitHub 正式发行

- [v0.1.0](https://github.com/silevilence/AiLocalApi/releases/tag/v0.1.0) 已由 [Actions 40635162426](https://github.com/silevilence/AiLocalApi/actions/runs/40635162426) 成功发布，提交 `b5089fa13da60971c3632dbe6fa21f31d9d0eb24`。Windows runner 的 23 项测试、发布输出真实 CPU 转写/复用/卸载、NSIS 与更新资产版本检查均通过。
- 首次发布作业在上传草稿后因按标签接口不返回草稿而失败，没有公开不完整版本。修正为认证列表查询后重跑成功；只在尚未发布时修正首版标签，发布后不改动标签或资产。
- 已发布版本的只读重试预检返回 Already published，核对相同提交、正文和四项完整资产后跳过，不覆盖。CI 结果存于 `artifacts/release-results/ci-010/`。
- 从实际 v0.1.0 Release 下载四项资产，全文件 SHA-256 与 GitHub 服务端摘要一致；安装外层、便携元数据、宿主与 Worker 版本均为 0.1.0。索引的 NotesMarkdown、Release 正文与对应 changelog 章节一致，没有包含旧版全文。当前连接的整流下载曾停滞，使用同一 GitHub 官方资产地址分段下载后通过完整摘要校验。
- 正式 NSIS 包通过图形目录页安装到 `T:\GitHub Install`，首次运行选择独立 `T:\GitHub Data`；正式 Portable ZIP 解压到 `T:\GitHub 绿色`。两者在 Sandbox 均通过真实转写、单实例和正常退出检查，保留 0.1.0 实例等待第二版；安装注册记录为原选定路径和单个 Update.exe 卸载器。证据：`github-installed-baseline.txt`、`github-portable-baseline.txt`、`github-baseline-registry.json`。

在线双形态升级结果在实际验收后追加。本地升级不替代在线更新验收。
