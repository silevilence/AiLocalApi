# 推理后端

默认 CPU，设置页可选 cpu / auto / vulkan / cuda；没有在途任务时才能切换，后续模型加载时重建 Worker。静态管理窗口使用软件渲染，与推理后端独立。

自动顺序为 CUDA、Vulkan、CPU。CUDA 依赖只在 `scripts/build.ps1 -EnableCuda` 时还原；默认构建携带 CPU/Vulkan。自动 GPU 初始化失败后重启 Worker 使用 CPU 并显示原因；显式 GPU 失败直接报告任务错误。不能把“加载了 Vulkan DLL”当作“正在使用 GPU”：Worker 读取已加载 ggml 的真实设备注册表，没有 GPU 时拒绝 GPU 模式。

2026-10-28 本机 Intel Graphics 的 Vulkan 真实转写通过，原生日志报告 Intel 设备，注册表报告 Vulkan0。相同模型复用，空闲 TTL 后 Worker 退出。Windows Sandbox 禁用 vGPU 时，自动模式成功使用 CPU；显式 Vulkan 被拒绝并显示未检测到 GPU。无 NVIDIA 硬件，CUDA 真机组合未实测；缺少 CUDA 依赖时明确报错。详细命令、资源观测与兼容边界见 verification.md 和 THIRD-PARTY.md。
