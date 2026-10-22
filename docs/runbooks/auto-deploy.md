# Auto-deploy after merge

`.github/workflows/deploy-production.yml` deploys to the EC2 production host
automatically after a pull request is **approved and merged to `main`**, with a
human approval gate in front of the deploy itself. It runs the same scripts a
manual deploy does — `backup.sh`, then build, then `deploy.sh '' --no-build` — so
there is no second deploy path to keep in sync.

This replaces the "type one command after merge" step, not the review. A change
still reaches production only after an owner approves the PR **and** a reviewer
approves the `production` environment. It does **not** reinstate deploy-on-push of
unreviewed code — the trigger is a merged commit, and the gate is deliberate (see
`production-deploy.md` §8 for why the deploy is gated at all).

---

## Security — read before enabling (this repo is public)

A self-hosted runner on a **public** repository is dangerous if misconfigured: a
malicious fork pull request could otherwise run code on the runner, which sits on
the box that holds BitLocker recovery keys and can act as SYSTEM on every managed
PC. Two things make it safe, and **both are required**:

1. **The deploy workflow never runs from a pull request.** Its only triggers are
   `push` to `main` (which a fork cannot do) and manual `workflow_dispatch` (which
   only a maintainer can start). A fork PR therefore cannot start a deploy.

2. **Require approval for all outside collaborators' workflows.**
   Settings → Actions → General → *Fork pull request workflows from outside
   collaborators* → **Require approval for all outside collaborators** (or stricter).
   This stops a fork PR from running *any* workflow — including one that tries to
   target the self-hosted runner — until a maintainer approves it.

Do not point the self-hosted runner at any other repository, and do not add a
`pull_request` trigger to this workflow.

---

## One-time setup

All on the EC2 host, as the deploy user (the one that owns `deploy/docker/.env`).

### 1. Passwordless sudo for the deploy user

The workflow runs `backup.sh` / `git` / `docker` / `deploy.sh` with `sudo` and
cannot answer a password prompt.

```bash
echo "$USER ALL=(ALL) NOPASSWD:ALL" | sudo tee /etc/sudoers.d/epp-deploy
sudo chmod 440 /etc/sudoers.d/epp-deploy
```

This is a real loosening of the box; it is the same grant a hands-on deploy already
needs. Narrow it to the specific scripts if you prefer.

### 2. Install the self-hosted runner

GitHub → repo **Settings → Actions → Runners → New self-hosted runner → Linux x64**,
then follow the shown commands on the EC2 host. When it asks for labels, add
**`production`** (the `self-hosted` label is automatic). Install it as a service so
it survives reboots:

```bash
# from the actions-runner directory the setup created
sudo ./svc.sh install
sudo ./svc.sh start
```

The runner must be able to run `docker` (add its user to the `docker` group or rely
on the `sudo` above) and reach `/opt/endpoint-platform/src`.

### 3. Create the `production` environment with a required reviewer

GitHub → repo **Settings → Environments → New environment → `production`** →
enable **Required reviewers** and add the people allowed to approve a deploy.

After a merge, the workflow starts and **waits** on this gate; a reviewer clicks
*Approve* and the deploy runs. Remove the reviewer only if you truly want
merge = instant deploy (not recommended for this box).

---

## What a deploy does

1. `backup.sh` — a verified encrypted bundle before anything changes. The job
   fails here (and deploys nothing) if the backup cannot be made.
2. `git fetch` + `git reset --hard origin/main` — the exact merged commit.
3. Build the four images with plain `docker build` (EC2's buildx is too old for
   `docker compose build`).
4. `deploy.sh '' --no-build` — migration job first, then the API swap; it rolls
   back and exits non-zero unless the stack answers on the real URL.
5. `curl /api/health/ready` — a final outside check.

## Rolling back

Rollback stays manual and is unchanged: `deploy.sh` already reverts to the previous
release on a failed health check. For a bad change that passed health checks, revert
the commit on `main` (a new PR) and let the next deploy carry the revert, or run the
manual release swap in `production-deploy.md`. Schema migrations are forward-only —
a destructive one still needs the dump-and-compensate treatment described there.
