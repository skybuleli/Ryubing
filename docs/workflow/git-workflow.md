# Git 工作流规范 — Ryubing (Forgejo: git.ryujinx.app)

> 适用范围: `master` 为主干的 **Trunk-Based** 单主干模型。`P1 Metal (Slang->DXIL->MSC)` 专栏在 `docs/p1-metal/` 按本文档门禁执行。本地与远端同规。

## 1. 决策

| 项 | 选择 | 理由 |
|---|---|---|
| 模型 | **Trunk-Based** + 短命分支 | 团队小、发布频(`canary.yml`推`master`即发版)，GitFlow 重分支/长期 `develop` 增加合并成本 |
| 主干 | `master` (远端 `origin/master`，受保护) | 历史主分支即 `master`，保持与 `build.yml: branches:[master]`/`canary.yml: push master` 一致，不迁 `main` |
| 合并 | **Squash Merge** 默认 | 线性历史，`git log --oneline` 可读；特例多提交需保留考古时用 `Rebase Merge` 并需维护者批准 |
| 提交 | **Conventional Commits** | `labeler.yml` 与 `renovate.json` 已按 `feat/fix/refactor` 语义分类，CI 可按 `type` 自动打标/生成 CHANGELOG |

```
master ──●──●──●──●──●──●─ (受保护, 只能 PR 合入, 需 CI 绿)
           \  |  \  |
feat/* ─────●──●  |  ●─  ( <5 天, rebase 后 PR)
fix/*  ──────────●
docs/* ─────────────●
```

## 2. 分支命名

```
<type>/<scope>-<slug>[-#issue]
```

`type`: `feat|fix|perf|refactor|docs|chore|ci|test`
`scope`: `cpu|gpu|metal|shader|audio|hle|kernel|gui|infra|p1-metal`
例: `feat/metal-slang-gen`, `fix/gpu-texture-leak#174`, `docs/p1-master`, `feat/p1-metal-msc-#12`

规则:
- 全小写、短横分隔、<40 字符
- 关联 Issue 必须后缀 `#<id>`
- 禁 `feature/` / `bugfix/` 等非规范前缀 (与 `labeler.yml` 的 `any-glob-to-any-file` 判定冲突时以本表为准)

## 3. 提交规范 (Conventional Commits)

```
<type>(<scope>): <subject>  # 50 字符内,祈使句,小写开头,无句号

[body]  # 72 列换行，解释 why 而非 what

[footer]  # Fix #id / BREAKING CHANGE:
```

例:
```
feat(metal): emit slang from StructuredProgram

将 StructuredIr 经 SlangGenerator 输出 HLSL，支撑 slangc -target dxil
-p sm_6_0 离线编译。未接 MSC，metallib 产出由下一提交补齐。

Fix #42
```

`type` 与 `labeler.yml` 映射: `feat->gpu/cpu/gui` 等按路径自动；`docs: -> documentation`，`ci: -> infra`。`renovate.json` 的依赖升级走 `chore(deps):`。

**本地强制**: `commit-msg` 钩子校验，未命中 `^(feat|fix|perf|refactor|docs|chore|ci|test)(\(.+\))?: .+` 拒绝提交。

## 4. Issue 规范

### 远端策略

- **主 Issue 库**: `https://github.com/Ryubing/Issues` (对外部用户)。本仓库 ` .forgejo/issue_template/config.yml` 保留 `contact_links` 外跳，但 **启用本地 Issue** 供开发/ `P1` 内协 ( `blank_issues_enabled: false` 改为分模板)。
- **本地 Issue 仅用于**: `P1` 任务拆解、技术债、CI/ infra。用户 Bug/游戏兼容仍导向 GitHub。

### 模板 ( `.forgejo/issue_template/*.yml` )

| 模板 | 标签 | 必填 |
|---|---|---|
| `bug_report.yml` | `bug` | 复现步骤、环境、日志 |
| `feature_request.yml` | `enhancement` | 动机、方案、验收 |
| `task.yml` | `infra/docs` | Checklist |
| `p1_metal.yml` | `gpu, graphics-backend:metal` | 关联 `docs/p1-metal/04-PHASE-PLAN` 阶段 |

所有 Issue 要求: 标题前缀 `[Bug]/[Feature]/[P1]`，正文含 `关联 PR / 验收标准`。

## 5. PR 规范

### 模板 `.forgejo/pull_request_template.md`

必填段: `关联 Issue` / `变更类型` / `测试` / `Checklist` (含 `dotnet format`/`build`/`evidence`)。详见模板文件。

### 生命周期

```
Draft PR (early CI) -> Ready -> 2 Approvals (CODEOWNERS) -> CI 绿 -> Squash -> Delete branch
```

- **Draft**: 仍在 `WIP` 时用，或标题 `[WIP]`，`pr_triage.yml` 仅在 `ready_for_review` 打标，避免噪音。
- **Rebase**: 冲突由作者 `git fetch origin && git rebase origin/master` 解决，禁止 `merge master` 污染历史。
- **审批**: `reviewers.yml` + `CODEOWNERS` 双重，`gpu` 路径需 `@GreemDev`，`p1-metal` 需金属负责人。
- **合并条件** (与 `docs/workflow/pr-guide.md` 对齐): 2 approvals + `build.yml` (win/linux/macos) 绿 + `labeler` 已同步。
- **小 PR 原则**: <300 行优先，>500 行必须拆。

### 标签

沿用 `.forgejo/labeler.yml` 的 13 类，新增 `graphics-backend:metal` 与 `p1-metal` (见更新后文件)。PR 标题 `type` 与路径标签需一致，`pr_triage.yml` 自动同步，`sync-labels:true` 会清掉手工误标。

## 6. 本地工作流 (必做)

```bash
# 首次 clone 后
git config pull.rebase true
git config push.autoSetupRemote true
git config push.default simple
git config core.hooksPath .githooks   # 或 .git/hooks (本文已落 pre-commit/commit-msg)
git config commit.template .gitmessage

# 日常
git fetch origin --prune
git checkout -b feat/metal-xxx origin/master
# ... commit (钩子自动 format 校验)
git push -u origin feat/metal-xxx
# Forgejo 上 Create PR -> Draft -> Ready -> 等 CI

# 同步主干
git fetch origin
git rebase origin/master
git push --force-with-lease

# 合入后清理
git checkout master && git pull --ff-only
git branch -d feat/metal-xxx
git push origin --delete feat/metal-xxx
```

别名 (`git config --global` 或本仓库 `.gitconfig` 建议):
```
alias.lg = log --oneline --graph --all -12
alias.sync = "!git fetch origin --prune && git rebase origin/master"
alias.pr = "!gh pr create --fill"  # 或 tea pr create (Forgejo)
```

## 7. 远端分支保护 (Forgejo Settings -> Branches)

对 `master` 启用:

- [x] `Protect this branch`
- [x] `Require pull request reviews before merging` (2 approvals)
- [x] `Require status checks to pass` -> `build (win-x64) / build (macOS Universal)` (`build.yml`)
- [x] `Require branches to be up to date before merging`
- [x] `Restrict pushes that create files` (禁止直接 push)
- [x] `Allow squash merging` / 禁 `merge commit` (除维护者特批)
- [ ] `Allow force pushes` = OFF

`canary.yml` 与 `release.yml` 仅 `workflow_dispatch` / `push master` 触发，保护后仍可由 CI 以 `GITHUB_TOKEN` 推送 tag。

## 8. CI 门禁 ( `.forgejo/workflows/` )

| 工作流 | 触发 | 门禁作用 |
|---|---|---|
| `build.yml` | `pull_request: [master]` | PR 必须绿，含 `dotnet build+test+publish` 三平台 |
| `pr_triage.yml` | `pull_request_target: opened/ready` | 自动 `labeler.yml` 打标 |
| `canary.yml` | `push: master` | 合入即 Canary 发布 (受保护分支间接保护) |
| `release.yml` | `workflow_dispatch` | 手动 Stable 发布 |

新增建议 `verify.yml` (可选): `dotnet format --verify-no-changes` 独立 job，快于全量 build，失败即阻断。

## 9. `P1 Metal` 专栏衔接

- 分支: `feat/p1-metal-*` 统一前缀，便于 `labeler` 命中 `graphics-backend:metal`。
- Issue: 用 `p1_metal.yml` 模板，标题 `[P1-P1.2]` 对应 `docs/p1-metal/04-PHASE-PLAN`，关联 `evidence/p1-*`。
- PR 需附 `evidence/` 产物 ( `toolchain.log` / `metallib` / `Instruments trace` )，否则 `Checklist` 不勾选不得合入。
- 文档先行: `docs/p1-metal/00-MASTER` 更新与代码同 PR。

## 10. 迁移步骤 (已执行)

1. 本仓库落 `pull_request_template + issue_template` + `CODEOWNERS` + `labeler.yml` 增量 + `commit-msg/pre-commit` 钩子 + `.gitmessage`。
2. 远端在 Forgejo UI 手动启用 `master` 保护 (见 §7)。
3. 团队执行 `git config pull.rebase true && git config commit.template .gitmessage`。

## 11. 验证

```bash
git status                          # clean
git log --oneline -3                # 形如 feat(metal): ...
forgejo-cli pr list --state open    # 或 Web UI Actions 绿
cat .git/hooks/commit-msg | head    # 钩子生效
```
