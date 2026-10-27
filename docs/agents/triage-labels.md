# Triage Labels

保留五个 canonical triage roles 的默认名称。在本地 `ROADMAP.md` 的目标条目下用 `Status: <标签>` 记录，一个条目最多有一个分诊状态。

| Canonical role | 本项目标签 | 含义 |
| --- | --- | --- |
| `needs-triage` | `needs-triage` | 等待维护者评估 |
| `needs-info` | `needs-info` | 等待补充信息 |
| `ready-for-agent` | `ready-for-agent` | 需求充分明确，可由代理执行 |
| `ready-for-human` | `ready-for-human` | 需要人工实现 |
| `wontfix` | `wontfix` | 不予实施 |

技能提到某一分诊角色时，使用表中的对应标签。未分诊的新任务默认使用 `needs-triage`；技能或用户明确指定其他状态时遵循该要求。进度通过 ROADMAP 分区和复选框表达，不把 `claimed`、`resolved` 或 `completed` 写入分诊 `Status:`。

后续如需改名，更新本表的「本项目标签」列并同步现有条目。
