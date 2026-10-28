# 本地 API

默认地址 `http://localhost:17840`；仅 IPv4/IPv6 回环监听。设置 Bearer key 后除 `/health` 外均需 `Authorization: Bearer <key>`。有 Origin 的浏览器请求和非回环 Host 被拒绝。其他本地进程处于同机信任边界；不提供公网监听或 HTTP 管理写接口。

## 异步任务

```powershell
curl.exe http://localhost:17840/api/jobs -H "Authorization: Bearer YOUR_KEY" -F "file=@audio.wav" -F "model=whisper-1"
curl.exe http://localhost:17840/api/jobs/JOB_ID
curl.exe http://localhost:17840/api/jobs/JOB_ID/result?format=srt
curl.exe -X POST http://localhost:17840/api/jobs/JOB_ID/cancel
curl.exe -X POST http://localhost:17840/api/jobs/JOB_ID/retry
```

提交成功返回 **202**、`id`、`Location`。客户端断开后已持久化任务继续。`GET /api/jobs?page=0&size=50` 分页（每页最多 100）。状态：queued/running/succeeded/failed/cancelled/expired；阶段为 queued/loading/decoding/transcribing/completed/interrupted/cancelled/expired。阶段不是伪造百分比。取消重复调用幂等；仅 failed/cancelled 可重试，succeeded 不会被覆盖。结果尚未完成返回 409，失败/取消返回 422，过期返回 410。

## Whisper 兼容

`POST /v1/audio/transcriptions` 和 `POST /v1/audio/translations` 使用 multipart `file`、`model`；同步等待最终结果。翻译含义是输出英文，英语专用模型不支持翻译。`GET /v1/models` 列出已安装模型，`whisper-1.alias_of` 公开本地默认模型 ID。

| 参数 | 转写 | 翻译 |
| --- | --- | --- |
| model | 已安装 ID 或 whisper-1 | 多语言模型 ID 或对应别名 |
| language | 支持的 ISO 语言代码；省略则检测 | 不支持，明确拒绝 |
| prompt | 最长 4096 字符 | 同左 |
| temperature | 有限数值 0–1 | 同左 |
| response_format | json / text / srt / vtt / verbose_json | 同左 |
| timestamp_granularities[] | verbose_json 时支持 segment / word，可同时传入 | 不支持 |

JSON 基础响应只含 `text`。verbose_json 提供 `text/language/duration` 和请求的 `segments`（id/start/end/text）、`words`（word/start/end）。不伪造 seek、token ID、avg_logprob、no_speech_prob 或压缩比；依赖这些额外字段的客户端属于兼容边界。词时间戳由 Windows ICU 分词后聚合实际原生 token 对齐区间；一个 token 覆盖多个汉语词时区间可能重叠，不能解读为逐词强制对齐的精确边界。长音频按 30 秒受控缓冲推理，使用全局时间偏移，切块处可能影响语义连贯。

兼容请求最长等待 30 分钟；超时返回 504 并取消本地任务，断连也取消。异步提交不采用此断连取消规则。默认上传 512 MiB、解码时长 2 小时、任务临时空间 2 GiB、最多 100 个排队任务。可通过核心 Settings 配置；修改上传限制需重启 HTTP 宿主。WAV/MP3/M4A/FLAC/OGG/WebM/MP4 在 Worker 内使用 FFmpeg 共享库解码为 mono 16 kHz float PCM，不调用 CLI。空文件、损坏文件、无音轨、超限及不支持参数均报错。

错误结构为 `{"error":{"message":"...","type":"invalid_request_error","param":null,"code":"..."}}`。常见状态：400 参数错误、401 key、403 Origin/Host、404 模型或任务不存在、409 忙或冲突、413 上传过大、415 格式、422 解码/推理失败、429 队列满、503 暂停/未配置、507 临时空间不足。接受上传后才能确定的解码错误通过任务失败返回。

默认保留 7 天，从最终状态更新时间计算。过期清理上传和结果，保留 expired 元数据供查询；活动任务不清理。没有 Realtime、流式识别或 diarization。输出质量由本地模型决定，与 OpenAI 托管模型不保证一致。

验证客户端使用官方 [OpenAI .NET SDK](https://github.com/openai/openai-dotnet) 2.2.0，将 `OpenAIClientOptions.Endpoint` 指向本地 `/v1`；无需真实云端 key，不会把音频发送到云端。
