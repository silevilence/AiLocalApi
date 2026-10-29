# 本地模型与推理引擎

程序提供 whisper.cpp 和 CTranslate2 两条本地推理路径。whisper.cpp 通过 Whisper.net 运行；CTranslate2 4.6.0 通过项目维护的 C++ 桥接库运行，特征提取、分词、长音频窗口处理和结果格式由本项目提供。它没有运行 Faster Whisper 的 Python 包，也不承诺与 Faster Whisper 的输出完全一致。

## 选择模型

模型页可以按引擎、档位、语言能力和文件量化筛选。tiny/base 较轻，small/medium 提供更大的模型，large-v1/v2/v3 与 large-v3-turbo 提供不同的质量和速度取舍。`.en` 只用于英语；多语言模型可用于中文。turbo 支持多语言转写，但不支持音频翻译为英文；该请求会在入队前被拒绝。

显式粤语代码 `yue` 需要 large-v3 或 large-v3-turbo；较早模型的词表没有该语言 token，请求会在入队前返回 `model_language`。

whisper.cpp 使用 `ggml-*.bin`，保留 tiny 等原有 API 模型 ID。CTranslate2 使用模型包目录，ID 以 `ct2-` 开头，如 `ct2-small`。两者的权重格式不能互换。CTranslate2 的“导入模型包”需要权重、配置、分词器和词表等完整文件；离线运行不会自动补下载缺少的文件。

目录中的每个文件都固定来源修订、大小和 SHA-256。下载可取消、重试并在服务器支持时续传；所有必需文件校验通过后才登记安装。导入只接受目录内的已知文件。模型页展示的是下载大小，实际 RAM/VRAM 取决于引擎、模型及计算精度，不能把文件大小当作内存需求。

## 后端与精度

| 引擎 | 后端 | 计算精度 |
| --- | --- | --- |
| whisper.cpp | CPU、Vulkan、可选 CUDA、auto | 使用所下载的 F16 / Q5 / Q8 模型 |
| CTranslate2 | CPU、可选 CUDA、auto | default、float32、int8、int8_float32；显式 CUDA 还可选 float16、int8_float16 |

默认发行构建提供 CTranslate2 CPU 运行库，使用 Ruy 后端，不需要 Python。CUDA 构建另需匹配的 CUDA/cuDNN 开发环境，运行时需相应 NVIDIA 驱动和库。当前本地机器没有 NVIDIA 显卡，CUDA 真机尚未验收。CTranslate2 不提供 Vulkan。

固定的 CTranslate2 4.6.0 源码要求 CUDA 至少 11.0、cuDNN 至少 8；这只是上游构建下限，不是本项目已验收的发行组合。具体 CUDA/cuDNN 补丁版本、驱动下限及 GPU 分发包仍待对应硬件验证。

`auto` 尝试 GPU，失败时提示并回退 CPU；显式 CUDA 失败不会静默改用 CPU。运行诊断显示实际引擎、后端和有效计算精度，例如 `int8` 在 CPU 上可能显示为 `int8_float32`。下载文件的存储精度与运行时计算精度是两个不同的属性。

两个引擎共享任务队列、按需加载、同模型复用、空闲卸载、取消及重试规则。一个 Worker 同时执行一个任务、驻留一个模型；引擎或后端切换通过回收 Worker 释放资源。原生推理未在取消后及时返回时，宿主在三秒宽限期后回收该 Worker。

## API 能力与边界

`whisper-1` 继续指向用户设置的默认模型。任务入队时固定实际模型 ID 和所属引擎，后续默认值变化不会改写已有任务。`GET /v1/models` 增加 `engine`、`multilingual`、`supports_translation`、`quantization` 信息；异步任务返回 `engine`。

| 能力 | whisper.cpp | CTranslate2 |
| --- | --- | --- |
| 指定语言 / 自动检测 | 支持 | 支持 |
| 初始提示 / 温度 | 支持 | 支持；提示最多保留最后 180 个 token |
| 英文翻译 | 非 turbo 多语言模型 | 非 turbo 多语言模型 |
| 文本、JSON、verbose_json、SRT、VTT | 支持 | 支持 |
| 分段 / 词时间戳 | 原生 token 时间 + ICU 分词 | 原生交叉注意力 DTW 对齐 + ICU 分词 |
| 长音频 | 30 秒分块及全局偏移 | 30 秒窗口，未完成尾段按完成时间戳继续 |
| VAD、批量推理、说话人分离、实时流式 | 当前不提供 | 当前不提供 |

CTranslate2 在温度 0 时使用 beam size 5，非零温度使用采样。性能对比需记录模型、存储及计算精度、线程数、解码策略、语言、时间戳选项和硬件，不能用不同配置的耗时断言某引擎普遍更快。静音、噪声和真实人声的识别质量仍依赖模型；数字静音直接返回空结果，不伪造文本或词时间戳。

长音频在首次有声窗口检测语言，后续窗口沿用该语言；指定的初始提示在各窗口复用，不自动把上一窗口文本作为上下文。完成时间戳之后尚未解码的尾部会再次处理，无法前进时明确报错，避免卡在同一位置。

词时间来自原生 DTW；若窗口首词异常长，保留其原生结束时间，按同窗口词时长的中位数收紧起点，避免前导停顿全部归入首词。该修正属于对齐启发式，不是 VAD，词边界仍有误差，不能当作人工标注。

## 构建与验证

构建者需要 .NET 10.0.301、Visual Studio 2022 C++ 与 CMake 组件、Git。用户安装已打包应用不需要编译工具。`scripts/setup-ctranslate2.ps1` 从固定提交构建 CTranslate2 及其 C ABI 桥接，使用固定子模块修订，将 DLL 和许可证放到 `artifacts/ctranslate2/runtime-cpu`。`scripts/build.ps1` 与 `scripts/publish.ps1` 会调用该入口。`scripts/build.ps1 -EnableCuda` 选择 CUDA 构建，需要额外的 CUDA/cuDNN 工具链。

```powershell
./scripts/test.ps1
$env:AILOCAL_WORKER = "$PWD/src/AiLocalApi.Worker/bin/Debug/net10.0-windows/AiLocalApi.Worker.exe"
dotnet tools/AiLocalApi.Verify/bin/Debug/net10.0-windows/AiLocalApi.Verify.dll --engine artifacts/ct2-check artifacts/models artifacts/fixtures/jfk.wav ct2-tiny
dotnet tools/AiLocalApi.Verify/bin/Debug/net10.0-windows/AiLocalApi.Verify.dll --dual-suite artifacts/dual-check artifacts/models artifacts/fixtures
```

`--catalog` 和 `--bundle-catalog` 逐项下载、校验、加载并转写目录中的模型，可能占用数十 GiB 磁盘；验证结果写入指定数据目录的 `*.verified.json`，重复执行复用已通过记录。需要重新验收变更后的实现时，应使用新的验证数据目录。常规 CI 只运行两个引擎的 tiny CPU 验证，不代表所有硬件组合都已通过。

上游：[Whisper 模型说明](https://github.com/openai/whisper)、[whisper.cpp 模型目录](https://github.com/ggml-org/whisper.cpp/blob/master/models/README.md)、[CTranslate2 4.6.0](https://github.com/OpenNMT/CTranslate2/tree/v4.6.0)、[Faster Whisper](https://github.com/SYSTRAN/faster-whisper)。
