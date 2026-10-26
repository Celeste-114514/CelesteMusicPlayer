# collab/ — 双引擎协作交接区（WorkBuddy ⇄ Codex）

这个目录是**交接区**，不是源码。放的是"给另一个 AI 看的东西"。

## 为什么要有它

本项目有两个 agent 在干活：

- **WorkBuddy（本地）**：有完整工具链，能读文件、grep、跑构建、看日志、跑冒烟脚本。
  干的是"手脚活"：取上下文、批量改、构建验证、归档。
- **Codex（远端）**：判断力强但额度有限。干的是"脑力活"：根因假设、架构决策、重构路径、code review。

两边不共享记忆。唯一的共享方式就是这个目录里的文件。
**人不做的转述工作，让文件去传递。**

## 文件命名

| 文件 | 谁写的 | 给谁看 | 含义 |
|---|---|---|---|
| `PROJECT-BRIEF.md` | WorkBuddy | Codex | 项目压缩包。**Codex 每次新会话读这一个就够了** |
| `_TEMPLATE-TASK.md` | WorkBuddy | 双方 | 任务单模板，复制后改 |
| `TASK-YYYYMMDD-短名.md` | WorkBuddy | Codex | 派给 Codex 的任务（含压好的上下文） |
| `RESULT-YYYYMMDD-短名.md` | Codex | WorkBuddy | Codex 的结论 / 方案 / patch 说明 |
| `VERIFY-YYYYMMDD-短名.md` | WorkBuddy | 双方 | 落地后的验证结果（构建+冒烟证据） |
| `LOCK.md` | 双方 | 双方 | 历史协作提示；源码独占锁已停用 |

日期用东八区。同一个任务三个文件的"短名"要一致，才找得齐。

## 多 agent 并行协作

用户已于 2026-10-26 明确停用源码独占锁，多个 agent 可以并行修改源码。
修改前查看 `git status`、相关任务单和近期 diff；保留别人的改动，重叠处按实际内容合并。
`LOCK.md` 保留给旧任务单兼容引用，不得据此阻塞工作。

## 任务生命周期

```
1. WorkBuddy 采集上下文（读文件/日志/grep）→ 压缩成 TASK-*.md
2. 在任务单记录工作范围与相关文件
3. Codex 读 AGENTS.md → PROJECT-BRIEF.md → TASK-*.md，产出 RESULT-*.md
4. WorkBuddy 读 RESULT，结合当前 diff 落地代码
5. WorkBuddy 构建 + 冒烟，证据写进 VERIFY-*.md
6. 一次任务一个 commit
```

## 压缩上下文的三条要求（给 WorkBuddy 的约束）

发给 Codex 的任务单必须满足：

1. **给坐标，不给全文**。用 `文件:行号` + 10~30 行片段，不要贴整个文件。
   反面教材：MainWindow.xaml 8941 行，整份贴过去就是近十万 token。
2. **给已经做完的排查**。已经 grep 过、已经试过、已经排除的，全部列出来，
   别让 Codex 重复烧一遍同样的钱。
3. **给明确的产出形态**。要"一段可直接替换的代码"还是"三个方案选一个"，写清楚，
   别让它写 2000 字论述然后你自己再提炼一遍。
