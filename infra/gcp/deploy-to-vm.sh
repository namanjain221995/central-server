#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Uploads this source tree to the Compute Engine VM and installs the platform.
#
# Run from the REPOSITORY ROOT on any machine with gcloud (Cloud Shell included):
#
#   bash infra/gcp/deploy-to-vm.sh --host epp.example.com --email ops@example.com \
#        --admin-email admin@example.com
#
# Everything travels over Identity-Aware Proxy, so the VM needs no SSH port open
# to the internet and this needs no key file: gcloud authenticates as you.
#
#   1. package the working tree (git archive, so .gitignore and .gitattributes
#      are honoured: infra/.env and node_modules never ship, and *.sh arrive
#      with LF endings)
#   2. gcloud compute scp the tarball to the VM
#   3. extract into ~/app, keeping nothing from the previous tree except what
#      lives outside it (the secrets are in /etc/endpoint-platform, untouched)
#   4. run infra/ubuntu/install.sh there
#
# The FIRST run installs everything and takes 10-20 minutes on an e2-small,
# most of it apt and the .NET SDK download. Later runs are redeploys: pass
# --skip-host-prep --skip-cert and it just rebuilds and restarts, in about
# three minutes.
# ---------------------------------------------------------------------------
set -euo pipefail

PROJECT=""
ZONE=us-central1-a
NAME=endpoint-platform
REMOTE_DIR=app
PUBLIC_HOST=""
CERTBOT_EMAIL=""
ADMIN_EMAIL=""
GENERATE_PASSWORD=1
EXTRA_ARGS=()

while [ "$#" -gt 0 ]; do
    case "$1" in
        --project) PROJECT="${2:-}"; shift 2 ;;
        --zone) ZONE="${2:-}"; shift 2 ;;
        --name) NAME="${2:-}"; shift 2 ;;
        --remote-dir) REMOTE_DIR="${2:-}"; shift 2 ;;
        --host) PUBLIC_HOST="${2:-}"; shift 2 ;;
        --email) CERTBOT_EMAIL="${2:-}"; shift 2 ;;
        --admin-email) ADMIN_EMAIL="${2:-}"; shift 2 ;;
        # Prompts for the password on the VM instead of generating one. The
        # prompt is interactive, so it needs a terminal.
        --prompt-admin-password) GENERATE_PASSWORD=0; shift ;;
        # Passed through to install.sh, e.g. --skip-host-prep --skip-cert.
        --skip-host-prep | --skip-cert) EXTRA_ARGS+=("$1"); shift ;;
        -h | --help) sed -n '2,28p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

command -v gcloud >/dev/null 2>&1 || { echo "gcloud is not installed. Use Cloud Shell." >&2; exit 1; }
command -v git >/dev/null 2>&1 || { echo "git is required to package the tree." >&2; exit 1; }

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_dir"
[ -f EndpointPlatform.slnx ] || { echo "run this from the repository (root not found at ${repo_dir})" >&2; exit 1; }

if [ -z "$PUBLIC_HOST" ]; then
    echo "--host <public-dns-name> is required" >&2
    exit 1
fi
if ! [[ "$PUBLIC_HOST" =~ ^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$ ]]; then
    echo "--host must be a plain DNS name (no scheme, no port, no path); got: ${PUBLIC_HOST}" >&2
    exit 1
fi
skipping_cert=0
for arg in ${EXTRA_ARGS+"${EXTRA_ARGS[@]}"}; do
    [ "$arg" = "--skip-cert" ] && skipping_cert=1
done
if [ "$skipping_cert" -eq 0 ] && [ -z "$CERTBOT_EMAIL" ]; then
    echo "--email is required unless --skip-cert is given (Let's Encrypt needs a contact address)" >&2
    exit 1
fi
if ! [[ "$REMOTE_DIR" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
    echo "--remote-dir must be a single directory name under the login's home; got: ${REMOTE_DIR}" >&2
    exit 1
fi

if [ -z "$PROJECT" ]; then
    PROJECT="$(command gcloud config get-value project 2>/dev/null || true)"
fi
if [ -z "$PROJECT" ] || [ "$PROJECT" = "(unset)" ]; then
    echo "--project <id> is required (or run: gcloud config set project <id>)" >&2
    exit 1
fi

gc() { command gcloud --project "$PROJECT" --quiet "$@"; }
on_vm() { gc compute ssh "$NAME" --zone "$ZONE" --tunnel-through-iap --command "$1"; }

echo "==> project ${PROJECT}, instance ${NAME} (${ZONE}), host ${PUBLIC_HOST}"

# --- 1. package ---------------------------------------------------------------
#
# A TEMPORARY git index: read-tree HEAD + add -A builds an index of the working
# tree (tracked and untracked, .gitignore honoured) without touching the real
# index, so this never stages anything for the next commit.

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT
tarball="${tmp_dir}/src.tar.gz"

commit="$(git rev-parse --short HEAD 2>/dev/null || echo unversioned)"
if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
    commit="${commit}-dirty"
    echo "==> note: the working tree has uncommitted changes, and they ship"
fi

echo "==> packaging the working tree (${commit})"
tree="$(GIT_INDEX_FILE="${tmp_dir}/index" bash -c 'git read-tree HEAD && git add -A && git write-tree')"
git archive --format=tar.gz -o "$tarball" "$tree"
echo "    $(du -h "$tarball" | cut -f1)"

# --- 2. upload ------------------------------------------------------------------

echo "==> uploading to ${NAME}"
gc compute scp "$tarball" "${NAME}:~/epp-src.tar.gz" --zone "$ZONE" --tunnel-through-iap

# --- 3. extract -------------------------------------------------------------------
#
# The secrets live in /etc/endpoint-platform and the releases in
# /opt/endpoint-platform, so replacing this directory outright is safe: it is
# only ever a build tree.

echo "==> extracting into ~/${REMOTE_DIR}"
on_vm "set -e
rm -rf ~/${REMOTE_DIR}.new ~/${REMOTE_DIR}.old
mkdir -p ~/${REMOTE_DIR}.new
tar -xzf ~/epp-src.tar.gz -C ~/${REMOTE_DIR}.new
printf '%s\n' '${commit}' > ~/${REMOTE_DIR}.new/.deployed-commit
if [ -d ~/${REMOTE_DIR} ]; then mv ~/${REMOTE_DIR} ~/${REMOTE_DIR}.old; fi
mv ~/${REMOTE_DIR}.new ~/${REMOTE_DIR}
chmod 0755 ~/${REMOTE_DIR}/infra/ubuntu/*.sh ~/${REMOTE_DIR}/infra/gcp/*.sh
rm -f ~/epp-src.tar.gz
rm -rf ~/${REMOTE_DIR}.old
echo extracted"

# --- 4. install ---------------------------------------------------------------------

install_args="--host ${PUBLIC_HOST}"
[ -n "$CERTBOT_EMAIL" ] && install_args="${install_args} --email ${CERTBOT_EMAIL}"
if [ -n "$ADMIN_EMAIL" ]; then
    install_args="${install_args} --admin-email ${ADMIN_EMAIL}"
    [ "$GENERATE_PASSWORD" -eq 1 ] && install_args="${install_args} --generate-admin-password"
fi
for arg in ${EXTRA_ARGS+"${EXTRA_ARGS[@]}"}; do
    install_args="${install_args} ${arg}"
done

echo "==> running install.sh on the VM"
echo "    (the first run takes 10-20 minutes: apt, the .NET SDK, then the build)"
on_vm "cd ~/${REMOTE_DIR} && bash infra/ubuntu/install.sh ${install_args}"

cat <<EOF

==============================================================================
  Deployed ${commit} to ${NAME}.

  Dashboard   https://${PUBLIC_HOST}
  Health      https://${PUBLIC_HOST}/api/health/ready

  Redeploy after a code change (skips the slow steps):

      bash infra/gcp/deploy-to-vm.sh --project ${PROJECT} --zone ${ZONE} \\
           --name ${NAME} --host ${PUBLIC_HOST} --skip-host-prep --skip-cert

  Shell on the VM:

      gcloud compute ssh ${NAME} --zone ${ZONE} --tunnel-through-iap --project ${PROJECT}

  Logs:

      journalctl -u 'endpoint-platform-*' -f
==============================================================================
EOF
