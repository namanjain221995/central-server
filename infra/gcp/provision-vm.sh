#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Creates the Google Cloud resources the platform needs, and nothing else.
#
# Run it in CLOUD SHELL (console.cloud.google.com, the ">_" icon): gcloud is
# already installed and already authenticated as you there, so nothing has to be
# installed or given a credential locally. It also runs on any machine with
# `gcloud auth login` done.
#
#   bash infra/gcp/provision-vm.sh --project round-legacy-509201-a1
#
# What it creates, all idempotent - re-running adopts what already exists:
#
#   a static external IP        so the address survives a stop/start. An
#                               ephemeral IP changes, which breaks both the DNS
#                               record and the TLS certificate issued for it.
#   two firewall rules          80 and 443 from anywhere, to instances tagged
#                               endpoint-platform. GCP's firewall sits in front
#                               of the host, so ufw alone would not open these.
#   one firewall rule           22 from 35.235.240.0/20, Google's IAP range, so
#                               "gcloud compute ssh --tunnel-through-iap" works
#                               without exposing SSH to the internet.
#   one VM                      Ubuntu 24.04 LTS, e2-small, 30 GB balanced disk.
#   (--allow-ssh-from only)     a third rule, <name>-allow-ssh-direct. The
#                               teardown block printed at the end lists it;
#                               do not leave it behind.
#
# SSH reaches the VM through IAP by default, under your IAM identity, and port
# 22 is not open to the internet. That also means an ordinary ssh/scp client
# cannot reach it, so infra/ubuntu/Deploy-Ubuntu.ps1 (which drives a plain SSH
# session from a Windows laptop) does NOT work against this VM as provisioned.
# Use infra/gcp/deploy-to-vm.sh instead, which does the same thing over IAP.
#
# To use the Windows driver anyway, pass --allow-ssh-from <your-public-IP>/32.
# That opens 22 to exactly that address and enables metadata SSH keys on the
# instance. Prefer IAP: a home or office IP changes, and each change is another
# firewall edit.
#
# It does NOT install the platform. That is infra/ubuntu/install.sh, which runs
# on the VM afterwards; see infra/gcp/README.md.
#
# COST: roughly USD 15-20/month for an e2-small plus the disk and the static IP,
# at list price. Nothing here is free-tier: the free e2-micro has 1 GB of RAM,
# which is not enough to build the .NET images. Delete the resources with the
# command this script prints at the end.
# ---------------------------------------------------------------------------
set -euo pipefail

PROJECT=""
ZONE=us-central1-a
MACHINE_TYPE=e2-small
DISK_SIZE=30
NAME=endpoint-platform
TAG=endpoint-platform
IMAGE_FAMILY=ubuntu-2404-lts-amd64
IMAGE_PROJECT=ubuntu-os-cloud
ALLOW_SSH_FROM=""

while [ "$#" -gt 0 ]; do
    case "$1" in
        --project) PROJECT="${2:-}"; shift 2 ;;
        --zone) ZONE="${2:-}"; shift 2 ;;
        --machine-type) MACHINE_TYPE="${2:-}"; shift 2 ;;
        --disk-size) DISK_SIZE="${2:-}"; shift 2 ;;
        --name) NAME="${2:-}"; shift 2 ;;
        --allow-ssh-from) ALLOW_SSH_FROM="${2:-}"; shift 2 ;;
        -h | --help) sed -n '2,46p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

if [ -n "$ALLOW_SSH_FROM" ]; then
    if ! [[ "$ALLOW_SSH_FROM" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}/[0-9]{1,2}$ ]]; then
        echo "--allow-ssh-from must be a CIDR such as 203.0.113.10/32; got: ${ALLOW_SSH_FROM}" >&2
        exit 1
    fi
    if [ "$ALLOW_SSH_FROM" = "0.0.0.0/0" ]; then
        echo "--allow-ssh-from 0.0.0.0/0 would open SSH to the entire internet. Refusing." >&2
        exit 1
    fi
fi

command -v gcloud >/dev/null 2>&1 || {
    echo "gcloud is not installed. Easiest path: open Cloud Shell in the Google Cloud console" >&2
    echo "(the >_ icon, top right) and run this script there." >&2
    exit 1
}

if [ -z "$PROJECT" ]; then
    PROJECT="$(gcloud config get-value project 2>/dev/null || true)"
fi
if [ -z "$PROJECT" ] || [ "$PROJECT" = "(unset)" ]; then
    echo "--project <id> is required (or run: gcloud config set project <id>)" >&2
    exit 1
fi

REGION="${ZONE%-*}"
IP_NAME="${NAME}-ip"

echo "==> project ${PROJECT}, zone ${ZONE}, machine ${MACHINE_TYPE}"

gcloud() { command gcloud --project "$PROJECT" --quiet "$@"; }

# Without these the first create fails with a "API not enabled" error and a URL.
echo "==> enabling the Compute Engine and IAP APIs (no-op if already on)"
gcloud services enable compute.googleapis.com iap.googleapis.com

# --- static external IP --------------------------------------------------------

if gcloud compute addresses describe "$IP_NAME" --region "$REGION" >/dev/null 2>&1; then
    echo "==> static IP ${IP_NAME} already exists"
else
    echo "==> reserving static IP ${IP_NAME}"
    gcloud compute addresses create "$IP_NAME" --region "$REGION"
fi
IP="$(gcloud compute addresses describe "$IP_NAME" --region "$REGION" --format='value(address)')"

# --- firewall ------------------------------------------------------------------
#
# Scoped to the target tag, so these rules apply to this VM and not to anything
# else that shares the network.

ensure_rule() {
    local rule="$1"; shift
    if gcloud compute firewall-rules describe "$rule" >/dev/null 2>&1; then
        echo "==> firewall rule ${rule} already exists"
    else
        echo "==> creating firewall rule ${rule}"
        gcloud compute firewall-rules create "$rule" --target-tags "$TAG" "$@"
    fi
}

ensure_rule "${NAME}-allow-http" \
    --allow tcp:80 --source-ranges 0.0.0.0/0 \
    --description "endpoint platform: HTTP, for Let's Encrypt HTTP-01 and the redirect to HTTPS"

ensure_rule "${NAME}-allow-https" \
    --allow tcp:443 --source-ranges 0.0.0.0/0 \
    --description "endpoint platform: HTTPS, the dashboard and both APIs (nginx is the only entry point)"

# 35.235.240.0/20 is Google's Identity-Aware Proxy range. SSH arrives through
# Google's front door under your IAM identity rather than from the internet, so
# there is no "allow 22 from my IP" rule to keep updating.
ensure_rule "${NAME}-allow-ssh-iap" \
    --allow tcp:22 --source-ranges 35.235.240.0/20 \
    --description "endpoint platform: SSH via Identity-Aware Proxy only, never from the internet"

if [ -n "$ALLOW_SSH_FROM" ]; then
    ensure_rule "${NAME}-allow-ssh-direct" \
        --allow tcp:22 --source-ranges "$ALLOW_SSH_FROM" \
        --description "endpoint platform: SSH from one operator address, for Deploy-Ubuntu.ps1"
fi

# --- the VM ----------------------------------------------------------------------
#
# OS Login ties SSH access to IAM, which is what makes the IAP path work without
# managing authorized_keys. It also IGNORES instance metadata SSH keys, so the
# direct path turns it off: Deploy-Ubuntu.ps1 authenticates with a key file.

if [ -n "$ALLOW_SSH_FROM" ]; then
    oslogin=FALSE
else
    oslogin=TRUE
fi

if gcloud compute instances describe "$NAME" --zone "$ZONE" >/dev/null 2>&1; then
    echo "==> instance ${NAME} already exists"
else
    echo "==> creating instance ${NAME} (OS Login: ${oslogin})"
    gcloud compute instances create "$NAME" \
        --zone "$ZONE" \
        --machine-type "$MACHINE_TYPE" \
        --image-family "$IMAGE_FAMILY" \
        --image-project "$IMAGE_PROJECT" \
        --boot-disk-size "${DISK_SIZE}GB" \
        --boot-disk-type pd-balanced \
        --address "$IP" \
        --tags "$TAG" \
        --metadata "enable-oslogin=${oslogin}" \
        --shielded-secure-boot --shielded-vtpm --shielded-integrity-monitoring \
        --scopes https://www.googleapis.com/auth/logging.write,https://www.googleapis.com/auth/monitoring.write
fi

cat <<EOF

==============================================================================
  Provisioned.

  Instance   ${NAME}  (${MACHINE_TYPE}, ${ZONE})
  Public IP  ${IP}     <- static, survives stop/start

  NEXT STEPS

  1. Point DNS at it. Create an A record:

         <your-host-name>   A   ${IP}

     Let's Encrypt resolves that name over the public internet during the
     HTTP-01 challenge, so it must be a real, resolvable record before step 3.
     Verify from anywhere:  dig +short <your-host-name>

  2. Upload the source tree and install, in one command from the repository
     root on any machine with gcloud (Cloud Shell included):

         bash infra/gcp/deploy-to-vm.sh --project ${PROJECT} --zone ${ZONE} \\
              --name ${NAME} --host <your-host-name> --email <you@example.com>

     Or do it by hand:

         gcloud compute ssh ${NAME} --zone ${ZONE} --tunnel-through-iap
         # then, on the VM, from the repository root:
         bash infra/ubuntu/install.sh --host <your-host-name> \\
              --email <you@example.com> \\
              --admin-email <admin@example.com> --generate-admin-password

  TO DELETE EVERYTHING THIS CREATED (billing stops):

      gcloud compute instances delete ${NAME} --zone ${ZONE} --project ${PROJECT}
      gcloud compute addresses delete ${IP_NAME} --region ${REGION} --project ${PROJECT}
      gcloud compute firewall-rules delete ${NAME}-allow-http ${NAME}-allow-https \\
          ${NAME}-allow-ssh-iap ${NAME}-allow-ssh-direct --project ${PROJECT}

  (${NAME}-allow-ssh-direct exists only if you passed --allow-ssh-from; deleting
  a rule that was never created just reports "not found". Leaving it behind is
  worse than that error: it is a tcp/22 allow for an address that has probably
  since been reassigned, and it applies to every instance carrying the
  endpoint-platform tag - including one provisioned later under a different
  --name.)

  Deleting the instance destroys its disk, and with it the database: the audit
  trail, the enrolled devices and every uploaded package. Take a dump first if
  the data matters (infra/ubuntu/README.md covers backups).
==============================================================================
EOF
