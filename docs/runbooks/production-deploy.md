# Production deployment runbook

Start-to-finish instructions for putting the Endpoint Management Platform onto
the Ubuntu machine and making it reachable from outside the office.

This is the **specific** runbook for this deployment. For how the kit works in
general see [`infra/ubuntu/README.md`](../../infra/ubuntu/README.md); for the
architecture see [`docs/architecture.md`](../architecture.md).

You do not need to know C# or React. You do need `sudo` on the Ubuntu machine
and access to the Cloudflare dashboard and the office router.

---

## 1. What you are building

```
                                 Ubuntu machine (192.168.8.96)
  browser  ──┐
             ├── 443 ──▶ router ──▶ nginx ──┬──▶ /          dashboard (static files)
  agent    ──┘          port-forward        ├──▶ /api/   ──▶ Admin API  127.0.0.1:5080
  (home laptop)                             └──▶ /agent/ ──▶ Agent API  127.0.0.1:5081
                                                                 │
                                                PostgreSQL 17 ◀──┤  loopback only
                                                Redis         ◀──┘  loopback only
```

Three .NET processes under systemd, behind nginx. **No containers anywhere.**

Everything is served from **one hostname**. That is a hard requirement, not a
preference: the session cookie uses the `__Host-` prefix, so a browser will
discard it if the dashboard and the API are on different hosts, and nobody will
be able to sign in.

| Thing | Value |
|---|---|
| Public hostname | `epp.techsarasolutions.com` |
| Machine | `192.168.8.96`, user `paras-thind` |
| TLS | Let's Encrypt via Cloudflare DNS-01 |
| Ports published | **443 only** |

---

## 2. Before you start

You need all four of these. The install will fail without them.

### 2.1 A Cloudflare API token

In the Cloudflare dashboard: profile menu → **API Tokens** → **Create Token** →
**Edit zone DNS** template.

- **Permissions:** `Zone` → `DNS` → **Edit**, plus a second row
  `Zone` → `Zone` → **Read**. Both are needed — Read is how the script finds the
  zone ID.
- **Zone Resources:** `Include` → `Specific zone` → `techsarasolutions.com`
- Copy the token when it is shown. Cloudflare will not show it again.

**Do not use the Global API Key.** It can do anything to the whole account; this
token can only edit one zone's DNS.

Put it on the Ubuntu machine:

```bash
sudo install -d -m 0750 /etc/endpoint-platform
sudo tee /etc/endpoint-platform/cloudflare.ini >/dev/null <<'INI'
dns_cloudflare_api_token = PASTE_THE_TOKEN_HERE
INI
sudo chmod 600 /etc/endpoint-platform/cloudflare.ini
sudo chown root:root /etc/endpoint-platform/cloudflare.ini
```

### 2.2 A router port-forward

Forward **TCP 443 → 192.168.8.96:443**.

**Forward nothing else.** In particular do not forward 5080 or 5081. Both APIs
bind to loopback and trust `X-Forwarded-For` from nginx; exposing them directly
would let anyone on the internet forge their own client IP and assert that their
request arrived over HTTPS.

### 2.3 The A record

You do not create this by hand — `setup-tls-cloudflare.sh` creates and updates
it. Two things to know:

- It is created **DNS-only (grey cloud)**, deliberately. An orange-cloud
  (proxied) record routes traffic through Cloudflare's edge, which both cannot
  reach a private `192.168.x.x` origin and would decrypt every request —
  including BitLocker recovery keys. If anyone turns the cloud orange later, the
  site stops working and its confidentiality is broken. Leave it grey.
- The office IP is **dynamic**. If it changes, re-run
  `sudo bash infra/ubuntu/setup-tls-cloudflare.sh epp.techsarasolutions.com` to
  re-point the record.

### 2.4 A machine that meets the requirements

Ubuntu 24.04 or 26.04, x86-64, 8 GB RAM or more, 40 GB free disk. The install
script adds swap if there is none.

---

## 3. Get the code onto the machine

```bash
ssh paras-thind@192.168.8.96
git clone git@github.com:namanjain221995/central-server.git
cd central-server
```

If the clone is refused, your SSH key is not on the GitHub account yet — the
repository is private.

---

## 4. Install

One command, from the repository root, as your normal login (**not** as root —
the script calls `sudo` itself where it needs to):

```bash
bash infra/ubuntu/install.sh \
    --host epp.techsarasolutions.com \
    --cloudflare \
    --admin-email <your-address> \
    --generate-admin-password
```

Expect 15–30 minutes, mostly package installation and the first .NET build.

It runs these in order, and each is safe to re-run on its own:

| Step | What it does |
|---|---|
| `host-prep.sh` | PostgreSQL 17, Redis, .NET 10 SDK, Node 24, nginx, certbot, service accounts |
| `gen-env.sh` | Generates the secrets **once**, renders one environment file per service |
| `setup-postgres.sh` | Creates the database and its two roles |
| `setup-redis.sh` | Loopback-only, password-protected Redis |
| `setup-nginx.sh` | The reverse proxy and the Let's Encrypt certificate |
| `deploy.sh` | Builds, installs, migrates, starts, health-checks |
| `bootstrap-admin.sh` | Creates the first Super Administrator |

**Write down the generated admin password.** It is printed exactly once, and the
account is required to change it at first sign-in.

### Why Redis needs to be 6.2 or later

`host-prep.sh` installs Redis from `packages.redis.io`, not from Ubuntu's own
repository. Ubuntu ships 6.0, and the platform uses `GETDEL`, which arrived in
6.2. If you substitute the distribution package, secret redemption fails at
runtime with a confusing error rather than at startup.

---

## 5. Verify

```bash
# All three services running
systemctl status 'endpoint-platform-*' --no-pager

# Health, from the machine
curl -fsS https://epp.techsarasolutions.com/api/health/ready && echo OK

# The certificate is genuinely trusted (no -k anywhere)
echo | openssl s_client -connect epp.techsarasolutions.com:443 \
    -servername epp.techsarasolutions.com 2>/dev/null \
    | openssl x509 -noout -issuer -enddate
```

The issuer must say **Let's Encrypt**. If it says anything about a local CA, the
self-signed fallback ran instead of the Cloudflare path and agents will refuse
to connect — see §7.

Then, from a machine **outside** the office network, open
`https://epp.techsarasolutions.com` and sign in. Testing from inside the office
can succeed even when the port-forward is wrong, so this check has to be done
from outside.

---

## 6. Install an agent

On each managed Windows PC, in an **elevated** prompt:

```
msiexec /i EndpointPlatformAgent-<version>-x64.msi SERVERBASEURL=https://epp.techsarasolutions.com
```

Then approve the device in the dashboard under **Enrollments**. Until it is
approved it receives nothing — that is deliberate, not a fault.

Agents are outbound-only. They poll every 60 seconds with jittered backoff and
tolerate being offline for days, so a laptop that goes home and comes back needs
no intervention.

---

## 7. Troubleshooting

**Nobody can sign in, but the site loads.**
Almost always TLS. The session cookie is `__Host-` + `Secure`, so browsers
discard it over plain HTTP on any origin except `localhost`. Confirm §5 shows a
Let's Encrypt issuer.

**Agents will not connect, browsers are fine.**
A Release build of the agent validates the server certificate against the
machine's trusted roots and has **no override**. A self-signed certificate will
work in a browser once you click through, and will never work for an agent. Fix
the certificate rather than the agents.

**`setup-tls-cloudflare.sh` says no zone was found.**
The token is missing `Zone:Zone:Read`, or it is scoped to the wrong zone. The
script never prints the token; re-create it per §2.1.

**The site was working and stopped.**
Check whether the office public IP changed — re-run
`setup-tls-cloudflare.sh` (§2.3). Also check nobody switched the DNS record to
proxied.

**Logs.**

```bash
journalctl -u 'endpoint-platform-*' -f
sudo nginx -t && sudo tail -n 100 /var/log/nginx/error.log
```

---

## 8. Back this up

| What | Where |
|---|---|
| Every secret for the deployment | `/etc/endpoint-platform/secrets.env` |
| The database | `pg_dump` of `endpoint_platform` |
| Uploaded packages and agent MSIs | `/opt/endpoint-platform/packages` |

`secrets.env` is `root:root 0600` and is **never regenerated** once it exists.
Two keys in it are unrecoverable if lost:

- `RECOVERY_ESCROW_KEY` — losing it makes **every escrowed BitLocker recovery
  password permanently undecryptable**. You will not discover the loss until a
  machine will not boot.
- `MFA_TOTP_KEY` — losing it makes every authenticator enrolment unreadable at
  once, and nobody can complete a sign-in.

Back this file up alongside every database dump, and store it somewhere the
platform itself does not depend on.

---

## 9. Redeploying after a code change

```bash
cd ~/central-server && git pull
bash infra/ubuntu/deploy.sh
```

`deploy.sh` builds, installs to a new release directory, swaps a symlink
atomically, waits for health, and rolls back if the new release does not come
up. It does not touch secrets, the database roles or the certificate.

---

## 10. Known state at time of writing

Be aware of these; none of them block the deployment.

- **Multi-factor authentication is partly built.** The database schema, the TOTP
  engine and the sign-in plumbing are in and tested. The enrolment and
  verification endpoints and the dashboard screens are not finished yet, so MFA
  is **not reachable** and sign-in behaves exactly as before. Nobody is enrolled,
  so nobody is challenged.
- **Password screening is active.** Passwords built from the account's own name
  or e-mail, keyboard walks, repeated units and common stems are refused. If
  someone complains their password is rejected, that is why.
- **A dashboard UI polish pass is outstanding.** Cosmetic only.
- **The `infra/gcp/` kit exists** and provisions an equivalent Compute Engine VM
  if this ever needs to move off the office LAN. It reuses `install.sh`
  unchanged.
