# Githooks

仓库级钩子，已随 `docs/workflow/git-workflow.md §6` 落盘。

新 clone 后执行一次:

```bash
git config core.hooksPath .githooks
# 或
git config core.hooksPath .git/hooks  # 已自动指向 .git/hooks，本目录为备份
```

钩子:
- commit-msg: Conventional Commits 校验
- pre-commit: dotnet format 校验
- pre-push: 禁止直接 push master
