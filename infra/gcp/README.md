# Deploying on Google Cloud

A Compute Engine VM running Ubuntu is an ordinary Ubuntu machine, so
[`infra/ubuntu/`](../ubuntu/) does the actual installing and nothing here
duplicates it. These two scripts only create the cloud resources around it and
carry the source tree over.

**Compute Engine, not Cloud Run or GKE.** Those want container images, which this
platform deliberately no longer builds, and it is stateful anyway: PostgreSQL
data and uploaded installer bytes live on the machine's disk.

## One time: provision

Run this in **Cloud Shell** (the `>_` icon in the console) — `gcloud` is already
installed and already authenticated as you there, so nothing needs a credential
locally.

```bash
git clone <this repository> && cd endpoint-platform
bash infra/gcp/provision-vm.sh --project <project-id>
```

It creates, all idempotently:

| Resource | Why |
|---|---|
| a **static external IP** | an ephemeral IP changes on stop/start, which breaks both the DNS record and the certificate issued for it |
| firewall: **80, 443** from anywhere | GCP's firewall sits in front of the machine, so `ufw` alone would not open these |
| firewall: **22 from `35.235.240.0/20`** | Google's Identity-Aware Proxy range, so SSH arrives under your IAM identity and port 22 is never open to the internet |
| a **VM**: Ubuntu 24.04, `e2-small`, 30 GB | |

Defaults are `--zone us-central1-a --machine-type e2-small --disk-size 30`.

**Not `e2-micro`.** The always-free tier gives it 1 GB of RAM, which cannot
complete `dotnet publish`; the kit adds swap, but the build crawls.

## Then: DNS

The script prints the static IP. Create an A record for it:

```
epp.example.com.   A   <the IP>
```

Let's Encrypt resolves that name over the public internet during the HTTP-01
challenge, so it has to be real and propagated **before** the install step.
Check with `dig +short epp.example.com`.

## Then: install

From the repository root, on any machine with `gcloud` (Cloud Shell included):

```bash
bash infra/gcp/deploy-to-vm.sh --host epp.example.com --email ops@example.com \
     --admin-email admin@example.com
```

That packages the working tree, copies it over IAP, and runs
`infra/ubuntu/install.sh` on the VM. The first run takes 10–20 minutes on an
`e2-small`.

Redeploy after a code change — skips apt and certbot, about three minutes:

```bash
bash infra/gcp/deploy-to-vm.sh --host epp.example.com --skip-host-prep --skip-cert
```

## Driving it from Windows PowerShell instead

`infra/ubuntu/Deploy-Ubuntu.ps1` opens a plain SSH session, which the IAP-only
firewall rule above deliberately prevents. To use it, provision with your own
public address allowed:

```bash
bash infra/gcp/provision-vm.sh --project <id> --allow-ssh-from 203.0.113.10/32
```

That also turns **OS Login off** on the instance, because OS Login ignores the
metadata SSH keys a key-file login relies on.

Prefer IAP where you can: a home or office IP changes, and each change is another
firewall edit. `deploy-to-vm.sh` does the same job over IAP with no key to manage.

## Operating

```bash
gcloud compute ssh endpoint-platform --zone us-central1-a --tunnel-through-iap
journalctl -u 'endpoint-platform-*' -f
```

Everything else — rollback, rotation, backups — is in
[`infra/ubuntu/README.md`](../ubuntu/README.md). Nothing about it is
GCP-specific.

## Cost, and turning it off

Roughly **USD 15–20/month** at list price for an `e2-small`, its disk and the
static IP. None of it is free-tier.

`provision-vm.sh` prints the exact delete commands when it finishes. Deleting the
instance destroys its disk, **and with it the database** — the audit trail, the
enrolled devices and every uploaded package. Take a dump first if the data
matters.

Stopping the instance rather than deleting it still bills the disk and, because
the IP here is reserved, the address as well.
