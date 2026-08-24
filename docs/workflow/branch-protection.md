# Forgejo 分支保护 - 手动步骤 (管理员在 Web UI 执行一次)

> 远端 https://git.ryujinx.app/projects/Ryubing  -> Settings -> Branches -> Add branch protection

分支: master

- [x] Enable branch protection
- [x] Require pull request reviews before merging: 2 approvals
  - Dismiss stale approvals when new commits are pushed: ON
  - Require CODEOWNERS review: ON (.forgejo/CODEOWNERS)
- [x] Require status checks to pass before merging
  - build (win-x64 Release)     <- 来自 .forgejo/workflows/build.yml
  - build (linux-x64 Release)   <- 同上
  - build (macOS Universal Release)
  - verify / format + yaml lint <- 来自 verify.yml
  - Require branches to be up to date: ON
- [x] Do not allow bypassing the above settings
- [x] Restrict pushes to master: 禁止直接 push (pre-push 钩子本地二次拦截)
- 合并策略: 仅允许 Squash merging (Forgejo -> Settings -> Pull Requests)

# API 方式 (若有 token，可一键执行，需 Forgejo 1.21+)
# curl -X POST "https://git.ryujinx.app/api/v1/repos/projects/Ryubing/branch_protections" \
#   -H "Authorization: token $TOKEN" -H "Content-Type: application/json" \
#   -d @- <<'JSON'
# {
#   "branch_name": "master",
#   "enable_push": false,
#   "enable_status_check": true,
#   "status_check_contexts": ["build (win-x64 Release)", "verify / format + yaml lint"],
#   "required_approvals": 2,
#   "enable_approvals_whitelist": true,
#   "approvals_whitelist_username": "buleli"
# }
# JSON

验证: 尝试 git push origin master 应被远端拒绝；PR 需 2 approvals + CI 绿才可合并。
