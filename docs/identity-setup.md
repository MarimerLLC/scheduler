# Identity setup (operations)

How to set up sign-in for a deployed Scheduler: the Microsoft and Google app registrations, the certificates
that sign and encrypt OAuth tokens, and the Kubernetes configuration. For why it works this way, see
[design.md, "Identity and authentication"](design.md#identity-and-authentication).

None of this is needed for local development. In Development, `/signin` offers a development sign-in for any
address, and OpenIddict uses development certificates from your user certificate store.

Throughout, `sched.example.com` stands for the server's public host name.

## What you need

| Setting | Required | What it is |
|---|---|---|
| `Identity:Microsoft:ClientId` | For Microsoft sign-in | Application (client) ID of the Entra app registration |
| `Identity:Microsoft:ClientSecret` | For Microsoft sign-in | A client secret of that registration |
| `Identity:Google:ClientId` | For Google sign-in | OAuth client ID from Google Cloud |
| `Identity:Google:ClientSecret` | For Google sign-in | That client's secret |
| `Identity:SigningCertificate` | Yes | Base64 PKCS#12 (no password): signs the tokens the server issues |
| `Identity:EncryptionCertificate` | Yes | Base64 PKCS#12 (no password): encrypts them |
| `AllowedHosts` | Yes | The public host name, e.g. `sched.example.com` |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | Yes, behind a TLS-terminating proxy | `true` (also gives the rate limits the real client IP) |
| `RateLimits:McpPerMinute`, `PagesPerMinute`, `EmailLinksPerHour`, `RegistrationsPerHour` | No | Rate limits; the defaults (120, 30, 10, 10) suit one team. See the design doc, "Abuse prevention". |

Configure at least one provider. A provider with only one of its two values set isn't offered. The app won't
start without both certificates.

In environment variables (Kubernetes), `:` becomes `__`: `Identity__Microsoft__ClientId`,
`Identity__SigningCertificate`, and so on.

## 1. Microsoft (Entra ID and personal accounts)

In the [Microsoft Entra admin center](https://entra.microsoft.com), under **App registrations**, choose
**New registration**:

1. **Name:** `Scheduler` (people see this name when they sign in).
2. **Supported account types:** *Accounts in any organizational directory and personal Microsoft accounts*.
   The app signs in through the `common` endpoint. A single-tenant registration would refuse everyone outside
   your tenant.
3. **Redirect URI:** platform **Web**, URI `https://sched.example.com/signin/callback/microsoft`.

Then, on the new registration:

4. **Certificates & secrets → Client secrets → New client secret.** Copy the **Value** (not the Secret ID)
   right away; it is shown only once. This is `Identity:Microsoft:ClientSecret`. Note the expiry date. The
   portal allows up to 24 months.
5. **Overview:** copy the **Application (client) ID**. This is `Identity:Microsoft:ClientId`.
6. **Add the `email` and `xms_edov` optional claims to the ID token.** Without them, people signing in with a
   work or school account are refused. Open **Manifest** and set `optionalClaims.idToken`:

   ```json
   "optionalClaims": {
       "idToken": [
           { "name": "email", "essential": false },
           { "name": "xms_edov", "essential": false }
       ],
       "accessToken": [],
       "saml2Token": []
   }
   ```

   Alternatively, use **Token configuration → Add optional claim → ID**: pick `email` and `xms_edov`, and
   accept the prompt to add the Microsoft Graph `email` permission.

   Why: Entra's `email` claim is just a directory attribute, and anyone who can edit it could claim someone
   else's address. Scheduler accepts a work or school address only when `xms_edov` says the tenant owns and
   has verified that email domain. Personal Microsoft accounts (outlook.com, hotmail.com, ...) are verified by
   Microsoft and need nothing extra.
7. **API permissions:** the default delegated `User.Read` is enough. `openid`, `email` and `profile` are
   requested at sign-in. No admin consent is needed.

Work and school users whose `email` isn't in a verified domain of their own tenant aren't signed in by
Microsoft alone. They are offered a sign-in link emailed to that address instead (so outbound email must be
working), or can sign in with Google for the same address.

## 2. Google

In the [Google Cloud console](https://console.cloud.google.com), pick or create a project, then open
**Google Auth Platform**:

1. **Branding:** app name `Scheduler`, a support email, and the authorized domain `example.com` (the
   registrable domain of the public host).
2. **Audience:** user type **External**, then **Publish app**. While the app is in *Testing*, only the test
   users listed there can sign in. Scheduler asks only for `openid`, `email` and `profile`. Those are
   non-sensitive scopes, so publishing doesn't need Google's app verification.
3. **Clients → Create client:**
   - Application type: **Web application**
   - Name: `Scheduler`
   - Authorized redirect URIs: `https://sched.example.com/signin/callback/google`

   Copy the **Client ID** and **Client secret**: these are `Identity:Google:ClientId` and
   `Identity:Google:ClientSecret`. Download the JSON or copy the secret right away; the console doesn't show
   it again later.

Google marks every address it verified, and Scheduler accepts only those.

## 3. Token certificates

Scheduler is the OAuth authorization server for MCP clients (Claude, VS Code, ...). It needs two
certificates:

- **Signing:** signs the tokens. Its key usage must be `digitalSignature`.
- **Encryption:** encrypts access tokens. Its key usage must be `keyEncipherment`.

Self-signed is fine: nobody outside the server validates them. OpenIddict refuses a certificate with the wrong
key usage at startup ("The specified certificate is not a signing certificate"), so don't swap them or use one
for both.

With OpenSSL 3 (Linux, macOS, or Git Bash on Windows):

```sh
# Git Bash only: stop it rewriting "/CN=..." into a Windows path.
export MSYS_NO_PATHCONV=1

openssl req -x509 -newkey rsa:2048 -sha256 -days 730 -nodes \
  -keyout signing.key.pem -out signing.cert.pem \
  -subj "/CN=Scheduler token signing" -addext "keyUsage=critical,digitalSignature"
openssl pkcs12 -export -inkey signing.key.pem -in signing.cert.pem -out signing.pfx -passout pass:

openssl req -x509 -newkey rsa:2048 -sha256 -days 730 -nodes \
  -keyout encryption.key.pem -out encryption.cert.pem \
  -subj "/CN=Scheduler token encryption" -addext "keyUsage=critical,keyEncipherment"
openssl pkcs12 -export -inkey encryption.key.pem -in encryption.cert.pem -out encryption.pfx -passout pass:

# The setting values: one line of base64 each (about 3.5 KB).
base64 -w0 signing.pfx > signing.b64
base64 -w0 encryption.pfx > encryption.b64
```

(On macOS, use `base64 -i signing.pfx -o signing.b64`.)

The PFX files have no password, so treat them and the `.key.pem` files as secrets: put them in the secret
store, then delete the local copies. Record the expiry date (two years with `-days 730`).

**Rotating** (before expiry, or if a key leaks): generate new certificates, update the secret and restart.
The server holds one certificate of each kind, so tokens issued with the old ones stop working. Every
connected MCP client has to sign in again the next time it is used. Browser sessions aren't affected; they
use a cookie, not these certificates.

## 4. Kubernetes

One secret holds everything identity-related:

```sh
kubectl create secret generic scheduler-identity -n scheduler \
  --from-literal=Identity__Microsoft__ClientId='<application id>' \
  --from-literal=Identity__Microsoft__ClientSecret='<secret value>' \
  --from-literal=Identity__Google__ClientId='<client id>.apps.googleusercontent.com' \
  --from-literal=Identity__Google__ClientSecret='<client secret>' \
  --from-file=Identity__SigningCertificate=signing.b64 \
  --from-file=Identity__EncryptionCertificate=encryption.b64
```

Then in the deployment's container:

```yaml
envFrom:
  - secretRef:
      name: scheduler-identity
env:
  - name: AllowedHosts
    value: sched.example.com
  - name: ASPNETCORE_FORWARDEDHEADERS_ENABLED
    value: "true"
```

Why the last two matter: the server builds its OAuth issuer and its MCP resource URI
(`https://sched.example.com/mcp`) from each request's scheme and host. TLS ends at the ingress, so the app
must trust `X-Forwarded-Proto` to know the request was HTTPS. Without that, it advertises `http://` URLs and
refuses OAuth requests. `AllowedHosts` stops a request with a forged `Host` header from making it advertise
some other host. The ingress must pass the original `Host` header through, which ingress controllers do by
default.

Run one replica. That's a v1 limit for other reasons (see the design doc's Deployment section).

## 5. Check it

```sh
curl -s https://sched.example.com/.well-known/oauth-protected-resource/mcp
```

Expect `"resource": "https://sched.example.com/mcp"` and
`"authorization_servers": ["https://sched.example.com/"]`. Anything starting with `http://` means forwarded
headers aren't reaching the app.

```sh
curl -s https://sched.example.com/.well-known/oauth-authorization-server
```

Expect `"issuer": "https://sched.example.com/"`, `S256` in `code_challenge_methods_supported`,
`"client_id_metadata_document_supported": true` and a `registration_endpoint`.

```sh
curl -si -X POST https://sched.example.com/mcp | grep -i www-authenticate
```

Expect
`WWW-Authenticate: Bearer resource_metadata="https://sched.example.com/.well-known/oauth-protected-resource/mcp"`.

Then sign in for real:

1. Browse to `https://sched.example.com/signin`. A button for each configured provider appears; sign in with
   each one.
2. Connect an MCP client. For example, in Claude Code:
   `claude mcp add --transport http scheduler https://sched.example.com/mcp`, then `/mcp` to authenticate.
   The browser opens, you sign in (if you aren't already), and a consent page asks you to allow the client.
   Afterwards, ask the agent to run the `get_my_access` tool. It should report your address.

## 6. Personal access tokens (headless agents)

Agents that can't open a browser, such as RockBot, use a personal access token instead of the OAuth
sign-in. There is nothing to configure on the server for them.

1. The person who owns the agent signs in and opens `https://sched.example.com/account` (their name in the
   page header links there).
2. Under **Personal access tokens**, they name the token (for example `RockBot`), pick a lifetime (30 to 365
   days) and choose **Create token**. The token (`sched_pat_...`) is shown once, with a ready-made RockBot
   `mcp.json` entry:

   ```json
   {
     "scheduler": {
       "type": "sse",
       "url": "https://sched.example.com/mcp",
       "transportMode": "streamable-http",
       "headers": { "Authorization": "Bearer sched_pat_..." }
     }
   }
   ```

   Any other client sends the same `Authorization: Bearer` header.
3. When it expires, or if it leaks, they **Revoke** it on the same page and create a new one. Revoking
   takes effect on the agent's next request.

A token acts as its owner, with all of their verified addresses. The server stores only a hash of each
token. Creating and revoking tokens is recorded in the audit log (`token.created`, `token.revoked`).

Check one with curl: `curl -si -X POST https://sched.example.com/mcp -H "Authorization: Bearer sched_pat_..."`.
An active token gets past authorization (an MCP error about the request body is fine). An expired, revoked
or mistyped one gets a 401 with the `WWW-Authenticate` header.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Microsoft: "AADSTS50011: The redirect URI ... does not match" | The registration's Web redirect URI isn't exactly `https://sched.example.com/signin/callback/microsoft` (scheme, host, path, no trailing slash). |
| Google: "Error 400: redirect_uri_mismatch" | Same, for `https://sched.example.com/signin/callback/google` under the client's Authorized redirect URIs. |
| Google: "Access blocked: ... has not completed the Google verification process" or only some people can sign in | The app is still in *Testing* under Audience; publish it. |
| "Your organization's Microsoft sign-in doesn't confirm that you own ...", with an offer to email a link | The ID token had no `xms_edov=true`. Either the optional claims from step 1.6 are missing, or the address isn't in a verified domain of the user's own tenant. The emailed link works; fix the claims if this happens to everyone in a tenant. |
| "Several links were sent to ... in the last hour" | An address gets at most 3 confirmation or sign-in links an hour. Use the newest one, or wait. |
| Requests get 429 "Too many requests" | A rate limit. If everyone hits it at once, forwarded headers are probably off, so all requests share the proxy's IP. |
| "Your Microsoft account didn't share an email address" | The `email` optional claim is missing, or the user has no email address in the directory. |
| OAuth endpoints answer "This server only accepts HTTPS requests." | The app sees plain HTTP: set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` and check that the ingress sends `X-Forwarded-Proto`. |
| Metadata shows the wrong host, or every request is 400 "Bad Request - Invalid Hostname" | `AllowedHosts` doesn't match the host the ingress forwards. |
| App won't start: "Identity:SigningCertificate and Identity:EncryptionCertificate are not configured" | One or both certificate settings are missing or empty. |
| App won't start: "The specified certificate is not a signing certificate" (or "...encryption certificate") | The certificates are swapped, or created without the key usage in step 3. |
| An MCP client that worked has to sign in again | Expected after a certificate rotation, or after 14 days without use (the refresh token expired). |
| An agent using a personal access token suddenly gets 401 | The token expired or was revoked; see its status on `/account` and create a new one. |
| Everyone signed out of the web pages after a restart | ASP.NET Data Protection keys aren't persisted yet (design doc, Deployment). MCP clients are unaffected. |
| A Microsoft client secret expired | Sign-in with Microsoft fails at the callback. Create a new secret, update the Kubernetes secret, restart, then delete the old secret in Entra. Set a reminder before the next expiry. |
