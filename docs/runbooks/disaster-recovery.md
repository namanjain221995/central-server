# Disaster recovery: rebuild the server on a new machine

If the server's hardware dies, a new Ubuntu machine can take over with every
device, every escrowed BitLocker recovery key, every administrator and every
uploaded package intact. Enrolled agents are **not** reinstalled: they keep
their credentials and reconnect on their own once the new machine answers on
the same name.

Two scripts in `deploy/docker/` do the work:

| Script | Runs on | Does |
|---|---|---|
| `backup.sh` | the live server, nightly | one encrypted bundle: database, `.env`, certificates, DNS credentials, uploaded packages |
| `restore.sh` | the new machine, once | decrypts a bundle, restores everything, builds and starts the stack, verifies row counts |
| `offsite-upload.sh` | the live server, after each backup | uploads the bundle to Google Drive, verifies it, keeps the newest 30 |
| `Pull-ServerBackup.ps1` | an admin PC, daily | copies new bundles off the server and checks their checksums |

## Why the bundle is encrypted, and what that means for you

The database alone restores nothing useful: escrowed recovery passwords are
sealed with keys that exist only in `.env`. The database and `.env` together
open every BitLocker volume in the fleet. So they travel together, and only
encrypted (GPG, AES-256, passphrase).

**The passphrase is the one thing the scripts cannot back up for you.** It
lives on the server in `/etc/endpoint-platform/backup.passphrase` and dies with
it. Store a copy in a password manager the day you set it up. A bundle without
its passphrase is unrecoverable, by design.

Never put the bundles in a Git repository, and never store the passphrase next
to them.

## One-time setup on the live server

```bash
cd /opt/endpoint-platform/src/deploy/docker
sudo ./backup.sh --init           # creates the passphrase, prints how to view it
sudo cat /etc/endpoint-platform/backup.passphrase; echo   # copy into the password manager
sudo ./backup.sh                  # first backup, now
sudo ./backup.sh --install-timer  # nightly at 02:30, catches up after downtime
```

Bundles land in `/var/backups/endpoint-platform/bundles/`; the newest 14 are
kept. Each run decrypts its own bundle again before reporting success.

Check the timer at any time:

```bash
systemctl list-timers endpoint-platform-backup.timer
journalctl -u endpoint-platform-backup -n 30 --no-pager
```

## Keep copies off the server

### Automatically, to Google Drive (recommended)

```bash
sudo ./offsite-upload.sh --setup   # once: connect the Drive account (interactive)
sudo ./offsite-upload.sh           # upload the newest bundle now
```

From then on `backup.sh` uploads every new bundle, verifies the uploaded copy
by hash, and keeps the newest 30 in the Drive folder. If the upload fails, the
nightly run is marked failed in `journalctl -u endpoint-platform-backup`, while
the local bundle is kept.

During `--setup`, choose the **`drive.file`** scope. rclone can then see only
the files it uploaded, so the token on the server cannot read anything else in
that Google account. The server has no browser, so the sign-in step runs on any
PC with rclone (`winget install Rclone.Rclone`) and the token is pasted back.

Use a dedicated account for backups, protect it with 2-step verification, and
never put the backup passphrase in the same Drive. If the account belongs to a
Google Workspace that restricts third-party apps, a Workspace admin has to allow
rclone first.

To restore from Drive, download the newest `epp-backup-*.tar.gpg` and its
`.sha256` from the Drive folder in a browser, and continue with
[Restoring onto a new machine](#restoring-onto-a-new-machine).

### Pulled to an administrator's PC

From an administrator's PC (a daily scheduled task is enough):

```powershell
.\deploy\docker\Pull-ServerBackup.ps1 -SshHost <server> -SshUser <user> `
    -KeyPath "$HOME\.ssh\<key>" -Destination D:\EppBackups
```

It copies only new bundles, verifies each checksum, keeps the newest 30 and
warns when the newest is more than 36 hours old. An external disk or a second
site is better still.

## Restoring onto a new machine

1. Install Ubuntu Server (24.04 or later) on the new machine.
2. Give it the **old server's LAN address** if at all possible (router DHCP
   reservation, or netplan). Agents and DNS then need no change.
3. Make sure the old machine is off or disconnected. Two servers answering for
   the same name split the fleet.
4. Clone the repository and copy in the newest bundle and its `.sha256`:

   ```bash
   sudo apt-get update && sudo apt-get install -y git
   sudo git clone https://github.com/<owner>/<repo>.git /opt/endpoint-platform/src
   cd /opt/endpoint-platform/src/deploy/docker
   # scp or USB: epp-backup-<stamp>.tar.gpg and epp-backup-<stamp>.tar.gpg.sha256
   sudo ./restore.sh epp-backup-<stamp>.tar.gpg
   ```

5. Enter the backup passphrase when asked.

`restore.sh` then, in order:

1. installs Docker, the compose and buildx plugins, GnuPG and curl if missing;
2. refuses to continue if the machine already has a deployment, unless you pass
   `--force` (which replaces it);
3. decrypts the bundle and checks every part against the checksums recorded at
   backup time;
4. refuses if this checkout is older than the code that wrote the backup (run
   `git pull`);
5. restores `.env`, `tls/`, `letsencrypt/` and `cloudflare.ini`;
6. creates the database roles from `.env` and restores the dump;
7. restores the uploaded packages;
8. runs `deploy.sh`, which builds the images, applies any newer migrations and
   waits for both APIs to report healthy;
9. compares device, administrator, recovery-key and agent-release counts with the
   backup's manifest, and fails loudly if any differ.

It ends by telling you what is still needed for agents to reach it.

## If the new machine has a different LAN address

The public name's DNS record points at the old address. Either change the
machine's address to the old one, or:

```bash
cd /opt/endpoint-platform/src/deploy/docker
sudo sed -i 's#^LAN_ORIGIN=.*#LAN_ORIGIN=https://<new-lan-ip>#' .env
sudo ./issue-certificate.sh    # repoints the DNS record (DNS only, never proxied)
sudo ./deploy.sh
```

Also move any router port-forward for 443 to the new machine.

## What happens to the agents

Nothing, if the name and certificate are the same. Each agent's credential is
already in the restored database, so heartbeats resume within a few minutes.
Watch **Devices > Last seen**.

Agents that cannot reach the server for a long time keep retrying with backoff;
they do not need to be touched.

## Rehearse it

A backup that has never been restored is a hope, not a backup. Every few months,
restore the newest bundle onto a spare machine or VM that is **not on the
production network**, and check that the dashboard shows the devices and that a
recovery key can be revealed. Then destroy the copy: it holds every recovery key.
