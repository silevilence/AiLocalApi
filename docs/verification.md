# 本地与 Windows Sandbox 验证

日期：2026-10-28。范围为 ROADMAP「建立核心工程与本地开发骨架」至「提供本地模型与任务管理界面」，共 13 项。后续 NSIS、Velopack、升级和 GitHub 发布任务不在本次范围。

## 环境与输入

- Windows 11 x64，Intel Core Ultra 7 165U，14 个逻辑处理器，Intel Graphics；没有 NVIDIA 显卡。
- .NET SDK 10.0.301、Avalonia 11.3.9、Whisper.net CPU/Vulkan 1.9.1、FFmpeg.AutoGen 9.0.1.1、FFmpeg 9.0.2 LGPL shared，具体来源与摘要见 THIRD-PARTY.md。
- Sandbox 使用自包含 win-x64 程序、随包 FFmpeg 和 MSVC CRT/OpenMP；不安装 .NET、编译工具、Python、Whisper CLI 或 FFmpeg CLI。
- 英文样本为 whisper.cpp 固定提交中的 11 秒 JFK WAV。中文样本由 Windows Huihui SAPI 本地生成。`prepare-fixtures.ps1` 固定英文摘要，并通过 FFmpeg 共享库生成六种其他封装及 44 秒跨块样本。
- tiny / tiny.en F16 均经过固定大小及 SHA-256 校验。默认 Hugging Face 源在本机出现 TLS/超时；真实下载测试使用显式选择的 hf-mirror.com，校验要求不变。

## 可重现入口

```powershell
dotnet restore AiLocalApi.slnx --locked-mode
./scripts/setup-native.ps1
./scripts/build.ps1
dotnet test AiLocalApi.slnx --no-build
./scripts/prepare-fixtures.ps1

$env:AILOCAL_WORKER = "$PWD/src/AiLocalApi.Desktop/bin/Debug/net10.0-windows/worker/AiLocalApi.Worker.exe"
# models 中须有已校验 ggml-tiny.bin；第一次可显式附加 --download。
./tools/AiLocalApi.Verify/bin/Debug/net10.0-windows/AiLocalApi.Verify.exe artifacts/check artifacts/models artifacts/fixtures/jfk.wav cpu --suite
# 独立临时数据与模型目录，避免修改正在使用的模型。
$env:AILOCAL_MODEL_ORIGIN = 'https://hf-mirror.com'
./tools/AiLocalApi.Verify/bin/Debug/net10.0-windows/AiLocalApi.Verify.exe artifacts/model-check artifacts/models artifacts/fixtures/jfk.wav cpu --copy-model --models
```

验证工具的 `--models` 会下载 tiny.en、取消并续传、切换推理，然后删除**测试目录**中的该模型；请使用 `--copy-model` 和独立数据目录。`--suite` 只终止它自己创建的 Worker，用于验证恢复。

## 验证结果

| 检查 | 结果与证据 |
| --- | --- |
| 固定依赖、构建、测试 | Debug 构建 0 警告、0 错误；13 项 xUnit 全通过，包含连续 500 次真实 Worker 管道请求和日志文件被占用时的存储可用性 |
| 数据与迁移 | 配置重启保持；中文空格路径；较新 schema 拒绝写入；故障迁移保留原表、版本及备份 |
| 资源准入 | 空上传、上传超限和临时空间超限均被拒绝，没有残留任务、文件或启动的 Worker |
| 模型管理 | 真实下载、字节进度、取消、续传、同 ID 合并、SHA-256、导入、损坏文件拒绝、默认别名、模型切换、安全删除 |
| CPU 推理 | 中文与英文真实样本；自动检测、指定语言和英文翻译；tiny 的中文精度有限，验证不将非空输出等同于准确率达标 |
| Vulkan | 原生诊断确认 `Intel(R) Graphics`，实际转写成功；冷加载 1 次、同模型复用、TTL 后 Worker 消失 |
| CUDA 缺失 | 显式选择 cuda 得到 failed 和运行库/驱动要求，未静默回退或显示 GPU 成功；真实 CUDA 硬件未实测 |
| 解码 | WAV、MP3、M4A、FLAC、OGG、WebM、MP4 全通过，时长 11.000–11.040 秒；损坏文件失败后下个任务仍成功 |
| 全局时间戳 | 44 秒输入跨过 30 秒块边界，第二块词时间大于 30 秒，duration 为 44 秒；中英文词经 ICU 分词和原生对齐区间聚合 |
| 生命周期 | 冷加载、复用计数、2 秒 TTL、忙时拒绝卸载/删除、空闲回收 Worker；GPU 资源释放以 Worker 退出为证，不以整机 GPU 归零判断 |
| 队列与恢复 | 多任务、排队/运行取消、重试、Worker 强制结束后有限恢复；状态条件更新防止取消后被成功结果覆盖 |
| 桌面实际操作 | 选择音频、长音频转写、选中结果、导出文本；在设置中选镜像后下载 tiny.en 成功并校验；下载期间切换页，万条历史任务翻页及滚动 |
| 窗口与单实例 | 关闭窗口后 health 可用，重复打开恢复同一 PID 的窗口；窗口隐藏停止刷新，退出释放端口 |

Sandbox 最终回归全部通过：`final-core.txt` 包含七格式、44 秒全局时间戳、取消/重试、忙时卸载/删除、Worker 崩溃和损坏输入恢复；`final-http.txt` 包含官方 OpenAI .NET SDK 中英文转写/英文翻译、segment/word、五种 HTTP 格式、鉴权、Origin、异步查询、单实例、强制结束宿主后的持久任务恢复、锁屏后 health 可达、正常退出与端口释放。托盘启动到 health 为 7,146 ms，启动工作集约 79.2 MiB。自动后端在 Sandbox 中选择到透传的 Intel Vulkan，并通过转写、复用及卸载；因此这次 Sandbox 不能作为“没有 GPU”的硬件环境证明。

随后创建第二个 Sandbox，显式配置 `<vGPU>Disable</vGPU>` 和 `<Networking>Disable</Networking>`。最终程序再次通过完整核心回归（`final-no-gpu-core.txt`）和 HTTP/SDK/崩溃恢复/锁屏/退出回归（`final-no-gpu-http.txt`），两次退出码均为 0；托盘启动到 health 为 6,784 ms，工作集约 80.6 MiB。`no-gpu-auto.txt` 确认 backend=cpu，真实转写、复用和卸载通过；显式 Vulkan 的负向测试得到 failed 和“未检测到可用 GPU”，符合预期。最后用修复后的 Worker 在宿主 Intel 上再次验证 backend=vulkan、GPU devices=Vulkan0，结果与卸载均通过。

## 性能测量

运行 `--seed artifacts/performance` 创建 10,000 条历史任务；第 101 页只返回 50 条，冷查询约 64–80 ms。界面每页 50 条，并使用 VirtualizingStackPanel；截图中仅创建可见区域约 5 条容器。数据无变化时跳过列表重建；清理每分钟执行，过期索引和分页索引避免每秒扫描历史。

```powershell
./tools/AiLocalApi.Verify/bin/Debug/net10.0-windows/AiLocalApi.Verify.exe --seed artifacts/performance
./scripts/measure-process.ps1 -ProcessId <宿主PID> -State tray
./scripts/measure-process.ps1 -ProcessId <宿主PID> -State window
./scripts/measure-process.ps1 -ProcessId <宿主PID> -State downloading
./scripts/measure-process.ps1 -ProcessId <WorkerPID> -State inference
```

每次采样约 5.5 秒。下表 CPU 以一个逻辑核的 100% 为单位，除以 14 才是相对于全部逻辑核的占比。测量同时存在远程/虚拟显示和 Sandbox，属于本机观测，不是通用性能承诺。

| 状态 | 进程 | CPU（单核基准） | 工作集 MiB | 采样峰值 MiB |
| --- | --- | ---: | ---: | ---: |
| 仅托盘，未打开窗口 | 宿主 | 6.85% | 107.2 | 107.2 |
| 窗口打开，软件渲染 | 宿主 | 37.06% | 152.9 | 152.9 |
| 镜像真实下载 | 宿主 | 61.88% | 190.7 | 194.1 |
| 长音频推理中 | 宿主 | 52.78% | 232.4 | 245.8 |
| 长音频推理中 | Worker | 276.81% | 149.8 | 291.8 |
| 下载后隐藏窗口 | 宿主 | 4.27% | 199.0 | 199.0 |

关闭并重新打开同一窗口三次，工作集为 206.6 / 207.4 / 211.2 MiB，句柄为 932 / 889 / 921；窗口实例及刷新计时器复用，没有每次新建窗口或不断追加历史控件。短时采样存在缓存增长，不能据此宣称已完成长期泄漏测试。默认 GPU 渲染在此环境约 249 MiB；DirectComposition 对照没有改善。静态管理界面因此选择软件渲染；Worker 的 Vulkan/CUDA 选择独立。

原始性能 JSONL、TRX 和真实音频结果保存在忽略的 `artifacts/`、`TestResults/`，不把模型或用户音频提交到 Git。

## Sandbox 重现

```powershell
./scripts/publish-sandbox.ps1
# 先启动 Windows Sandbox，以下 ID 使用 wsb list 返回的本次实例。
wsb share --id <ID> -f "$PWD/artifacts/sandbox" -s C:\AiLocalApi
wsb share --id <ID> -f "$PWD/artifacts/sandbox-results" -s C:\Results --allow-write
wsb exec --id <ID> -r ExistingLogin -c 'C:\AiLocalApi\verify\AiLocalApi.Verify.exe --sandbox-drive'
wsb exec --id <ID> -r ExistingLogin -c 'C:\AiLocalApi\verify\AiLocalApi.Verify.exe "T:\中文 空格" C:\AiLocalApi\models C:\AiLocalApi\fixtures\jfk.wav cpu --copy-model --suite --report C:\Results\final-core.txt'
wsb exec --id <ID> -r ExistingLogin -c 'C:\AiLocalApi\verify\AiLocalApi.Verify.exe --http C:\AiLocalApi\app\AiLocalApi.Desktop.exe "T:\中文 空格" C:\AiLocalApi\fixtures\jfk.wav --lock --report C:\Results\final-http.txt'
# 无 GPU / 无网络环境使用新的 Sandbox 实例：
wsb start --config '<Configuration><vGPU>Disable</vGPU><Networking>Disable</Networking></Configuration>'
# 对新 ID 执行 share、connect，再运行同样的 CPU 回归，另检查 auto 和显式 vulkan。
```

`--sandbox-drive` 用 subst 创建 T: 盘符：证明路径不依赖 C:，不代表已测试另一块物理硬盘。`--lock` 只允许 Sandbox 的 WDAGUtilityAccount，绝不锁宿主机。应用映射只读、结果映射可写；将数据指向只读应用映射得到包含具体路径的 UnauthorizedAccessException，桌面启动路径会将此类错误显示为诊断窗口。

## 整体审核与修复

- 修复 Sandbox 暴露的协议竞态：宿主收到 done 后立即发下一请求，Worker 内部 Task 尚未完成时曾误报 busy。现在在最终回复前释放忙状态，并用连续 500 次请求以及真实取消/重试回归覆盖。
- 修复取消的延迟终止影响下一请求风险、重复释放已断开管道、空闲卸载观测竞态；原生推理不响应取消时仅终止对应 Worker。
- 修复异步 verbose_json 下载丢失已请求词时间戳；HTTP 停止超时仍回收应用服务和 Worker。
- 无 GPU Sandbox 揭示 Vulkan 库会内部使用 CPU；现在从已加载 ggml 的设备注册表检查 GPU，而不是仅根据运行库名称宣告成功。
- 日志写入被占用或缺少权限时，不再因诊断写入异常终止调度器。
- 增加数据库历史及过期索引、清理限频、界面修订号检查，避免万条任务每秒全量扫描。
- 将 win-x64 放入公共 RuntimeIdentifiers，避免自包含发布改写 lock 文件后普通 locked-mode 还原失败。
- Sandbox 初次运行发现缺少 MSVC/OpenMP；发布验证包现在显式携带可分发运行库，未依赖宿主机隐式安装。

边界：CUDA 真机、实体第二硬盘、长期运行泄漏、真实用户注销后再次登录未进行实机验收；登录启动注册值、当前入口和 --tray 行为由代码及启动测试检查。tiny 的中文识别与翻译可能有明显错误；单个原生 token 跨多个中文词时词时间可能重叠。没有宣称完整 OpenAI 服务语义、实时识别、流式输出或说话人分离。

Sandbox 命令与隔离选项依据 [Microsoft CLI 文档](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli) 和 [配置文件文档](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)。设备查询与工厂初始化依据 [ggml 后端接口](https://github.com/ggml-org/whisper.cpp/blob/v1.8.3/ggml/include/ggml-backend.h) 和 [Whisper.net 1.9.1 工厂实现](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/WhisperFactory.cs)，并核对随包 DLL 的实际导出符号。
