# Process notes: phone-first build, manage, deploy

A running log of friction in the phone-only workflow (Claude Code on the web, GitHub mobile, Railway dashboard). It is meant to be appended to every session, and the fixes should come out of it rather than out of memory.

Each entry says what happened, what it cost, and the fix. The fix is tagged by where it lives:
- [env]: Claude Code environment settings (cloud environment menu in the session title bar → Edit)
- [prompt]: how the task gets written
- [repo]: files in this repo
- [gh]: GitHub settings
- [rw]: Railway settings

Status is open, done, or won't-fix.

---

## Phase 1: Initiation (session 1, 2026-09-24)

1. The spec's platform facts were stale. The prompt said to use `railway.json` and to avoid IaC because it was "experimental". Railway's docs now say the reverse: Config as Code is deprecated, new services can't opt in, it has a hard cutoff on 2026-12-01, and TypeScript IaC is GA. It cost one blocking question, which was cheap because the doc check was already in the prompt.
   - Fix [prompt]: state the intent ("config reviewable in the repo, least moving parts") instead of the mechanism, and keep the "read current docs first" line. That line caught this one. **open**
2. Branch name mismatch. The prompt said `main`, but the repo default is `master`. The PR targets `master`, and the docs were patched mid-session.
   - Fix [gh]: rename the default branch to `main` (GitHub → repo → Settings → Default branch; this works on mobile web, not in the app). Otherwise, say `master` in prompts. **open**
2b. Existing infrastructure wasn't mentioned. The Railway service had already been created and wired to auto-deploy from `master`, but the prompt didn't say so. The session wrote "create a new service" instructions, which were wasted steps.
   - Fix [prompt]: add one line on what already exists ("Railway service X exists, deploys from master, no variables set yet"). **open**
3. The one-question-at-a-time rule worked. A single multiple-choice question with a recommended option can be answered on a phone in about 5 seconds. Keep it. **done**

## Phase 2: Build and verify in the sandbox

4. The network policy blocked the hosts that matter. `docs.railway.com`, `backboard.railway.com` (the Railway API/CLI), `youtube.com`, and `builds.dotnet.microsoft.com` all returned proxy 403s. The Railway docs were read from their GitHub source instead, which worked but was a detour. A real download test and any CLI deploy were impossible.
   - Fix [env]: Network access → add `docs.railway.com`, `backboard.railway.com`, `railway.com`, and (for end-to-end tests) `www.youtube.com`, `youtube.com`, `*.googlevideo.com`. Or use a broader access level for this environment. **open**
5. No .NET SDK in the container. The build ran inside the `dotnet/sdk:10.0` Docker image, and `dockerd` had to be started by hand first. That was a few minutes of setup, repeated every new session.
   - Fix [env]: add a setup script that installs the .NET 10 SDK (the `dotnet-install.sh` host must be allowed) or starts `dockerd`. Alternative [repo]: a SessionStart hook that does the same. **open**
6. Docker builds need the sandbox proxy CA. This was solved with a throwaway `Dockerfile.sandbox` that is not committed. It is fine as-is, but it gets rebuilt from scratch each session.
   - Fix [repo]: a small `scripts/sandbox-docker-build.sh` that generates it. That is low value until builds happen more often. **open, low**
7. A test-runner surprise. xunit.v3 on the .NET 10 SDK needs `global.json` → `"test": {"runner": "Microsoft.Testing.Platform"}`. It cost one failed test run.
   - Fix: already in the repo. Note it for future .NET projects. **done**

## Phase 3: PR and review on the phone

8. The PR body is long for a phone screen. Everything needed is there, but the "what do I have to do" part sits below the fold.
   - Fix [prompt]/[repo]: put a 3-line "Action needed" block at the top of every PR body, with the details below it. **open**
9. There is no CI on the repo, so tests only run in the Claude session. GitHub mobile shows no green check to trust before merging.
   - Fix [repo]: a GitHub Actions workflow that runs `dotnet test` plus `docker build` on each PR. It is cheap, and it gives a merge signal visible from the phone. **open, high value**

## Phase 4: Deploy (not reached yet)

10. There is no Railway token in the environment, so the session could not preview or run any Railway command. Every Railway step is manual in the dashboard.
    - Fix [env]: store a project-scoped token as the environment variable `RAILWAY_TOKEN` (per Railway's CLI docs: `RAILWAY_TOKEN` is project-scoped, `RAILWAY_API_TOKEN` is account-scoped). Use the narrower one. Also needs item 4's hosts. Never paste it into chat. **open**
11. Dashboard-only config is not reviewable. The healthcheck path, restart policy, and variables live in Railway's UI, not in a diff.
    - Fix [repo]/[rw]: revisit IaC (`.railway/railway.ts` plus the `railwayapp/config` GitHub Action, which plans on PR and applies on merge) if a second service shows up or a config change ever surprises you. **won't-fix for now**
12. No post-deploy check exists. The Railway healthcheck only runs at deploy time and never touches YouTube.
    - Fix [repo]: a manual-dispatch GitHub Action (runnable from GitHub mobile) that hits the deployed `/api/convert` with a known short video and reports pass or fail. It needs the app URL and password as repo secrets. **open**

---

## Top 3 to do next (highest payoff per minute on a phone)
1. Item 4 and item 10: allow the Railway and YouTube hosts, and add `RAILWAY_TOKEN`. This unblocks deploy and verification from the session itself.
2. Item 9: a CI workflow gives a green check in GitHub mobile before merging.
3. Item 2: rename `master` to `main`, or stop saying `main`.

## How to add to this file
Append new entries under the phase they belong to, keep the numbering running, and update the status in place. Don't rewrite old entries; mark them done or won't-fix.
