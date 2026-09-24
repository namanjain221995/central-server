# Production deployment runbook

Putting the Endpoint Management Platform onto an Ubuntu machine and making it
usable by the people who need it.

The platform deploys as **Docker Compose**. The mechanics live in
[`deploy/docker/README.md`](../../deploy/docker/README.md) — this runbook covers
the decisions and checks around them that a README cannot: which certificate to
use and why it decides whether agents work at all, who creates the first
administrator, and what must be backed up.

You need `sudo` on the Ubuntu machine, access to the DNS provider, and (for
access from outside the office) the router.

---

## Fill these in before you start

This repository is **public**, so the values for a specific deployment are not
written down here. Get them from whoever owns the deployment.

| Thing | Value | Used as |
|---|---|---|
| Public hostname | `________________` | the URL, and `SERVERBASEURL` on every agent |
| Machine address | `________________` | where you SSH to |
| Login user | `________________` | must be able to `sudo` |
| DNS zone | `________________` | the zone the API token is scoped to |

Below, `<hostname>` and `<machine>` mean those values.

---

## 1. What you are building

```
                              Ubuntu machine — Docker Compose
  browser  ──┐
             ├── 443 ──▶ web (nginx) ──┬──▶ /          dashboard
  agent    ──┘                         ├──▶ /api/   ──▶ admin-api
  (laptop, anywhere)                   └──▶ /agent/ ──▶ agent-api
                                                │
                                   postgres ◀───┤   named volume
                                   redis    ◀───┘
```

Everything on **one hostname**. That is a hard requirement: the session cookie
uses the `__Host-` prefix, so a browser discards it if the dashboard and the API
are on different hosts and nobody can sign in.

Publish **443 only**. Never expose the API containers directly — they trust
`X-Forwarded-For` from the proxy in front of them.

---

## 2. Choose the certificate — this decides whether agents work

**This is the most consequential choice in the whole deployment.**

A Release build of the Windows agent validates the server certificate against the
machine's trusted root store and has **no override**. So:

| Certificate | Browser | Agents |
|---|---|---|
| **Let's Encrypt via DNS-01** | works silently | **work immediately, nothing to install** |
| Self-signed / local CA | warning you can click through | **cannot enrol at all** until the CA root is deployed to every managed PC |

Use DNS-01 unless there is no usable domain. It proves control of the **name**
over DNS, so it needs **no inbound internet** — a machine on a private LAN can
hold a publicly trusted certificate.

### Cloudflare DNS-01

Create an API token: dash.cloudflare.com → API Tokens → **Edit zone DNS**
template. It needs `Zone:DNS:Edit` **and** `Zone:Zone:Read`, scoped to the one
zone. **Never a Global API Key** — that can do anything to the whole account.

Put it on the machine:

```bash
sudo install -d -m 0750 /etc/endpoint-platform
sudo tee /etc/endpoint-platform/cloudflare.ini >/dev/null <<'INI'
dns_cloudflare_api_token = PASTE_THE_TOKEN
INI
sudo chmod 600 /etc/endpoint-platform/cloudflare.ini

sudo apt-get install -y certbot python3-certbot-dns-cloudflare
sudo certbot certonly --dns-cloudflare \
    --dns-cloudflare-credentials /etc/endpoint-platform/cloudflare.ini \
    --dns-cloudflare-propagation-seconds 30 \
    -d <hostname> --non-interactive --agree-tos --register-unsafely-without-email
```

Then hand the certificate to the stack and restart the proxy:

```bash
cd deploy/docker
sudo cp /etc/letsencrypt/live/<hostname>/fullchain.pem tls/server.crt
sudo cp /etc/letsencrypt/live/<hostname>/privkey.pem   tls/server.key
docker compose restart web
```

**The DNS record must be DNS-only (grey cloud).** A proxied record routes traffic
through Cloudflare's edge, which terminates TLS — revealed BitLocker recovery
keys, admin session tokens and agent credentials would all cross in cleartext at
a third party. It also cannot reach a private origin.

Renewal is certbot's own systemd timer. The copy above is not automatic; add a
`--deploy-hook` that repeats it, or repeat it at renewal time.

---

## 3. Deploy

```bash
cd deploy/docker
sudo ./deploy.sh https://<hostname>
```

First run generates the secrets, builds the images and starts everything in
dependency order. It **proves it worked** — non-zero exit unless the dashboard,
both APIs and pgAdmin all answer on the real URL. Re-running it is the normal way
to deploy a change; it keeps the secrets, the certificate and the database.

---

## 4. The first administrator — keep this one for yourself

If the person doing the deployment runs this, the first Super Administrator
account is **theirs**, and with mandatory MFA they will enrol their own
authenticator on it. Run it yourself:

```bash
sudo ./bootstrap-admin.sh you@example.com --generate
```

The password prints **once**. Sign in, change it, then enrol an authenticator and
**save the ten recovery codes** — they are shown once and the server keeps only
hashes.

Whoever deployed can still verify the platform without an account, in §5.

---

## 5. Verify

```bash
docker compose ps                                  # all healthy
curl -fsS https://<hostname>/api/health/ready       # postgres + redis Healthy

echo | openssl s_client -connect <hostname>:443 -servername <hostname> 2>/dev/null \
  | openssl x509 -noout -issuer -enddate
```

The issuer must say **Let's Encrypt**. If it names a local CA, §2 did not take
effect and **no agent will connect**.

Then open `https://<hostname>` from a machine **outside** the office — mobile data,
not office Wi-Fi. Testing from inside succeeds even when the port-forward is
wrong, so this check has to be done from outside.

---

## 6. One agent first, then the fleet

On a **single** Windows PC, elevated:

```
msiexec /i EndpointPlatformAgent-<version>-x64.msi SERVERBASEURL=https://<hostname>
```

Approve it in the dashboard under **Enrollments** — until approved it receives
nothing, which is deliberate. Wait until it reports inventory, then roll out.

On the device page, **BitLocker** should read *automatic escrow active*. If it
says *re-enrollment required*, the server was started without the sealing pair
(§7) when this device enrolled; re-running `deploy.sh` adds the pair, and the
device must then re-enrol — see `docs/runbooks/escrow-sealing-key.md`.

Do not install on the fleet before §5 passes. With the wrong certificate every
agent fails identically and you will debug enrolment instead of TLS.

Agents are outbound-only: 60-second poll, jittered backoff, and they tolerate
being offline for days.

---

## 7. Back this up

| What | Where |
|---|---|
| Every secret | `deploy/docker/.env` |
| The database | `pg_dump` of the `postgres` container |
| Uploaded packages and agent MSIs | the package volume |

Three keys in `.env` are **unrecoverable**:

- `RECOVERY_ESCROW_KEY` — losing it makes every escrowed BitLocker recovery
  password permanently undecryptable. You discover it when a machine will not boot.
- `RECOVERY_SEALING_PRIVATE_KEY` — the private half of the pair endpoints seal
  recovery passwords to. Losing it makes every *automatically* escrowed password
  unreadable in the same way.
- `MFA_TOTP_KEY` — losing it makes every authenticator enrolment unreadable and
  nobody can sign in.

Back these up wherever the database dumps go, and somewhere the platform itself
does not depend on.

---

## 8. Deployment is manual, on purpose

There is no deploy-on-push. An autodeploy timer that polled every 60 seconds was
installed once and **disabled deliberately**: an unreviewed commit must not reach
a machine that can reveal BitLocker keys and run commands as SYSTEM on every
managed PC. Re-enable it only as a considered decision.

To deploy a change: pull, then re-run `deploy.sh`.

---

## 9. What the first administrator will see

Two interstitials on first sign-in, in this order. Neither is a fault.

1. **Forced password change** — the bootstrap password was server-generated and
   shown once. Changing it signs every session out; sign in again.
2. **Two-factor enrolment** — mandatory for every administrator, cannot be
   skipped. Scan the QR with any authenticator app, then **save the recovery
   codes**.

If someone loses both phone and codes, another administrator with
`platform.user.manage` resets their second factor from Settings → Administrators.
Nobody can reset their own — that would reduce two factors to one.

### Before the support calls start

- **Passwords are screened.** Anything built from the person's own name or email,
  a keyboard walk, a short repeated unit, or a common word with digits appended is
  refused. That is the policy working.
- **Lockout is five failures for fifteen minutes**, and the count decays. An
  address that keeps failing is refused separately, which is what stops somebody
  locking a named administrator out on purpose.
- **A code works only once**, even inside the ~90 seconds it stays valid.
