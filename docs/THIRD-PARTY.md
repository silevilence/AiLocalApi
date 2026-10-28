# 原生依赖与许可

绑定与 CPU/Vulkan 运行库均固定 Whisper.net 1.9.1（MIT），由其包携带匹配的 whisper.cpp / ggml。上游：[Whisper.net](https://github.com/sandrohanea/whisper.net)、[whisper.cpp](https://github.com/ggml-org/whisper.cpp)。模型目录来自 [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp)，下载和导入均校验固定 SHA-256。

FFmpeg.AutoGen 9.0.1.1（LGPL-3.0）对应 FFmpeg 9 ABI。发行使用 BtbN `autobuild-2026-10-27-18-38` 的 `ffmpeg-n9.0.2-36-g6882969ce9-win64-lgpl-shared-9.0.zip`，SHA-256 为 `f70e43c4f5670fbbf2dc4eb667b0d6b3743d68386f766c08bb2e0ec3edece398`。只复制共享 DLL，不复制或调用 CLI。构建源与配置：[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)，FFmpeg 对应提交 `6882969ce9`；[固定发行资产](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-27-18-38) 与 [对应 FFmpeg 源码](https://github.com/FFmpeg/FFmpeg/tree/6882969ce9)。使用 LGPL shared 构建，不使用 GPL/nonfree 构建。发行物的 `worker/ffmpeg/LICENSE.txt` 保留上游许可，可替换该目录内 ABI 兼容共享库；不将这些库静态链接进应用。

CPU 为基线；Vulkan 需要显卡驱动提供 Vulkan loader 和计算支持；CUDA 需要 NVIDIA GPU、匹配驱动及对应 Whisper.net CUDA 运行库。显式选择缺失的后端返回错误，不假报 GPU 成功。当前机器无 NVIDIA 显卡，CUDA 组合未实测。

Whisper.net CPU 原生库还依赖 MSVC CRT 与 OpenMP（包括 `vcomp140.dll`）。Sandbox 自包含验证包从 Visual Studio 的 **Redist** 目录复制 14.44.35112 x64 的 `Microsoft.VC143.CRT`、`Microsoft.VC143.OPENMP`，按 Microsoft Visual Studio 分发条款使用，不能从 debug_nonredist 目录复制文件。构建脚本支持 `-VcRedist` 指定合法分发目录；开发运行也可使用系统已安装的 VC++ 2015–2022 x64 Redistributable。此依赖不能被 .NET 自包含发布取代。


## 多语言验证音频

Google [FLEURS](https://huggingface.co/datasets/google/fleurs)（Conneau et al., 2022），CC-BY-4.0。开发脚本从固定版本 `633a7b54485c911bc1df035d1d4384656eaa4724` 的 dev 归档提取各语言第一个 WAV，不修改音频；语言、原路径、摘要和参考文本见 `scripts/multilingual-fixtures.json`。测试覆盖中、日、英、韩，不构成完整语言质量基准。音频下载至忽略的 artifacts 目录，不纳入源码或正式发行包；Sandbox 验证材料中附带来源清单。
