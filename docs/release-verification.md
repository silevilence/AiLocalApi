# 发行验收记录

日期：2026-10-28。Windows 11 x64，.NET SDK 10.0.301，Velopack 1.2.161，NSIS 3.12。干净环境为 Windows Sandbox，8 GiB 内存、禁用 vGPU，未安装 .NET SDK；T: 为 Sandbox 内 subst 映射。

## 已执行结果

- Release 构建：0 警告、0 错误；23 项 xUnit 测试通过，包含路径迁移与更新暂停领取任务的新增回归。
- 自包含 publish 与 vpk/NSIS pack：本地 0.1.0 和 0.1.1 均成功。两版具有 NSIS 安装 EXE、Portable ZIP、完整 nupkg 与 releases.win.json；原始 Velopack Setup 保留为中间载荷。
- 干净 Sandbox CPU 核心回归：真实模型校验、转写、同模型复用、空闲卸载、七格式、44 秒全局时间戳、取消、重试、Worker 崩溃恢复、损坏音频隔离通过。证据：`artifacts/release-results/baseline-core.txt`。
- 干净 Sandbox HTTP 回归：官方 .NET SDK 中日英韩转写、五格式、时间戳、鉴权、异步任务、单实例、宿主中断后的 Worker 回收与持久任务恢复、正常退出和端口释放通过。托盘至健康接口约 4.3 秒；证据：`artifacts/release-results/baseline-http.txt`。

安装、双形态升级、图形交互和 GitHub 在线发布的结果在实际验收后追加。本文件中的构建成功不能替代这些场景的验收。
