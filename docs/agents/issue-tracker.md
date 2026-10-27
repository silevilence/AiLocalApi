# Issue tracker: Local ROADMAP

本项目使用根目录 `ROADMAP.md` 作为任务与需求的统一记录位置。技能提到 issue、ticket 或发布到 issue tracker 时，均映射到该文件中的条目。此约定取代本地 Markdown 模板默认的 `.scratch/<feature>/issues/` 工作流；不要另建平行任务列表或发布到远程 Issues。

## 条目与进度

- 沿用 `ROADMAP.md` 的三个分区：`## 📝 计划中 (Planned)`、`## 🚧 开发中 (In Progress)`、`## ✅ 已完成 (Completed)`。
- 新任务默认追加到「计划中」末尾，使用 `- [ ] **任务标题**`，写明功能要求与可验证的验收标准。默认不加编号或优先级；已有约定或用户指定时遵循其约定。
- 开始执行时将完整条目移到「开发中」；验收通过后移到「已完成」，将任务及已完成子项标为 `[x]`。移动时保留说明、标签、依赖和历史。
- 以 `ROADMAP.md` 加唯一任务标题引用任务；标题重复时补充分区与上下文。没有明确编号映射时，不把 `#42` 等引用猜作行号或远程 Issue。
- 分诊标签记录在条目下的 `Status:` 字段，取值见 `triage-labels.md`。该字段表示分诊状态；分区与复选框表示执行进度，两者独立。
- `wontfix` 条目保留在「计划中」，注明拒绝原因并排除出待执行队列，不能标成已完成。
- 如需类别，使用独立的 `Category: bug` 或 `Category: enhancement` 字段。

## 技能操作映射

- **发布任务 / 拆分 issues**：在 `ROADMAP.md` 中添加独立任务条目，每条包含自己的功能要求、验收标准及必要依赖。
- **发布 spec / PRD**：在对应需求条目下保留完整规格正文；可用缩进的 `<details>` / `<summary>` 折叠长正文，并保持规格原有章节结构。
- **获取任务**：按给定标题或锚点读取条目及其完整正文、讨论和依赖。
- **更新 / 评论**：原位编辑目标条目，在条目下的「讨论记录」中追加日期与内容，保留历史。技能要求的声明也随记录保留。
- **应用标签**：更新目标条目的 `Status:`；一次只保留一个分诊状态，不创建远程标签。
- **依赖**：使用 `Blocked by:` 引用其他任务的唯一标题；只有依赖已完成并验收时才视为解除阻塞。

## 探索任务（适用于 wayfinder）

若技能需要 map 和 child tickets，在 `ROADMAP.md` 中使用一个探索总条目及独立的子任务条目。总条目保存 Notes、Decisions-so-far、Fog；子任务通过唯一标题关联，记录 `Type: research/prototype/grilling/task` 和 `Blocked by:`。

- Frontier：按文件顺序选择尚未解决、依赖已解除且未被认领的子任务。
- Claim：执行前记录 `Claim: claimed` 并保存，分诊 `Status:` 保持独立。
- Resolve：在子任务下记录 Answer，设置 `Claim: resolved`，并在总条目的 Decisions-so-far 中补充结论与任务引用。
