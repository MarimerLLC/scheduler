# Deployment (operations)

How to run Scheduler on the `lhotkalake` cluster. The design and its reasons are in [design.md](design.md),
"Deployment"; signing in (Microsoft, Google, token certificates) is in [identity-setup.md](identity-setup.md).

| | |
|---|---|
| Public URL | `https://scheduler.tail920062.ts.net`, through Tailscale Funnel (the same as threa) |
| MCP endpoint | `https://scheduler.tail920062.ts.net/mcp` |
| Image | `rockylhotka/scheduler:<version>` on Docker Hub, built and pushed locally (`k8s/build.sh`) |
| Namespace | `scheduler`; manifests in `k8s/`, applied by `k8s/deploy.sh` |
| Database | `scheduler` on the shared PostgreSQL (`postgres.default.svc.cluster.local`) |
| Email | sent as `scheduler@sched.marimer.llc` through an SMTP relay (Brevo's free plan by default) |
| Replicas | one (the outbox, the jobs and live updates assume it) |

## 1. Database

Create a login and a database on the shared PostgreSQL. Run each statement on its own: `CREATE DATABASE` can't run
inside the transaction that several statements in one `-c` share.

```sh
# The admin user of the shared server, from its hand-managed secret (if that key isn't there, use the admin you know).
ADMIN="$(kubectl get secret postgres-credentials -n default -o jsonpath='{.data.POSTGRES_USER}' | base64 -d)"
PASSWORD="$(openssl rand -hex 24)"; echo "$PASSWORD"   # keep it for step 3
kubectl exec -n default statefulset/postgres -- psql -U "$ADMIN" -d postgres -c "CREATE ROLE scheduler LOGIN PASSWORD '$PASSWORD';"
kubectl exec -n default statefulset/postgres -- psql -U "$ADMIN" -d postgres -c "CREATE DATABASE scheduler OWNER scheduler;"
```

The tables are created by the migration Job on the first deploy. Back up this database with the others; the app
keeps its Data Protection keys there too (sign-in cookies and unsubscribe links depend on them).

## 2. Email: the relay and DNS for sched.marimer.llc

Scheduler sends plain SMTP (`Email:Smtp`): STARTTLS on 587 by default, `SslOnConnect` for 465. Any relay that
lets you send as `scheduler@sched.marimer.llc` works.

**Brevo's free plan** is the default here: 300 emails a day with its SMTP relay, enough for this server's volume
(each invitation, reminder or update is one email per person). If the volume ever outgrows it, any transactional
provider's SMTP endpoint drops in by changing the `Email__Smtp__*` values (SMTP2GO's free plan is 1,000 a month;
Postmark, Amazon SES and Azure Communication Services are paid but cheap). Check the free plan's current limits
on Brevo's pricing page; they change.

With Brevo:

1. Create a free account. Under **Settings → Senders, Domains, IPs → Domains**, add `sched.marimer.llc` and choose
   **Authenticate this email domain**. Brevo lists the exact records: a `brevo-code` TXT record, DKIM (two
   CNAMEs, `brevo1._domainkey` and `brevo2._domainkey`, or one TXT), and DMARC. Add them at the marimer.llc DNS host
   (Brevo can do it for you if it supports that host); the domain shows as authenticated once DNS has
   propagated, which can take a day.
2. Under **Senders**, add `scheduler@sched.marimer.llc` (name `Scheduler`).
3. Under **SMTP & API → SMTP**, generate an **SMTP key** (not an API key). The relay is `smtp-relay.brevo.com`,
   port 587, STARTTLS; the username is the SMTP login shown on that page. These go in the `scheduler-app` secret
   (step 3); `k8s/secret.example.yaml` already has the host, port and security mode.
4. For DMARC, start with `p=none` and a `rua` address you read (Brevo suggests its own report address), then move
   to `p=quarantine` once the reports show only Brevo sending as the domain.

With another relay, the same three things: authenticate the domain (the relay gives SPF and DKIM records), allow
the sender address, and create SMTP credentials. The records look like this:

| Name | Type | Value |
|---|---|---|
| `sched.marimer.llc` | TXT | `v=spf1 include:<the relay's SPF domain> -all` (from the relay's docs) |
| `<selector>._domainkey.sched.marimer.llc` | TXT or CNAME | from the relay |
| `_dmarc.sched.marimer.llc` | TXT | `v=DMARC1; p=quarantine; rua=mailto:<a mailbox someone reads>` |

Many relays also want a custom return-path (bounce) domain, e.g. a CNAME `bounces.sched.marimer.llc`; add it if
asked, so SPF aligns with the From domain.

**Bounces.** A recipient the relay refuses while sending (an SMTP 5xx) is suppressed at once, the organizer is
emailed, and it is audited as `email.bounced`. Bounces that come back later by mail go to the relay's return-path
and aren't seen by Scheduler; check the relay's bounce report now and then, and add a suppression by hand if needed:

```sh
kubectl exec -n default statefulset/postgres -- psql -U "$ADMIN" -d scheduler -c \
  "INSERT INTO \"Suppressions\" (\"Email\", \"Reason\", \"At\") VALUES ('someone@example.com', 'Bounced', now());"
```

## 3. Secrets

Two secrets, never committed:

- **`scheduler-identity`**: the Microsoft and Google clients and the token certificates.
- **`scheduler-app`**: the connection string, the voting-link key, admins and organizers, and the SMTP relay.

First register the apps: [identity-setup.md](identity-setup.md) sections 1 and 2, with `scheduler.tail920062.ts.net`
as the host, so the redirect URIs are `https://scheduler.tail920062.ts.net/signin/callback/microsoft` and `.../google`.
Then, in your own terminal (Git Bash on Windows is fine):

```sh
./k8s/create-secrets.sh
```

It asks for each secret value without echoing it, checks it before going on, and creates both secrets after you
confirm:

| It asks for | It checks |
|---|---|
| Microsoft client secret (the Value, not the Secret ID) | Microsoft issues a token with it |
| Google client secret | Google's token endpoint accepts the client |
| The `scheduler` role's password (step 1) | It signs in to the database |
| Admins (exact addresses) and organizers (addresses or `*@domain`) | |
| Brevo SMTP key (the login defaults to this account's) | Optionally sends a test email to the first admin through Brevo |

It generates the token certificates (two years; the expiry is printed) and the voting-link key, and fills in the
client IDs, tenant and relay settings, which aren't secret (they are at the top of the script; change them there
for another deployment). Nothing is written to disk outside a temporary folder it deletes.

**Changing one value later** (a new client secret or SMTP key, another admin):
`./k8s/create-secrets.sh --set scheduler-app Email__Smtp__Password`, then
`kubectl rollout restart deployment/scheduler -n scheduler`. Don't delete and recreate both secrets for that: a full
run makes a new voting-link key, which breaks every voting link already emailed, and new token certificates, which
sign every MCP client out.

Without the script: `k8s/secret.example.yaml` lists every key of `scheduler-app`, and identity-setup.md section 4
shows `scheduler-identity`.

## 4. Build and deploy

From a clean checkout of `main` at the commit to deploy:

```sh
./k8s/build.sh 1.0.0      # docker build + push rockylhotka/scheduler:1.0.0
./k8s/deploy.sh 1.0.0     # shows what is running now and asks before changing anything
```

`deploy.sh` applies the namespace and ConfigMap, checks that both secrets exist, runs the migration Job
(`/migrate/efbundle` in the same image) and waits for it, then rolls out the Deployment, Service and Ingress. If
the migrations fail, the running version is left alone and the Job's log is printed.

Before deploying from a machine you haven't used for a while, `git fetch` and check what is running
(`deploy.sh` prints it) and what PRs are open, so you don't roll back someone else's deploy.

The first time, Tailscale needs a minute to issue the Funnel certificate; until then the URL doesn't answer.

**Rolling back** to an earlier image: `./k8s/deploy.sh <earlier version>`. Migrations only go forward; an older
version runs against the newer schema, which is fine as long as a release hasn't removed or renamed anything it
uses. Restore the database backup if one did.

## 5. Check it

```sh
curl -s https://scheduler.tail920062.ts.net/.well-known/oauth-protected-resource/mcp
```

It should name `https://scheduler.tail920062.ts.net/mcp` and `https://scheduler.tail920062.ts.net/` as the
authorization server. Anything with `http://` means the forwarded headers aren't reaching the app.

Then:

1. Open `https://scheduler.tail920062.ts.net/signin` from a phone off the tailnet, so Funnel is what you're
   testing, and sign in with Microsoft or Google.
2. As an organizer, create an event on `/events/new` inviting an address you can read. The invitation should
   arrive from `scheduler@sched.marimer.llc`, and the mail client should show it passed SPF, DKIM and DMARC
   (Gmail: "Show original").
3. Vote from the link in the email, and watch `/events/<id>` update.
4. Connect an MCP client (identity-setup.md, section 5) and run `get_my_access`.

Logs: `kubectl logs -n scheduler deployment/scheduler`. Traces and metrics go to the cluster's Alloy
(`OTEL_EXPORTER_OTLP_ENDPOINT` in `k8s/configmap.yaml`).

## Troubleshooting

| Symptom | Cause |
|---|---|
| Pod restarts with "Email:Smtp:Host is not configured" | The `Email__Smtp__*` keys are missing from `scheduler-app`. |
| Pod restarts with "Identity:SigningCertificate ... are not configured" | `scheduler-identity` is missing or incomplete (identity-setup.md). |
| Pod restarts with "Scheduler:MagicLinkKey is not configured" | `Scheduler__MagicLinkKey` is missing from `scheduler-app`. |
| Migration Job fails with "password authentication failed" | The connection string's password doesn't match the role from step 1. |
| Emails stay pending and the log shows SMTP errors | Wrong relay host, port, security mode or credentials; the outbox retries with backoff for about a day before marking them failed. |
| Invitations land in spam | SPF, DKIM or DMARC isn't passing for `sched.marimer.llc`; check the message headers. |
| Every request is 400 "Bad Request - Invalid Hostname" | `AllowedHosts` in `k8s/configmap.yaml` doesn't match the host Tailscale forwards. |
| Everyone hits rate limits together | The app isn't seeing client IPs, so all requests share one; check that forwarded headers are on. |
