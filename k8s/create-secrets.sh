#!/bin/bash
# Creates the two Scheduler secrets on lhotkalake (docs/deployment.md, step 3): scheduler-identity and scheduler-app.
# Run it yourself, in your own terminal: it asks for the secret values without echoing them, checks each one
# against the service it belongs to, and never prints them. The defaults below (tenant, client IDs, Brevo login)
# are this deployment's; none of them is secret.
#
#   ./create-secrets.sh                      create both secrets (refuses if either exists)
#   ./create-secrets.sh --set SECRET KEY     replace one value, e.g. --set scheduler-app Email__Smtp__Password,
#                                            then restart the app; everything else is left as it is
#
# Don't delete and recreate both secrets just to change one value: a full run generates a new voting-link key
# (every voting link already emailed stops working) and new token certificates (every MCP client signs in again).
set -euo pipefail
export MSYS_NO_PATHCONV=1   # Git Bash: keep "/CN=..." and other slashes as they are

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
command -v cygpath >/dev/null && SCRIPT_DIR="$(cygpath -m "$SCRIPT_DIR")"   # Git Bash: C:/... for kubectl
NS=scheduler
TENANT=da478866-394f-4ef4-8257-b720d51eaade
MS_CLIENT_ID=3c6f221d-5cd1-4520-b787-8d2dac6204a0
GOOGLE_CLIENT_ID=860933865162-74b02v24a2ikat6anflq687q6086asj3.apps.googleusercontent.com
GOOGLE_REDIRECT=https://scheduler.tail920062.ts.net/signin/callback/google
SMTP_HOST=smtp-relay.brevo.com
SMTP_PORT=587
FROM=scheduler@sched.marimer.llc

fail() { echo "ERROR: $*" >&2; exit 1; }
ok() { echo "  ok: $*"; }

CONTEXT="$(kubectl config current-context)"
[[ "$CONTEXT" == lhotkalake ]] || fail "kubectl context is '$CONTEXT', not lhotkalake."

WORK="$(mktemp -d)"
chmod 700 "$WORK"
# Git Bash: with path conversion off, Windows programs (kubectl, openssl, curl) can't open "/tmp/..."; give them
# C:/... instead, which bash understands too.
command -v cygpath >/dev/null && WORK="$(cygpath -m "$WORK")"
trap 'rm -rf "$WORK"' EXIT
mkdir "$WORK/identity" "$WORK/app"   # on Windows the user's temp folder is already private
# Each file becomes one key of the secret, byte for byte (no trailing newline).
put() { printf '%s' "$3" > "$WORK/$1/$2"; }

ask() {   # ask VAR "prompt" [secret]
    local value
    if [[ "${3:-}" == secret ]]; then read -r -s -p "$2: " value; echo; else read -r -p "$2: " value; fi
    [[ -n "$value" ]] || fail "$2 is required."
    printf -v "$1" '%s' "$value"
}

# ── --set SECRET KEY: replace one value ──────────────────────────────────────
if [[ "${1:-}" == --set ]]; then
    SECRET="${2:?Usage: $0 --set <scheduler-identity|scheduler-app> <KEY>}"
    KEY="${3:?Usage: $0 --set <scheduler-identity|scheduler-app> <KEY>}"
    [[ "$SECRET" == scheduler-identity || "$SECRET" == scheduler-app ]] || fail "Unknown secret '$SECRET'."
    [[ "$KEY" =~ ^[A-Za-z0-9_]+$ ]] || fail "'$KEY' isn't a setting name, e.g. Email__Smtp__Password."
    kubectl get secret "$SECRET" -n "$NS" >/dev/null 2>&1 || fail "Secret $SECRET doesn't exist; run without --set first."
    if [[ "$KEY" == Scheduler__MagicLinkKey ]]; then
        read -r -p "A new voting-link key breaks every voting link already emailed. Continue? [y/N] " answer
        [[ "$answer" =~ ^[yY] ]] || fail "Stopped; nothing changed."
    fi
    ask VALUE "New value for $KEY in $SECRET (not checked; it is used after the restart below)" secret
    # A JSON patch from a file, so the value is never on a command line. "add" also replaces an existing key.
    printf '[{"op":"add","path":"/data/%s","value":"%s"}]' "$KEY" "$(printf '%s' "$VALUE" | base64 -w0)" > "$WORK/patch.json"
    unset VALUE
    kubectl patch secret "$SECRET" -n "$NS" --type json --patch-file "$WORK/patch.json" >/dev/null
    ok "Replaced $KEY in $SECRET."
    echo "The app reads secrets at startup. To use the new value: kubectl rollout restart deployment/scheduler -n $NS"
    exit 0
fi

for secret in scheduler-identity scheduler-app; do
    if kubectl get secret "$secret" -n "$NS" >/dev/null 2>&1; then
        fail "Secret $secret already exists. To change one value: $0 --set $secret <KEY>. Recreating both also replaces the voting-link key and token certificates (see the top of this script)."
    fi
done

echo "Creating Scheduler secrets in namespace $NS on $CONTEXT."
echo ""

# ── Microsoft ────────────────────────────────────────────────────────────────
echo "Microsoft (app $MS_CLIENT_ID)"
ask MS_SECRET "  Client secret VALUE (Entra: Certificates & secrets; not the Secret ID)" secret
# A client-credentials request succeeds only with the right secret (no permissions needed for the check).
response="$(printf 'client_id=%s&client_secret=%s&grant_type=client_credentials&scope=https%%3A%%2F%%2Fgraph.microsoft.com%%2F.default' \
    "$MS_CLIENT_ID" "$(printf '%s' "$MS_SECRET" | python -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.stdin.read(), safe=""), end="")')" \
    | curl -s --data @- "https://login.microsoftonline.com/$TENANT/oauth2/v2.0/token")"
[[ "$response" == *access_token* ]] || fail "Microsoft refused the client secret: $(printf '%s' "$response" | grep -oE 'AADSTS[0-9]+[^"]{0,100}' | head -1)"
ok "Microsoft accepts the client secret."
put identity Identity__Microsoft__ClientId "$MS_CLIENT_ID"
put identity Identity__Microsoft__ClientSecret "$MS_SECRET"
unset MS_SECRET response

# ── Google ───────────────────────────────────────────────────────────────────
echo "Google (client ${GOOGLE_CLIENT_ID%%.*}...)"
ask GOOGLE_SECRET "  Client secret (Google Auth Platform: Clients)" secret
# Redeeming a made-up code: Google says invalid_grant if the client authenticated, invalid_client if not.
response="$(printf 'client_id=%s&client_secret=%s&grant_type=authorization_code&code=check-only&redirect_uri=%s' \
    "$GOOGLE_CLIENT_ID" "$(printf '%s' "$GOOGLE_SECRET" | python -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.stdin.read(), safe=""), end="")')" \
    "$(python -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""), end="")' "$GOOGLE_REDIRECT")" \
    | curl -s --data @- https://oauth2.googleapis.com/token)"
[[ "$response" == *invalid_grant* ]] || fail "Google refused the client secret: $(printf '%s' "$response" | tr -d '\n' | head -c 200)"
ok "Google accepts the client secret."
put identity Identity__Google__ClientId "$GOOGLE_CLIENT_ID"
put identity Identity__Google__ClientSecret "$GOOGLE_SECRET"
unset GOOGLE_SECRET response

# ── Token certificates ───────────────────────────────────────────────────────
echo "Token certificates"
for kind in signing:digitalSignature encryption:keyEncipherment; do
    name="${kind%%:*}"; usage="${kind##*:}"
    openssl req -x509 -newkey rsa:2048 -sha256 -days 730 -nodes \
        -keyout "$WORK/$name.key.pem" -out "$WORK/$name.cert.pem" \
        -subj "/CN=Scheduler token $name" -addext "keyUsage=critical,$usage" 2>/dev/null
    openssl pkcs12 -export -inkey "$WORK/$name.key.pem" -in "$WORK/$name.cert.pem" -out "$WORK/$name.pfx" -passout pass:
done
put identity Identity__SigningCertificate "$(base64 -w0 "$WORK/signing.pfx")"
put identity Identity__EncryptionCertificate "$(base64 -w0 "$WORK/encryption.pfx")"
ok "Generated; they expire $(openssl x509 -enddate -noout -in "$WORK/signing.cert.pem" | cut -d= -f2). Rotate before then (docs/identity-setup.md, section 3)."

# ── Database ─────────────────────────────────────────────────────────────────
echo "Database (role scheduler on the shared PostgreSQL)"
ask DB_PASSWORD "  Password you gave the scheduler role in step 1" secret
[[ "$DB_PASSWORD" != *";"* && "$DB_PASSWORD" != *"'"* && "$DB_PASSWORD" != *'"'* ]] || fail "Use a password without ; or quotes (they break the connection string); change it with ALTER ROLE."
# Connect as scheduler from inside the postgres pod, to its own pod IP: 127.0.0.1 is trusted without a password, the
# pod IP needs one, as the app does. The password goes in on stdin, not the command line.
if ! printf '%s\n' "$DB_PASSWORD" | kubectl exec -i -n default statefulset/postgres -- \
    sh -c 'read -r p; PGPASSWORD="$p" psql -h "$(hostname -i | cut -d" " -f1)" -U scheduler -d scheduler -tAc "select 1"' >/dev/null 2>&1; then
    fail "Couldn't sign in to the scheduler database with that password."
fi
ok "The scheduler role signs in to the scheduler database."
put app ConnectionStrings__schedulerdb "Host=postgres.default.svc.cluster.local;Database=scheduler;Username=scheduler;Password=$DB_PASSWORD;GSS Encryption Mode=Disable"
unset DB_PASSWORD

# ── Who may administer and organize ──────────────────────────────────────────
echo "People"
ask ADMINS "  Admin addresses (comma-separated, exact addresses)"
ask ORGANIZERS "  Organizers (comma-separated; addresses or *@domain)"
i=0; IFS=',' read -ra list <<< "$ADMINS"; for a in "${list[@]}"; do a="$(echo "$a" | xargs)"; [[ -n "$a" ]] && put app "Scheduler__Admins__$i" "$a" && i=$((i+1)); done
j=0; IFS=',' read -ra list <<< "$ORGANIZERS"; for o in "${list[@]}"; do o="$(echo "$o" | xargs)"; [[ -n "$o" ]] && put app "Scheduler__Organizers__$j" "$o" && j=$((j+1)); done
ok "$i admin(s), $j organizer entr$([[ $j == 1 ]] && echo y || echo ies)."

# ── Voting-link key ──────────────────────────────────────────────────────────
put app Scheduler__MagicLinkKey "$(openssl rand -base64 32)"
ok "Generated the voting-link key. Never change it once links have gone out."

# ── Email (Brevo) ────────────────────────────────────────────────────────────
echo "Email ($SMTP_HOST:$SMTP_PORT, STARTTLS, as $FROM)"
read -r -p "  Brevo SMTP login [bec87e001@smtp-brevo.com]: " SMTP_LOGIN
SMTP_LOGIN="${SMTP_LOGIN:-bec87e001@smtp-brevo.com}"
ask SMTP_KEY "  Brevo SMTP key (not an API key)" secret
first_admin="$(echo "${ADMINS%%,*}" | xargs)"
read -r -p "  Send a test email to $first_admin through Brevo to check the login, sender and domain? [Y/n] " send
if [[ ! "$send" =~ ^[nN] ]]; then
    printf 'machine %s login %s password %s\n' "$SMTP_HOST" "$SMTP_LOGIN" "$SMTP_KEY" > "$WORK/netrc"
    printf 'From: Scheduler <%s>\r\nTo: <%s>\r\nSubject: Scheduler email check\r\n\r\nThis came from the Scheduler secrets script through Brevo. If it arrived, the SMTP login and the sender work.\r\nCheck the headers for spf=pass, dkim=pass and dmarc=pass.\r\n' \
        "$FROM" "$first_admin" > "$WORK/message.txt"
    if curl -s --show-error --url "smtp://$SMTP_HOST:$SMTP_PORT" --ssl-reqd --netrc-file "$WORK/netrc" \
        --mail-from "$FROM" --mail-rcpt "$first_admin" --upload-file "$WORK/message.txt"; then
        ok "Brevo accepted the test email; check $first_admin's inbox (and spam)."
    else
        fail "Brevo didn't accept the test email (wrong login or key, or the sender isn't allowed in Brevo yet)."
    fi
fi
put app Email__Smtp__Host "$SMTP_HOST"
put app Email__Smtp__Port "$SMTP_PORT"
put app Email__Smtp__Security StartTls
put app Email__Smtp__Username "$SMTP_LOGIN"
put app Email__Smtp__Password "$SMTP_KEY"
unset SMTP_KEY

# ── Create ───────────────────────────────────────────────────────────────────
echo ""
read -r -p "Create namespace $NS and both secrets on $CONTEXT now? [y/N] " answer
[[ "$answer" =~ ^[yY] ]] || fail "Stopped; nothing was created."
kubectl apply -f "$SCRIPT_DIR/namespace.yaml"
kubectl create secret generic scheduler-identity -n "$NS" --from-file="$WORK/identity"
kubectl create secret generic scheduler-app -n "$NS" --from-file="$WORK/app"
echo ""
echo "Keys (values not shown):"
for secret in scheduler-identity scheduler-app; do
    echo "  $secret: $(kubectl get secret "$secret" -n "$NS" -o go-template='{{range $k, $v := .data}}{{$k}} {{end}}')"
done
echo ""
echo "Done. Next: docs/deployment.md step 4 (k8s/build.sh, then k8s/deploy.sh)."
