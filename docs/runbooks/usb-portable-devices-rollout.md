# USB portable devices (agent 1.16.0): rollout, rollback and recovery

The release that restricts phones, tablets and cameras (MTP/PTP) — commit
`fcd54da`, migration `20261009153245_UsbPortableDevices`, agent 1.16.0 — and how
to take it back out. Read section 2 before deploying: **rolling the server back
needs one database update as well as the previous images**, and that was found by
rehearsing it, not by reading the code.

## Readiness, 2026-10-09

| Safeguard | Status | Evidence |
|---|---|---|
| Server rollback | **VERIFIED** on a local rehearsal of the real compose stack; **BLOCKED** on the production host | 1.15.0 deployed → 1.16.0 deployed → previous images retagged and started without building → health, migration job, routing and agent reports checked at each step, then rolled forward again. The production host was not accessed: whether it holds previous images is unknown. |
| Migration compatibility | **VERIFIED**, with a mandatory compensating update | The migration adds one nullable column and nothing else. The 1.15.0 migrator reports "already up to date" on the 1.16.0 schema; the full 1.15.0 Agent API and Admin API suites pass on it. The 1.15.0 server **cannot read rows the 1.16.0 server marked `PortableDevice`** (500s) until section 2's update runs; after it, it can. |
| Agent rollback | **CODE-REVIEWED**; Windows install paths **BLOCKED** | Package tables read from the built MSI. The 1.15.0 policy manager's handling of a 1.16.0 release list run as a test. No install, uninstall or downgrade was executed: no test endpoint exists, and the only Windows machine available runs the production agent. |
| USB recovery on an endpoint | **CODE-REVIEWED** and unit-tested; on hardware **NOT VERIFIED** | `UsbRecoveryTests`, `UsbAgentLifecycleTests`. That Windows actually re-enables a released phone — including one released while unplugged — is for the physical acceptance in [usb-control.md](../usb-control.md). |

| Staging deployment | **BLOCKED** (2026-10-10) | No staging environment is defined anywhere in this repository or on the workstation, and none was provided. Nothing was deployed to any server. |

Physical MTP/PTP enforcement itself remains **NOT VERIFIED** (see usb-control.md).

## 1. Deploying the server

Deploy the server before any agent: an older server stores `PortableDevice` as
`Unknown`, which is visible but not grantable. On the host, as in
[deploy/docker/README.md](../../deploy/docker/README.md):

```bash
cd /opt/endpoint-platform/src/deploy/docker

# 1. What is running now, to come back to.
git -C ../.. rev-parse --short HEAD                       # the previous commit
docker compose images                                      # the image ids in use

# 2. A verified backup before a deploy that carries a migration. backup.sh dumps with a
#    freshly pulled client (pg_dump inside the running container has segfaulted on this
#    host before), bundles the .env the dump is useless without, and reads it back.
#    Once per machine first: sudo ./backup.sh --init
sudo ./backup.sh

# 3. Keep the running images. deploy.sh rebuilds the :local tag in place, so without
#    this the previous images survive only as untagged layers until the next prune.
for image in admin-api agent-api migrations web; do
    docker tag endpoint-platform/$image:local endpoint-platform/$image:previous
    docker image inspect -f "$image {{.Id}}" endpoint-platform/$image:previous
done

# 4. Deploy the reviewed commit (it must be on the remote for this host to fetch it).
cd ../.. && git fetch && git checkout <release commit> && cd deploy/docker
sudo ./deploy.sh
```

From a workstation instead, `deploy/docker/remote-deploy.sh <user>@<host>` ships
the working tree it is run from — tracked files **and untracked files git does not
ignore** — and then runs `deploy.sh` there. Run it from a clean worktree of the
release commit, never from a checkout with local work in it:

```bash
git worktree add --detach ../release-fcd54da fcd54daf77adf991bb483c5cc6280e3b5fb8cb50
cd ../release-fcd54da && git status --short          # must print nothing
bash deploy/docker/remote-deploy.sh <user>@<host> https://<origin>
```

Steps 1 to 3 above still run on the host first, over SSH.

`deploy.sh` exits non-zero unless the migration job succeeded, both APIs report
healthy, and the dashboard, both APIs and pgAdmin answer on the public origin.
Then confirm the migration and the agents:

```bash
docker compose logs migrations | grep -E "Applying migration|up to date|completed"
docker compose exec -T postgres psql -U postgres -d endpoint_platform -c \
  "SELECT \"MigrationId\" FROM endpoint_platform.__ef_migrations_history ORDER BY 1 DESC LIMIT 1;"
docker compose logs --since 10m agent-api | grep -c "responded 500"      # expect 0
```

Agents on 1.15.0 and older keep reporting normally; their USB rows show
*Applied, not verified* instead of *Enforced* until they update.

## 2. Rolling the server back

**Do not** run the migration's `Down`, drop the column, or restore the dump to undo
the release: the column is harmless to the previous server, and a restore loses
everything written since the deploy. Restore the dump only for data damage.

```bash
cd /opt/endpoint-platform/src && git checkout <previous commit> && cd deploy/docker

# Previous images, if step 3 of section 1 was done:
for image in admin-api agent-api migrations web; do
    docker tag endpoint-platform/$image:previous endpoint-platform/$image:local
done
sudo ./deploy.sh '' --no-build
# Without :previous images, rebuild the previous commit instead (slower, needs the network):
#   sudo ./deploy.sh

# MANDATORY once the previous server is up: relabel phone rows it cannot read.
docker compose exec -T postgres psql -U postgres -d endpoint_platform -v ON_ERROR_STOP=1 -c \
  "UPDATE endpoint_platform.usb_devices SET device_class = 'Unknown' WHERE device_class = 'PortableDevice';"
```

Why the update is needed: `device_class` is stored as the enum's name, and EF Core
refuses to load a name the model does not have. Until the update runs, the previous
server answers **500** to the USB report of every endpoint that has reported a phone,
to that endpoint's USB panel, and to a **revoke** of a phone grant — so a read/write
grant on a phone could not be revoked and would stay live until it expired (up to 24
hours). Run the update *after* the previous server is up, not before: the 1.16.0
server would relabel the rows on the next report. It is idempotent, changes only that
one column, and leaves `enforcement_status` in place.

Verify:

```bash
docker compose ps                                          # all healthy; migrations exited 0
docker compose images                                      # the ids recorded in section 1
docker compose logs migrations | grep "already up to date"
docker compose logs --since 10m agent-api admin-api | grep -c "Cannot convert string value"   # expect 0
```

After a rollback: phones show as *Unknown*, not grantable; 1.16.0 agents keep
restricting them from local state; a live phone grant is still published until it is
revoked or expires, and can be revoked. Rolling forward again needs nothing extra:
the next report from a 1.16.0 agent restores each phone's class.

## Staging verification

`scripts/usb-portable-release-check.sh` runs on the staging host, in
`deploy/docker`, and checks sections 1 and 2 with evidence: image ids running and
kept, the migration job and history, the new column, `/health/ready` with
PostgreSQL and Redis, routing through the web origin, the dashboard and its bundle,
a simulated agent sending 1.15.0-shaped and 1.16.0-shaped USB reports, and the whole
rollback — previous images, the expected 500, the compensating update, forward
again. Anything that writes first checks the database is staging-sized and refuses
otherwise. It was validated on 2026-10-10 against a loopback-only local stack built
from the same two commits; it has not yet run on a staging host.

The staging host is a separate machine with a fresh database — never a copy of
production's, which holds escrowed BitLocker keys. If it has never run the platform,
deploy the previous release (`fc06661`) first, the same way, so there is something
to roll back to.

```bash
# From the workstation. The script is not in the release commit: copy it from the checkout.
scp scripts/usb-portable-release-check.sh <user>@<staging>:/tmp/
run() { ssh -t <user>@<staging> "cd /opt/endpoint-platform/src/deploy/docker && bash /tmp/usb-portable-release-check.sh $*"; }

run identify                       # host, origin, image ids, device count
run keep previous                  # before deploying
# deploy from a clean worktree of the release commit (section 1), then:
run verify release
run agent enroll && run agent 115 && run agent 116 && run agent rows
run rollback-test                  # ends back on the release images
```

## 3. Rolling the agent back

Windows Installer refuses the downgrade: the package carries a launch condition
`NOT WIX_DOWNGRADE_DETECTED` ("A newer version of the Endpoint Platform Agent is
already installed."), and the agent's own updater refuses a release that is not
strictly newer. So:

- **Fleet:** revert the change on `main`, set `VersionPrefix` to a higher version
  (for example 1.16.1), build it with the release workflow, publish it and queue the
  update. The upgrade removes 1.16.0 first; removing it stops its service, and that
  stop releases every device it restricted. The reverted agent classifies phones as
  *Other* and leaves them alone.
- **One test machine:** uninstall 1.16.0 from *Settings → Apps*, then install the
  1.15.0 MSI. The state directory, credential and enrolment are kept; no
  re-enrolment.

Either way, check afterwards that nothing is left disabled (section 4). If the 1.16.0
service was **not running** when it was removed — crashed, or stopped by hand and
killed — no release ran: the phone stays disabled under the older agent, which keeps
it on its release list but does not touch it, until that agent's own service stops.
`Restart-Service EndpointPlatformAgent` releases it.

A successful MSI install cannot be undone by Windows Installer's transactional
rollback; that covers only a failure during the install itself.

## 4. Recovering a USB device on an endpoint

All commands in an elevated PowerShell on the endpoint. In order of preference:

| Situation | What happens | Way back |
|---|---|---|
| Agent running, phone restricted by policy | Working as designed | Grant read/write in the console; the agent enables it within seconds |
| Server unreachable | The agent keeps enforcing from local state; there is no timeout that opens a device | A cached grant still works until it lapses; otherwise stop the service (below) |
| Service stopped in an orderly way (stop, restart, reboot, upgrade, uninstall) | Every device the agent restricted is released — **in code and unit tests; not yet observed on hardware** | Nothing to do |
| Service crashed or was killed | Devices stay restricted and listed; the next orderly stop releases them | `Restart-Service EndpointPlatformAgent` |
| Product removed while the service was not running | Nothing released: the installer runs no release of its own | `pnputil` below, with the phone plugged in |
| Release refused by Windows | Logged as an error; the device stays listed and every later stop retries | `pnputil` below |
| Windows deferred a disable to a restart | The console shows *Restart required*; the device stays usable until the reboot | Stopping the service first releases it, cancelling the pending disable |

```powershell
# What the agent believes it has disabled (administrators only).
Get-Content "$env:ProgramData\EndpointPlatformAgent\usb-restricted-devices.json"

# What Windows reports as disabled (problem code 22).
pnputil /enum-devices /problem 22

# Stand enforcement down: releases every device the agent restricted, and leaves
# EVERY USB storage device and phone on this machine unrestricted until it starts again.
Stop-Service EndpointPlatformAgent

# One device, by instance id. The device must be plugged in.
pnputil /enable-device "<instance id>"
```

`pnputil /enable-device` needs an administrator, changes one device, and is undone
by a running agent whose policy still says *Restricted* — within seconds, because the
re-enabled device raises an arrival the agent reacts to, and at the latest on its
one-minute reconcile. Grant access or stop the service first. Device Manager → the device →
*Enable device* is the same operation. An unplugged phone's release goes through
Windows' class installer for a device that is not present (verified read-only:
Windows still opens its record); whether that clears the disable has not been
observed, so if a phone comes back disabled after the agent is gone, plug it in and
use `pnputil`.

## 5. Before the next release that adds an enum value

The compensating update exists because the server reads string-stored enums
strictly. `UsbPortableDevicesMigrationTests.A_class_name_the_model_does_not_have_cannot_be_read_back`
pins that. Making the read tolerant — unknown names to `Unknown` — in one release
would make rolling back *the release after it* need no data step at all. The same
applies to every column stored with `HasConversion<string>()`.
