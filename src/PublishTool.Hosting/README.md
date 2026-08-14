# PublishTool.Hosting — Dev Server Deployment Guide

This is the "dev server" component of PublishTool: a small ASP.NET Core site that a team's
PublishTool GUI/CLI installs talk to over HTTP(S) instead of each publishing to a private folder
on their own machine. It does two things:

- Hosts a human-browsable build archive (the "Build Archive" page) where anyone with network
  access can browse/download builds.
- Exposes an API (`/api/*`, key-protected) that PublishTool's GUI/CLI uses to upload builds,
  manage a shared project registry, deploy builds to this server's own IIS, and manage IIS
  sites/pools remotely.

You do not need to know anything about how PublishTool itself works to deploy or maintain this —
just follow the steps below. This guide is written for whoever has IIS/server access, since the
person requesting the deployment usually won't.

## What you'll receive from the dev team

A published output folder (produced by running `dotnet publish` against the `PublishTool.Hosting`
project) — a folder of DLLs, static assets, and a `web.config`. You don't need Visual Studio or
the source code to deploy it, just this folder and IIS.

## Prerequisites on the target server

- Windows Server with IIS installed (Web Server role).
- **.NET 8 Hosting Bundle** installed — this is *not* the same as the plain .NET runtime; it adds
  the IIS module (ANCM) that lets IIS run ASP.NET Core apps at all. Get it from Microsoft's .NET
  download page ("Hosting Bundle" under .NET 8). Run `iisreset` after installing it if IIS was
  already running.
- A folder for the shared build archive, e.g. `C:\ProgramData\PublishTool\ServerBuilds` — create
  it now; permissions are step 3 below.

## First-time setup

### 1. Create the IIS site

- In IIS Manager, add a new website (or add it as an application under an existing site).
- Point its physical path at the published output folder you received.
- Give it a binding (port/hostname) the dev team's machines can reach. HTTPS is strongly
  recommended: PublishTool sends an API key in a request header on every call, and that's worth
  encrypting in transit unless this server is only reachable inside a fully trusted network.

### 2. Set the application pool identity

This is the step most likely to get skipped and cause confusing errors later. Two of this site's
features — deploying a build to IIS, and managing IIS sites/pools remotely — work by having this
site run `appcmd.exe` / manage IIS configuration on this same machine. The default
`ApplicationPoolIdentity` cannot do that. If it's left as the default, PublishTool GUI users will
see errors like "Failed to list IIS sites (appcmd exited with code ...)" or a raw ASP.NET Core
error page instead of a clean message.

In IIS Manager → **Application Pools** → (this site's pool) → **Advanced Settings** → **Identity**:

- Simplest: set it to the built-in **`LocalSystem`** account. Zero extra setup — it already has
  full control over IIS on this machine. Note this is broader than "Administrator": it's full
  SYSTEM-level access to the whole server, not just IIS, so only use it if that's acceptable for
  this box.
- More scoped alternative: create (or reuse) a dedicated service account, add it to this server's
  local **Administrators** group, then pick **Custom account** and enter its credentials. More
  setup, but a scoped, auditable identity instead of SYSTEM.

After changing this, right-click the app pool → **Recycle**.

### 3. Configure `BuildsRoot`

This is the folder builds get archived to, and where project/environment data is stored.
**Two files can set it, and the environment variable wins if both are present:**

- `web.config` (included in what you received) sets it via an `<environmentVariable
  name="BuildsRoot" ...>` entry inside `<aspNetCore><environmentVariables>` — this is what
  actually takes effect when the site runs under IIS.
- `appsettings.json` also has a `BuildsRoot` value, used only as a fallback if the app is ever run
  outside IIS. Keep both pointed at the same path to avoid confusion later.

Open `web.config` in the published folder and confirm the value points at a real folder on this
server, e.g.:

```xml
<environmentVariable name="BuildsRoot" value="C:\ProgramData\PublishTool\ServerBuilds" />
```

Create that folder if it doesn't exist, and grant the application pool identity **Modify**
permission on it (right-click the folder → Properties → Security → Edit → add the identity →
Modify). If you used `LocalSystem` in step 2, this is automatic; for a custom account, add it
explicitly.

### 4. Set the API key

Open `appsettings.json` in the published folder and set `ApiKey` to a strong random string — this
is the shared secret every PublishTool GUI/CLI user enters once, in their own Settings tab, to
talk to this server. **Do not reuse whatever placeholder key may already be in the file you
received** — treat it as an example, not a real credential, and replace it.

To generate a new one, run this in PowerShell on any Windows machine and copy the output:

```powershell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

Anyone with this key can upload/manage builds, and — since step 2 gave this site IIS management
rights — start/stop/recycle sites and deploy builds on this server. Treat it like a password, not
something to paste into chat or a ticket in plain text.

### 5. Check the upload size limit

`appsettings.json`'s `MaxUploadBytes` and `web.config`'s `maxAllowedContentLength` should match
(both default to ~500 MB) — IIS enforces its own limit before a request ever reaches the app, so
raising only one of them isn't enough. Only change these if a team's builds are larger than
500 MB.

### 6. Start the site and verify

Start (or recycle) the site in IIS Manager, then from any machine that can reach it:

```powershell
Invoke-RestMethod -Uri "https://<your-server>/api/ping" -Headers @{ "X-PublishTool-Api-Key" = "<the key from step 4>" }
```

A successful response looks like `{"ok":true}`. A 401 means the key doesn't match what's in
`appsettings.json`; anything else (connection refused, 404, 500) means the site itself isn't
running or reachable yet — check the IIS site's status in IIS Manager, and for a 500, check the
`logs\stdout` folder next to the published files (temporarily set `stdoutLogEnabled="true"` in
`web.config` for more detail if needed, then turn it back off).

Give the site's URL and the API key to whoever's coordinating PublishTool for the team — that's
the only two pieces of information devs need to enter in their own PublishTool Settings tab.

## Updating to a new version later

You'll receive a fresh published output folder each time the dev team ships a PublishTool.Hosting
update — this will happen periodically as features are added.

1. Stop the site's application pool in IIS Manager (or accept a brief blip — IIS locks files while
   running, so a straight overwrite while it's running can fail partway through).
2. **Before overwriting anything**, compare the new `appsettings.json` against the one currently
   deployed. If you customized `BuildsRoot`, `ApiKey`, or `MaxUploadBytes` on this server, carry
   those same values into the new file rather than blindly replacing it — otherwise you'll reset
   the API key and every dev's saved connection breaks.
3. Copy the new published files over the old ones, then reapply your `appsettings.json`/
   `web.config` values from step 2 if they got overwritten.
4. Start the app pool again.
5. Re-run the `/api/ping` check from step 6 above to confirm it's back up.

If devs report a 404 on some feature that used to work, it almost always means this step was
skipped and the server is still running an older build that doesn't have that feature's endpoint
yet.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Every request gets 401 | API key in `appsettings.json` doesn't match what the dev entered in their Settings tab. |
| A specific endpoint 404s but others work | Server is running an older published build — see "Updating" above. |
| "Failed to list IIS sites" or a raw ASP.NET Core error page on IIS actions | App pool identity doesn't have IIS management rights — redo step 2. |
| Devs can't reach the site at all | Firewall/network — confirm the site's port is reachable from dev machines, not just from this server itself. |
| Upload fails for a large build | Raise `MaxUploadBytes` (appsettings.json) and `maxAllowedContentLength` (web.config) together — see step 5. |
