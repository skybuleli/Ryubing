<!-- 标题格式: <type>(<scope>): <subject> 例: feat(metal): emit slang from StructuredProgram -->
## 关联 Issue

Closes # <!-- 或 Related # -->

## 变更类型

- [ ] feat / fix / perf / refactor / docs / chore / ci / test
- [ ] 是否关联 P1 Metal: `docs/p1-metal/` 阶段 `P1-__`

## 变更内容

<!-- 1-3 句说明 why，what 在 diff 中可见则不赘述 -->

## 测试

- [ ] `dotnet build -c Release` 通过
- [ ] `dotnet test --no-build -c Release` 通过 (若改动涉逻辑)
- [ ] `dotnet format --verify-no-changes` 通过
- [ ] 手测 / Instruments / 截图 / `evidence/` 已附 (P1 必须)

## Checklist

- [ ] 分支命名 `type/scope-slug[#issue]` (如 `feat/metal-slang-gen#42`)
- [ ] 提交符合 Conventional Commits (`feat(metal): ...` / `Fix #42`)
- [ ] 未混入无关改动，PR <500 行或已拆分
- [ ] 已 `git fetch origin && git rebase origin/master`
- [ ] `labeler.yml` 标签已自动同步，无需手工改
- [ ] 需要 2 approvals，已在 `CODEOWNERS` 指派

## 截图 / Evidence

<!-- 粘贴 Forgejo Actions 链接或 evidence/p1-*/ 路径 -->
