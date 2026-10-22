# Solaris ID — ASP.NET Core 8 / PostgreSQL

Development branch: `feature/solaris-id`. This extends the existing Identity database and launcher integration. It does not deploy the service, change live Minecraft servers, install a modpack, or modify launcher updates.

## Implemented service boundaries

- Existing ASP.NET Identity users, stable IDs, password hashes, email confirmation/reset, lockout and authenticator TOTP are retained.
- Login issues random opaque credentials; PostgreSQL stores SHA-256 hashes only. Access expires in 15 minutes, device sessions expire after 30 days. Refresh is single-use under a database row lock. Replaying a consumed refresh revokes that device session; concurrent legitimate refresh calls must be serialized by the launcher.
- Every bearer request rechecks account ban/email/security stamp, device-session revocation and expiry. Individual logout/revoke and all-session administrative revocation are supported. Authentication remains independent of Minecraft availability.
- Owner profile and PNG skin synchronize through the API. Every registered nickname stays reserved to its persistent owner, including after a rename; the same owner can return to an earlier nickname. Nickname changes preserve the user ID but revoke sessions/old game proofs and explicitly require login again. The original local accounts/data are not deleted or automatically claimed by an online account.
- New playtime totals use only authenticated server heartbeats and the API's own clock. Old `PlayerSessions` and `LegacyImports` rows remain in the database, explicitly untrusted and excluded from trusted totals. Client POST playtime returns 403. Legacy import is marked `untrusted-legacy` and never grants trusted hours.
- Solaris Control requires `SolarisAdmin` role **and** OTP-verified `amr=mfa`. Search, trusted statistics, service health, ban/unban, revoke, password-reset mail request, role management and audit are provided. The last administrator cannot be removed; concurrent role changes are serialized. No password/hash/session secret is returned to administrators.
- Game access uses separate audience-restricted server credentials and one-use tickets, with manual HTTPS launcher connection approval for unmodified clients. See `docs/solaris-id/CONTRACT.md`; neither nickname nor offline UUID is treated as identity proof.

## API

Credentials: `/api/v1/accounts`, `/api/v1/identity/login`, `/refresh`, `/logout`, `/forgotPassword`, `/resetPassword`, `/confirmEmail`, `/resendConfirmationEmail`. The old generic Identity `/register` bypass is disabled. Login keeps the launcher's existing token response fields, adding `sessionId`.

Owner: GET/PATCH `/api/v1/me`, GET/PUT `/api/v1/me/skin`, GET `/api/v1/me/playtime`, GET `/api/v1/me/sessions`, DELETE `/api/v1/me/sessions/{id}`. Profile mutation allows only a validated nickname; email/roles/ban/ID cannot be changed through this route.

MFA: POST `/api/v1/me/mfa/setup` with password returns an authenticator URI and secret **only** to its owner; first key generation preserves that password-proven enrollment session while revoking other devices. POST `/enable` with a valid code enables MFA and returns recovery codes once. Re-login with `twoFactorCode` or an unused `twoFactorRecoveryCode` is required after enabling. Recovery login still requires the password and consumes exactly one backup code through Identity; replay fails. A recovery session restores owner access but cannot use Solaris Control; an authenticator OTP login is required for administrative operations. Administrators must remove their administrator role before disabling MFA. Store recovery material securely; do not log or share it. Password reset alone does not remove MFA.

Control: `/api/v1/control/accounts?search=`, `/accounts/{id}/stats`, `/accounts/{id}/ban`, `/unban`, `/revoke`, `/reset-password`, `/roles`, `/audit`, `/status`. Role request: `{role:"SolarisAdmin",enabled:true|false,reason:"..."}`. Granting admin requires confirmed email and MFA already enabled on the target account.

## Runtime configuration

Configure secrets through the server's protected environment/secret manager, never repository files, CLI arguments, chat, screenshots or logs:

| Variable | Purpose |
| --- | --- |
| `SOLARIS_ID_CONNECTION_STRING` | Dedicated PostgreSQL role/database; minimal application permissions |
| `SOLARIS_ID_PUBLIC_ORIGIN` | Externally visible HTTPS origin used by confirmation links |
| `SOLARIS_ID_DATA_PROTECTION_KEYS` | Persistent directory for Identity email/reset protection keys; mandatory outside Development |
| `SOLARIS_SMTP_HOST`, `SOLARIS_SMTP_PORT` | SMTP submission server; default port 587, STARTTLS required |
| `SOLARIS_SMTP_FROM`, `SOLARIS_SMTP_USER`, `SOLARIS_SMTP_PASSWORD` | Sender and submission credentials |
| `SOLARIS_GAME_SERVERS` | Server credential JSON; unique strong key and fixed ID/mode per bridge; see contract |

Use OS permissions/encrypted storage for DB backups and the Data Protection key directory. Protect/backup the key ring; replacing it invalidates outstanding confirmation/reset links. DB credentials, SMTP credentials and bridge keys must be rotated through the hosting secret manager.

Serve direct Kestrel HTTPS or bind Kestrel to a private/loopback HTTPS listener behind the verified HAProxy example in `deploy/solaris-id/REVERSE_PROXY.md`. Only default known loopback reverse proxies are trusted for forwarded headers. Configure any additional known proxy explicitly; do not trust arbitrary forwarded IP/scheme headers. Non-HTTPS API requests fail closed. Health verifies PostgreSQL, not just a listening process. Both access and error logs must exclude credential-bearing query strings, authorization headers and bodies. The HAProxy template keeps operational status/error fields without raw request data; other proxies require their own verified logging configuration.

Request-body size is globally bounded; skin upload additionally streams with a 512 KiB cap and validates PNG chunks/CRCs/zlib scanlines/dimensions. Accepted skin format is 64x64 noninterlaced 8-bit RGB or RGBA PNG; convert indexed/interlaced PNG before upload. API responses use `Cache-Control: no-store`. Hosting/HTTP request and EF SQL-command logging are suppressed below Warning to avoid credential/query leakage; never enable body logging or EF sensitive-data logging in production.

Production authentication limit: 10 credential/administrative mutations per IP per minute plus five failed password/OTP attempts per account before a 15-minute lockout. Development-only `SOLARIS_AUTH_RATE_LIMIT` permits isolated integration tests to raise the IP limit; production ignores it. `SOLARIS_ID_ALLOW_HTTP_LOOPBACK=true` permits plaintext **only** when Environment=Development and the peer is loopback; real HTTPS should be used for integration tests too. Neither option is appropriate for production.

## Migrations and first administrator

Back up and test restore before applying migrations. Use a deployment/migration role with schema permissions, then a restricted runtime role. Do not use `EnsureCreated`, delete users, clear the database or auto-merge accounts.

```bash
dotnet tool install --tool-path ./tools dotnet-ef --version 8.0.11
dotnet restore backend/Solaris.Id.Api/Solaris.Id.Api.csproj
dotnet build backend/Solaris.Id.Api/Solaris.Id.Api.csproj -c Release
./tools/dotnet-ef database update --project backend/Solaris.Id.Api --startup-project backend/Solaris.Id.Api --configuration Release
```

`20261021164937_SolarisIdSessionsAndGameAccess` adds sessions/game records and unique normalized email. Its preflight aborts on pre-existing duplicate normalized emails with an actionable error; it never deletes or merges accounts. Resolve actual ownership explicitly after backup, then retry. Existing users/hashes, skins and historical rows remain. Old stateless bearer tokens are not accepted by the new session scheme; users sign in again.

`20261021165655_PermanentNicknameOwnership` adds permanent nickname reservations and reserves each existing account's current nickname to its stable user ID. Historical nicknames that were changed before this migration are not present in the existing schema and require operator-reviewed ownership records if they must also be reserved.

Create the first administrator through an explicit operator action: register, confirm email, enable authenticator MFA, identify the exact persistent user UUID, then run on the server:

```bash
dotnet backend/Solaris.Id.Api/bin/Release/net8.0/Solaris.Id.Api.dll --bootstrap-admin USER_UUID
```

The command uses configured DB credentials, exits without starting HTTP, refuses nickname-based grants, requires confirmed/unbanned/MFA-enabled account, records an audit event, and refuses to bootstrap another administrator once one exists. Subsequent role management goes through authenticated, audited Control. There is no public registration shortcut or environment nickname that grants administrator access.

## Operations and remaining deployment work

Run restore/build plus the real HTTPS/PostgreSQL/SMTP security integration suite before deployment; see `tests/SolarisId.Integration/README.md` when present. Keep test fixtures isolated from production. Current cloud validation does not prove a deployed domain/mail reputation, Windows DPAPI UI, native Minecraft/Paper/Forge client joins, or production load/backup recovery.

Provide a separate HTTPS auth host, PostgreSQL, trusted SMTP sender/certificate, persisted protected keys, correct game-server IDs/strong keys, backups and monitored service startup. Registration/recovery cannot deliver real mail until SMTP/DNS is configured. No external deployment or live server change is performed by committing these sources.

Plan regular retention of expired refresh-history rows after their expiry; expired session/game audit history must respect foreign keys and retention policy. No cleanup should invalidate an active session or delete historical accounts. Proxy/API logs must remain free of password, refresh/access, bridge-key and email-confirmation/reset values.
