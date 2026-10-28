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

GitHub 在线发布的结果在实际验收后追加。本地升级不替代在线更新验收。
