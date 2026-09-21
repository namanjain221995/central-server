# Provisioning the escrow sealing keypair

Automatic BitLocker recovery-password escrow seals on the endpoint. Endpoints
encrypt to a **public** key; only the **private** half can read the result back.
Splitting that pair across the two APIs is the control that keeps the
endpoint-facing process unable to read what it stores.

This runbook contains no key material and never will. Everything below is run on
the host; nothing is pasted into source control, a ticket, or a chat window.

## Requirements at a glance

| | |
|---|---|
| Algorithm | RSA, **3072 bits minimum** — enforced in code on both halves |
| Private format | **PKCS#8 DER**, base64 — PKCS#1 is rejected at startup |
| Public format | **SPKI DER**, base64 |
| Fingerprint | SHA-256 over the SPKI DER, hex — public, not a secret |
| Private key | **admin-api only**; the Agent API refuses to start if it sees one |
| Public key | admin-api **and** agent-api |
| `/etc/endpoint-platform/secrets.env` | mode **600**, `root:root` |
| Secret values | **never** printed, echoed, logged, pasted or committed |

## Where each half goes

| Half | Process | Setting | Why |
|---|---|---|---|
| Public (SPKI) | **admin-api and agent-api** | `RecoveryEscrow:SealingPublicKey` | Encrypts only. Agent API pins it at enrollment and rejects envelopes sealed elsewhere. |
| Private (PKCS#8) | **admin-api only** | `RecoveryEscrow:SealingPrivateKey` | Anything holding this can read every automatically escrowed recovery password. |

Two startup guards enforce this and both fail the host rather than warn:

- **Agent API** refuses to start if `RecoveryEscrow:SealingPrivateKey` or the
  master `RecoveryEscrow:Key` appears in its configuration.
- **Admin API** refuses to start if a public key is configured without its
  matching private half, or if the two are not the same pair. Without that check,
  every escrow would *succeed* while nobody could ever open one — discovered on
  the day a disk will not boot.

Leaving both empty is a valid state. Automatic escrow is simply off: no device
becomes eligible, and manual escrow is unaffected.

## Generating the pair

Run on the host as root.

**The PKCS#8 conversion in step 2 is required, not tidiness.** `openssl genpkey`
with `-outform DER` does not reliably emit PKCS#8: on some builds it writes a
bare PKCS#1 `RSAPrivateKey`, which the Admin API rejects at startup with
`ASN1 corrupted data ... tagged with 'Universal' class value '2'`. That happened
during a real provisioning run.

```sh
umask 077
D=$(mktemp -d) && chmod 700 "$D"

# 1. Generate RSA-3072. Format not yet guaranteed.
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -outform DER -out "$D/raw.der"

# 2. Force PKCS#8 DER. This is the format the Admin API imports.
openssl pkcs8 -topk8 -nocrypt -inform DER -outform DER -in "$D/raw.der" -out "$D/private.der"

# 3. Derive the public half from the private one, so they cannot disagree.
openssl pkey -inform DER -in "$D/private.der" -pubout -outform DER -out "$D/public.der"

# 4. Encode to FILES, never into a shell variable that is later interpolated.
base64 -w0 "$D/private.der" > "$D/private.b64"
base64 -w0 "$D/public.der"  > "$D/public.b64"
```

## Writing the values into `secrets.env`

**Write from files by concatenation. Do not interpolate base64 into a format
string.** An unquoted `printf` format loses its backslash, and the failure is
close to invisible: the value gains a trailing literal `n`, is one character too
long, still looks like base64, still decodes far enough to parse — and is
refused at startup as invalid base64. That also happened during a real run.

```sh
F=/etc/endpoint-platform/secrets.env

# The file already carries empty placeholders for both, so remove those lines
# first rather than appending a second definition of each.
sed -i '/^RECOVERY_SEALING_PUBLIC_KEY=$/d;/^RECOVERY_SEALING_PRIVATE_KEY=$/d' "$F"

{
  printf 'RECOVERY_SEALING_PUBLIC_KEY='
  cat "$D/public.b64"
  echo
  printf 'RECOVERY_SEALING_PRIVATE_KEY='
  cat "$D/private.b64"
  echo
} >> "$F"

chown root:root "$F"
chmod 600 "$F"
shred -u "$D"/*.der "$D"/*.b64 && rmdir "$D"
```

Two rules, if a format string is used at all:

- **Always quote it.** Write `printf '%s\n' "$value"`, never `printf %s\n $value`.
- **Never build a `NAME=VALUE` line with a two-argument format.** The unquoted
  `printf %s=%s\n NAME "$VALUE"` form is exactly what corrupted a production
  value; `cat` from a file cannot corrupt anything.

## Verifying before restarting anything

Every check reports a length, a fingerprint, or a yes/no. **None prints a key.**
Run them *before* restarting the Admin API, so a bad value is caught while the
service is still up rather than as a crash loop.

```sh
F=/etc/endpoint-platform/secrets.env

# Base64 length must be a multiple of 4. One that is not is corrupt, however
# plausible it looks. This single check catches the trailing-character failure.
V=$(grep '^RECOVERY_SEALING_PRIVATE_KEY=' "$F" | cut -d= -f2-)
echo "private: ${#V} chars, mod4=$(( ${#V} % 4 ))"

# It must decode AND parse as PKCS#8 RSA. This catches the PKCS#1 failure.
grep '^RECOVERY_SEALING_PRIVATE_KEY=' "$F" | cut -d= -f2- | base64 -d | openssl pkey -inform DER -noout && echo "private: valid PKCS#8"

# Confirm the key size the code requires.
grep '^RECOVERY_SEALING_PRIVATE_KEY=' "$F" | cut -d= -f2- | base64 -d | openssl pkey -inform DER -noout -text | head -1

# The halves must be the same key. Derive the public half from the private one
# and compare fingerprints; mismatched halves mean every escrow succeeds and
# none can ever be revealed.
A=$(grep '^RECOVERY_SEALING_PRIVATE_KEY=' "$F" | cut -d= -f2- | base64 -d | openssl pkey -inform DER -pubout -outform DER | openssl dgst -sha256 -hex | awk '{print $NF}')
B=$(grep '^RECOVERY_SEALING_PUBLIC_KEY=' "$F" | cut -d= -f2- | base64 -d | openssl dgst -sha256 -hex | awk '{print $NF}')
[ "$A" = "$B" ] && echo "pair verified, SPKI SHA-256: $A" || echo "MISMATCH — do not restart"

# Permissions.
stat -c '%a %U:%G' "$F"   # must be: 600 root:root

# Re-render the per-service environment files from the secrets. It refuses to
# write a value it cannot carry, and prints names only.
bash infra/ubuntu/gen-env.sh "$(grep '^PUBLIC_ORIGIN=' "$F" | cut -d= -f2-)"

# Which service receives which half. Prints names and lengths, never values.
for s in admin-api agent-api; do
  awk -v svc="$s" -F= '/RecoveryEscrow__Sealing/{print svc, $1, "len=" length($2)}' \
    "/etc/endpoint-platform/${s}.env"
done
```

Expected from the last command: `admin-api` has **both** halves, `agent-api` has
**only** the public one.

The private key is **unrecoverable if lost**: back it up wherever the master
escrow key is backed up, because every automatically escrowed password depends
on it.

## Verifying after the restart

```sh
systemctl restart endpoint-platform-admin-api endpoint-platform-agent-api

# The Admin API accepted the pair if it is active with no restarts.
systemctl show endpoint-platform-admin-api -p ActiveState -p NRestarts
curl -s -o /dev/null -w '%{http_code}\n' -H 'X-Forwarded-Proto: https' \
  http://127.0.0.1:5080/health/ready

# The boundary, checked on the LIVE process environment. Counts only, never
# values. /proc/<pid>/environ is root-readable; this is the real check, not a
# reading of the file that was supposed to produce it.
PID=$(systemctl show endpoint-platform-agent-api -p MainPID --value)
tr '\0' '\n' < "/proc/$PID/environ" | grep -c 'SealingPrivateKey\|RecoveryEscrow__Key'  # must be 0
tr '\0' '\n' < "/proc/$PID/environ" | grep -c 'SealingPublicKey'                        # must be 1

# No key material reached the journal.
journalctl -u endpoint-platform-admin-api --since '30 min ago' | grep -cE '[A-Za-z0-9+/]{100,}'  # must be 0
```

The agent-facing fingerprint can also be read back; it is public and safe to
compare against the `openssl dgst` output above:

```sh
curl -s .../agent/v1/bitlocker/escrow-status -H '...' | jq -r '.sealingKeyFingerprint'
```

If the Admin API crash-loops, **read the inner exception, not the wrapper.** The
outer message is deliberately identical for both failures — `RecoveryEscrow:
SealingPrivateKey is not a valid base64 PKCS#8 RSA private key` — because it must
never quote the value. The inner exception distinguishes them:

| Inner exception | Cause | Fix |
|---|---|---|
| `AsnContentException` / `ASN1 corrupted data` | key is PKCS#1, not PKCS#8 | rerun step 2 above |
| `FormatException` (invalid base64) | the encoded value is damaged, usually a stray trailing character | check `mod4`, rewrite the line from a file |

## Rotation

Rotation is **not** automatic, and the current build does not implement
continuity proof. What happens today:

- New escrows seal under the new key.
- Existing rows stay readable: each records its `key_version` and `seal_scheme`.
- **Agents pinned to the old fingerprint refuse the new key** and stop escrowing
  until they re-enroll. This is the pin working, not a fault.

So a rotation is a fleet re-enrollment. Plan it as one.

## Turning automatic escrow on

Deliberately several deliberate steps, in this order:

1. Apply the `AutomaticBitLockerEscrow` migration.
2. Provision the keypair as above and restart both APIs.
3. Deploy an agent build that contains the escrow runner.
4. **Re-enroll** the devices that should participate. A credential issued before
   the keypair existed carries no pinned fingerprint, and an unpinned device
   never reads a recovery password. There is no trust-on-first-use path, by
   design.

Devices that are not re-enrolled keep working: full BitLocker inventory, manual
escrow, and a console status reading *automatic escrow unavailable —
re-enrollment required*.
