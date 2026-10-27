# Agent instructions

## Windows 文件清理

- 对用户已授权的文件清理，先核实目标绝对路径、清理范围和需要保留的内容，再由代理执行并核验结果；不要默认把清理交给用户手动执行。
- 普通 PowerShell 删除使用 `Remove-Item -LiteralPath <已核实路径> -ErrorAction Stop`，删除目录时按需加 `-Recurse`。不要习惯性添加 `-Force`；保留隐藏、只读等文件的保护。
- 若普通删除遇到隐藏、只读、占用或权限错误，明确报告具体目标与原因，使用受支持的审批流程处理需要额外授权的操作。不要通过更换解释器、编码命令、修改文件属性或关闭安全策略来规避拒绝。
- 遇到 `blocked by policy` 时，区分命令策略拒绝、沙箱边界与操作系统错误。只有证据表明自动审核参与时，才称为自动审核拒绝；完全访问下也不要无依据地要求用户重新开启权限。

## Agent skills

### Issue tracker

使用根目录 `ROADMAP.md` 统一跟踪本地任务、需求与进度。执行涉及任务读取、发布或更新的技能前，先阅读 `docs/agents/issue-tracker.md`。

### Triage labels

使用默认标签：`needs-triage`、`needs-info`、`ready-for-agent`、`ready-for-human`、`wontfix`。分诊前先阅读 `docs/agents/triage-labels.md`。

### Domain docs

采用 single-context：根目录 `CONTEXT.md` 与 `docs/adr/`。探索代码前先阅读 `docs/agents/domain.md`，再按其中规则读取领域文档。
