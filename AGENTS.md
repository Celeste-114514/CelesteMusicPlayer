# AGENTS.md — 给任何 AI Agent（Codex / Claude / Cursor / 其他）的入口

> 这个文件是给 AI 看的，不是给人看的。人请看 `README.md`。
> **动手之前先读完 `collab/PROJECT-BRIEF.md`**，那是本项目的压缩包。读完你就知道该碰什么、不该碰什么。

## 项目

CelesteMusicPlayer — WinUI 3 + C# 的 Windows HiFi 音乐播放器（.NET 9 / win-x64）。

## 三条硬规矩（违反会造成实际损失）

1. **搜索有地盘**。仓库里堆着大量 obj/bin 备份和构建缓存，里面塞着 9000+ 行的自动生成 `.g.cs`。
   全仓搜索会瞬间吃掉大量上下文并且结果全是噪音。**搜索前先读 `collab/PROJECT-BRIEF.md` 的「搜索地盘」一节。**

2. **允许多 agent 并行维护**。源码独占锁已停用。修改前查看 `git status`、相关任务单和近期 diff，
   保留其他 agent 的工作；遇到同文件重叠改动时，按实际 diff 合并处理。

3. **产物要能独立回退**。一次任务一个 commit，不 `git add -A`，不 rebase / force-push。
   详见 `collab/PROJECT-BRIEF.md` 的「交付约定」。

## 日常协作

- 任务单模板：`collab/_TEMPLATE-TASK.md`
- 交接协议：`collab/README.md`
- 项目压缩包（必读）：`collab/PROJECT-BRIEF.md`

## 一次会话的标准起手式

```
读 collab/PROJECT-BRIEF.md
查看 git status 和相关任务单
（有任务单就读 collab/TASK-*.md，没有就等用户派）
```
不要 `ls -R`，不要全仓 grep，不要通读 MainWindow.xaml（8941 行）。
这些信息 Project Brief 里都已经替你读过了。
