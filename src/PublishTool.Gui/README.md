# PublishTool.Gui

The WPF desktop app -- see the [root README](../../README.md) for what PublishTool is overall and
the [CLI](../../README.md#cli-usage), which the GUI's Command tab runs directly. This file covers
two things: how to **use** the app (this half), and how it's **packaged, distributed, and kept up
to date** on other people's machines (the second half, below).

## Getting Started

### Prerequisites

- **Windows.** The app itself, plus IIS/Firewall management (`appcmd.exe`, `netsh`), use
  Windows-only APIs.
- **To build a .NET project**, Visual Studio or Build Tools with the ASP.NET/web development
  workload, so `MSBuild.exe` has the Web Publishing targets classic `dotnet build` doesn't ship.
- **To build an Angular or Android project**, Node.js/npm on `PATH`. Android additionally needs a
  JDK and the Android SDK (Gradle itself comes from the project's own `gradlew`). The **Help** tab's
  Dependencies list checks for all of these and tells you exactly what's missing.
- **Administrator privileges are only needed for actions against *this machine's own* IIS or
  Windows Firewall** -- auto-creating a site, starting/stopping/recycling a site or app pool, and
  adding/editing/removing a firewall rule, all on the **IIS**/**Firewall** tabs, plus a Publish
  whose **Deploy target** is set to **Local**. You'll see a warning banner appear right at the point
  it's actually needed, rather than the whole app requiring elevation up front.
  - **If your team has a dev server set up (remote mode) and you're deploying/managing IIS there
    instead, you don't need Administrator at all** -- those actions run on the dev server's own
    machine, not yours. See [Setting up Remote Hosting](#setting-up-remote-hosting) below.
  - Publishing itself (building, archiving, uploading) never needs elevation, regardless of mode.

### First launch

- The app runs normally without elevation -- if you do need an elevated action later, right-click
  the app and choose **Run as administrator**, then relaunch.
- **Closing the window doesn't quit the app.** The X button hides it to the system tray (so a
  background publish or IIS monitoring keeps running); only the tray icon's **Exit** -- or Windows
  shutting down -- actually terminates it. Double-click the tray icon to bring the window back.
- **Only one copy runs at a time.** Opening PublishTool again while it's already running just
  brings the existing window to the front instead of starting a second instance.
- **It updates itself.** On every launch it silently checks GitHub for a newer version; if one's
  found, it downloads in the background (you'll see "Downloading update..." in the status bar) and
  then asks once, with what's new, whether to restart now or later. See
  [Distribution model](#distribution-model) below if you're the one shipping updates.

## Local vs. remote mode

Everything in this app works purely against files/registry/IIS on your own machine by default
("local mode"). If your team has a [`PublishTool.Hosting`](../PublishTool.Hosting/README.md) dev
server set up, turning on **Use dev server for projects + IIS tab** in Settings ("remote mode")
switches the **Projects**, **IIS**, **Firewall**, and **Audit Logs** tabs, plus the Deployment
Environments list, over to that shared server instead of this machine. A few things always stay
local to your own machine regardless of mode -- see [Local settings](#local-settings) below.

## Setting up Remote Hosting

You'll need the dev server's URL and an API key from whoever set it up (see
[`PublishTool.Hosting`'s README](../PublishTool.Hosting/README.md) if that's you).

1. Open **Settings** and find the **Remote Build Hosting** section.
2. Enter the **URL** (e.g. `https://devserver.internal`) and **API key**.
3. Click **Test connection** -- confirms the URL/key actually work before you rely on them.
4. Once that succeeds, turn on **Use dev server for projects + IIS tab**.

From then on, the **Projects**, **IIS**, **Firewall**, and **Audit Logs** tabs, and the
**Deployment Environments** list, all read from/manage the dev server instead of this machine.
Whether any individual project actually uploads/deploys its builds there is a separate, per-project
choice (its **Remote IIS** toggle, in that project's Local settings -- see
[Adding or editing a project](#adding-or-editing-a-project)), independent of this switch.

## Setting up deployment environments

An "environment" is a named deploy target -- e.g. **Staging** or **Production** -- offered
everywhere a project's IIS deployment is configured or you're picking one to publish to.

1. Open **Settings** and find the **Deployment Environments** section.
2. Type a name (e.g. `Staging`) and click **Add**.
3. **Make default** preselects that one in the "Deploy to" dropdown when you haven't picked yet.
4. **Remove** takes one back out.

This list is local to your own machine unless remote mode is on, in which case it's shared with
your whole team. Adding a name here just makes it *available* to pick from -- to actually deploy a
project to it, that project also needs its own **Local IIS** and/or **Remote IIS** toggle turned
on (in its Edit dialog), with at least one environment configured there too (name, site bindings,
whether to auto-create the site). See [Adding or editing a project](#adding-or-editing-a-project).

## Tabs

### Publish

Build, version, annotate, and (optionally) deploy one publish of a project.

1. Pick a **Project** from the dropdown -- this loads its git branches, existing versions, and (if
   enabled) its App Config settings.
2. Optionally change **Git branch** -- pre-filled with the repo's current branch. Publishing checks
   out whatever's typed here automatically before building; use **Checkout** instead to switch (and
   inspect) the working tree right away, e.g. to preview that branch's App Config first. If the
   branch has uncommitted changes, a dialog offers to discard, stash, or commit them before
   switching.
3. Enter a **Version**, or pick an existing one from the dropdown to republish it (a warning
   appears if you're about to overwrite an existing build).
4. If the project has Local and/or Remote IIS deployment enabled, a **Deploy target** / **Deploy
   to** pair appears -- pick which side and which named environment to deploy to. Picking **Local**
   shows a warning if you're not running elevated (the deploy step would otherwise fail). If
   neither side is enabled, publishing just archives (and, in remote mode, uploads) the build with
   no deploy step.
5. **Android projects only**: pick **Artifact type** (APK or AAB -- AAB is release-only, since Play
   Store doesn't take debug bundles) and **Build variant**. If this is a release build and no
   keystore is configured on the project yet, you'll be prompted once (the same fields as Android
   Studio's own signing dialog) and it's saved for next time.
6. If the project's app config has no fixed path set, **Config file to edit** appears when more
   than one matching file was found automatically -- pick which one this publish should write to.
7. Toggle **Mark as latest release** if this build should replace whichever one is currently
   flagged "latest" on the hosting site (only one build per project can hold that flag). Next to
   it, **List in hosting site** is on by default -- turn it off for a throwaway test build you
   don't want cluttering the hosting page's listing (it's still archived either way).
8. If the project has app config enabled, an **App Config** accordion shows the live config file's
   key/value pairs -- edit them here and they're written to the file at publish time. Picking an
   already-published version above shows *that build's* saved config instead, for reference.
9. **Android projects only**: an **Android Config** accordion shows Bundle Id, Display Name,
   Version Number, and Build Number, read from the native project's own files -- edit and they're
   written before building. A blank field (shown as "Not found") is left unchanged.
10. Fill in **Release notes for this build** -- four lists (Features and Enhancements, Fixes,
    Other Updates, Backlog Items) archived alongside the build and shown on the hosting site.
11. Click **Publish**.

### Projects

Register new projects and manage each one's build history and audit trail.

- **Add project** / **Edit** / **Remove** on the left manage the registration list itself -- see
  [Adding or editing a project](#adding-or-editing-a-project) below for what goes into one.
  Removing a project only unregisters it; archived builds and its IIS host folder are untouched.
- **History** (per-project, next to the build history header) shows everything recorded for just
  the selected project -- added, removed, settings changed, published, deployed, build changes.
  **All history**, up on the project list, shows the same trail across every registered project.
- Selecting a project shows its **Build history** on the right -- every archived version, when it
  was published, by whom, and whether it's flagged latest/listed on the hosting site. Each row has:
  - **Deploy this version** -- redeploys that specific build (asks which environment first, if the
    project has more than one configured). Hidden for a build that can't be deployed (e.g. Android).
  - **Mark latest** -- flags that build as the project's latest.
  - **Delete** -- permanently removes the build's archived zip/APK/AAB and release notes.

### IIS

View and control IIS sites and application pools -- on this machine, or on the dev server if
remote mode is on (Settings).

- **Sites** sub-tab: filter by All/Started/Stopped. Each row's compact icon buttons adapt to that
  site's current state -- **Start** is hidden once it's running; **Stop** and **Browse** (opens the
  site in your browser, using the dev server's own address when browsing remotely, not
  `localhost`) only show while it's running. **History** shows that site's full deploy history with
  search. **Remove** deletes the site and, if it has one, its dedicated application pool too.
- **Application Pools** sub-tab: icon buttons for **Start** / **Stop** / **Recycle**.
- **All history**, top-right, shows the audit trail for every site/pool action taken here (start,
  stop, remove, recycle, manual deploys) -- who did it and when.
- **Manual deploy** lets you create a brand-new IIS site (name, physical path, bindings, app pool
  type) or deploy into an existing one, from a folder or zip file, without needing a registered
  project at all. Works in both local and remote mode -- in remote mode, the physical path is a
  folder on the *dev server's* machine, not yours.

Starting/stopping/recycling/removing on your own machine needs Administrator elevation; the same
actions against the dev server (remote mode) don't.

### Firewall

Lists inbound Windows Firewall rules PublishTool itself has created (not a general firewall
console -- use Windows' own for anything else), on this machine or the dev server depending on
remote mode. Rules are named `[IIS] {label}` -- the protocol/port aren't folded into the name since
the grid already shows those as columns.

- **Add rule** opens a port for a label you choose (e.g. "Staging site") -- the port field takes a
  single port or netsh-style ranges, e.g. `9001,9005-9008`.
- **Edit selected rule** changes an existing rule's label/ports/protocol in place, instead of
  removing and re-adding it.
- **Remove selected rule** takes one back down.
- **History** shows the full audit trail -- every rule added, edited, or removed, by whom, and
  when.
- **Show all rules** toggles between PublishTool's own rules and every rule on the system (useful
  for checking whether a port's already taken) -- Edit/Remove still only work on PublishTool's own
  `[IIS]` rules either way.

Useful right after creating a new IIS site, so whatever's supposed to reach it on that port
actually can. Adding/editing/removing on your own machine also needs Administrator elevation.

### Event Logs

Browse a per-project slice of the Windows Event Log, for projects that have this turned on (see
[Adding or editing a project](#adding-or-editing-a-project)).

Pick a **Project**, then filter by free-text search, **Level** (Critical/Error/Warning/Info/
Verbose), **Method**, or **Type**. Click a row to see its full message. **Export CSV** saves the
currently-filtered rows.

If the project's event log is configured to read from a specific remote machine (local mode only --
remote/dev-server mode never needs this), you may be prompted once for that machine's credentials;
check "Remember" to save the password (encrypted, this Windows user only) so you're not asked again.

### Audit Logs

A single combined, searchable view merging the **Projects**, **Firewall**, and **IIS** tabs' own
audit trails into one list -- every action recorded anywhere in the app, newest first, with its
category, who did it, and when. Each of those tabs also has its own scoped history button if you
only care about one area; this tab is for seeing everything together. Click **Refresh** to reload.

### Command

A single free-text box that runs the exact same commands as the CLI, with output streamed into the
log panel below -- useful for anything that doesn't have a dedicated button yet. Press Enter or
click **Run**. See the [root README's CLI usage](../../README.md#cli-usage) for the full command
list; every one of those works here too, including a couple with no GUI equivalent
(`set-msbuild-path`, `list-builds`).

### Settings

- **Builds root** -- where archived builds are stored on this machine (`Browse...` / `Save` /
  **Open in Explorer**).
- **Dark mode** -- leave it alone to follow your OS theme automatically, or toggle it to pin
  Light/Dark regardless of OS setting.
- **Accent color** -- pick a swatch; applying it restarts the app.
- **Start on Windows startup** -- launches PublishTool automatically at login, running in the tray.
- **Project configs** -- **Export projects...** saves selected registrations to a `.ptproj.json`
  file (e.g. to hand to a teammate); **Import projects...** reads one back in, flagging any name
  collisions with what's already registered here so nothing gets silently overwritten.
- **Remote Build Hosting** -- see [Setting up Remote Hosting](#setting-up-remote-hosting) above.
- **Deployment Environments** -- see [Setting up deployment environments](#setting-up-deployment-environments)
  above.

### Help

Shows the installed version (**Check for updates** to check right now instead of waiting) and a
dependency check (**Recheck**) for MSBuild, IIS (`appcmd.exe`), Node.js/npm, Java, and the Android
SDK -- see [Prerequisites](#prerequisites) above for what each one is needed for. If anything
required for what you actually use is missing, a warning pops up automatically the first time you
open the app.

## Adding or editing a project

Opened from the Projects tab's **Add project** / **Edit** buttons. The only required field is
**Name** -- every other field is optional, so a project can be registered purely to manage an
existing build's IIS site, Event Log, or firewall rules, with nothing else filled in. Publish
itself is what enforces whatever a given project type actually needs to build, with a clear error
naming what's missing.

Fields are split into two groups:

- **Local settings** -- facts about this machine and your own personal preferences (where your
  clone of the repo lives, your own local IIS target, whether *you* want this project's dev-server
  deploy target offered to you). Every dev registering the same shared project sees their own copy,
  and these stay editable even in remote mode.
- **Shared settings** -- properties of the project itself. In remote mode, every PublishTool user
  sees the same values here, and editing them needs an extra "Edit shared settings" confirmation
  since it affects the whole team. **Name** is the one exception in this group -- it's locked
  permanently once a project is first added (in local mode too), since it's the key its build
  folder and any shared registration are filed under; renaming it isn't supported.

### Local settings

- **Project root folder** (optional) -- for Angular/Android projects, the app's root folder (see
  Project type below). Not shown for .NET.
- **Release signing** (Android only) -- optional, the same fields as Android Studio's "Generate
  Signed Bundle/APK" dialog (keystore path, keystore password, key alias, key password), used to
  sign a release build. Leave unconfigured to fall back to the native project's own signingConfig,
  if it has one, or to be prompted once at publish time. Passwords are encrypted and never shared
  with your team.
- **.csproj path** (optional, .NET only) -- only needed to Publish; leave blank for a project
  registered just to redeploy an existing build or manage its Event Logs/IIS/firewall rules.
- **AssemblyInfo.cs** (optional, .NET only) -- for version stamping.
- **Publish profile** (optional, .NET only) -- the `.pubxml` profile name to build with.
- **Remote IIS** toggle -- your own personal choice of whether *you* want this project's dev-server
  deploy target (configured below, in Shared settings) offered to you on the Publish tab. Only
  takes effect while remote mode is on. Teammates decide this independently.
- **Local IIS** toggle (.NET and Angular only -- Android has no IIS equivalent) -- turn on to
  deploy this project to IIS sites on *your own* machine. Set a host root path, add one or more
  named environments, and configure each environment's site bindings (protocol/IP/port/hostname).
  Whoever publishes with this on needs to run PublishTool elevated on their own machine, or the
  local deploy step fails.

### Shared settings

- **Project type** -- **.NET** (the default), **Angular**, or **Android (Capacitor/Cordova)**.
  Picks how Publish actually builds the project:
  - **.NET** uses the Local settings above (.csproj/AssemblyInfo/Publish profile), plus **Extra
    publish targets** (optional) and **Modern SDK-style project** (turn on for ASP.NET Core-style
    projects instead of classic .NET Framework Web Deploy projects).
  - **Angular** just needs **Project root folder** pointed at the app's root (where
    `package.json`/`angular.json` live) -- Publish runs `npm run build` there. Optionally set a
    **Workspace project** name for a workspace with more than one buildable project; build
    configuration is inferred from whichever app-config environment file you're editing, or picked
    on the Publish tab. The built output deploys to IIS exactly like a .NET project's does, with a
    default SPA-routing `web.config` generated if the build doesn't already have one.
  - **Android (Capacitor/Cordova)** needs **Project root folder** pointed at a hybrid app's root
    (Ionic, Capacitor, or Cordova -- whichever frontend framework is underneath doesn't matter).
    PublishTool auto-detects whether it's a Capacitor or Cordova project from what's in that folder
    (shown live as **Detected: ...**) and builds accordingly. Build variant, artifact type (APK/AAB),
    and app metadata (Bundle Id, Display Name, versions) are all chosen per-publish on the Publish
    tab, not here. There's no deploy step -- the built file lands on the Build Archive hosting page
    as a download instead.
- **Project ID** -- a short code used as the release-notes reference prefix (e.g. `BPS` →
  `BPS-2026-0007`).
- **Edit user-visible app config from the Publish tab** toggle -- when on, pick a **Config type**
  (Web.config/App.config, appsettings.json, or an Angular/Ionic `environment.ts` file) and,
  optionally, a fixed **Config file path**. Leave the path blank to have PublishTool search for a
  matching file under the project's own source folder automatically each time it's needed -- you're
  only asked to pick if that search finds more than one match.
- **Enable Event Logs tab for this project** -- turn on to make this project selectable on the
  Event Logs tab; set the log name, and how entries are matched (by Event Source name, or by a
  substring in the message -- useful for apps sharing a generic log via something like NLog), plus
  an optional remote machine/username for local-mode reads.
- **Dev-server environments** -- where this project's builds get deployed on the dev server (e.g.
  Staging, Production). Shown once **Remote IIS** is toggled on in Local settings above. Shared:
  the same targets apply no matter who deploys.

## Other dialogs you might see

- **Git Conflict** -- when checking out a branch with uncommitted changes: discard, stash, commit,
  or (if the checkout wasn't actually blocked) proceed anyway.
- **Environment Picker** -- when redeploying a build from the Projects tab and the project has more
  than one environment configured on that side.
- **Edit Environment** -- name, auto-create-site toggle, and site bindings grid, for one Local or
  Remote environment on a project.
- **Android Signing** -- keystore path, keystore password, key alias, key password, opened from a
  project's Local settings or (if none is configured yet) automatically when publishing a release
  build.
- **Manual Deploy** -- see the IIS tab above.
- **Update Available** -- see "It updates itself" under Getting Started above.

---

# Distributing and releasing PublishTool.Gui

The rest of this file is for whoever ships updates to the app itself -- not needed just to use it.

## Distribution model

The GUI is packaged with [Velopack](https://velopack.io) and self-updates via GitHub Releases on
this repo (`marhano/lazyly`). Instead of handing someone a portable `.exe`, you hand them a link to
`Setup.exe` from the [latest GitHub Release](https://github.com/marhano/lazyly/releases/latest) --
they run it once, and from then on the app checks for and installs new versions itself in the
background, prompting only once an update has finished downloading (version + release notes,
Restart Now / Later).

This only works once the repo is public (so the installed app can check GitHub Releases without an
embedded credential) and the first release has actually been published.

### Update-check rate limit

GitHub caps unauthenticated API requests at 60/hour per network -- fine for one person, but a
whole team behind the same office connection can exhaust that shared budget (surfaces as "Update
check failed: ... 403 (rate limit exceeded)"). The app already throttles its own automatic check
to once per 6 hours per machine, but the real fix is a **read-only** GitHub token baked into the
build, raising the limit to 5000/hour:

1. Generate a token with no write scopes -- github.com -> Settings -> Developer settings ->
   Personal access tokens. A classic token with zero scopes checked (or a fine-grained token
   scoped to just this repo with Contents: Read-only) is enough; it only needs to prove you're an
   authenticated request, not actually access anything private.
2. Add it as a **repository secret** named `GH_UPDATE_TOKEN` -- this repo's Settings -> Secrets
   and variables -> Actions -> New repository secret. Paste the token value there; it's never
   pasted anywhere else, including this repo's source.
3. That's it -- [`.github/workflows/release-gui.yml`](../../.github/workflows/release-gui.yml)
   already passes it to the build (`-p:GithubUpdateToken=...`, embedded as assembly metadata, read
   at runtime in `MainWindow.xaml.cs`). Every release built after the secret is added picks it up
   automatically; older installed copies get it the next time they update.

Skipping this is safe -- a build with no token just checks unauthenticated, same as before this
existed.

## Releasing an update

### Automated (normal path)

1. Bump `<Version>` in [`PublishTool.Gui.csproj`](PublishTool.Gui.csproj), e.g. `1.4.1`.
2. Add `release-notes/v<version>.md` (e.g. `release-notes/v1.4.1.md`) -- markdown, written for the
   person receiving the update. This becomes both the GitHub Release body and the text shown in the
   installed app's "what's new" prompt (see Distribution model above), so keep it user-facing, not a
   raw commit log. The workflow fails fast with a clear error if this file is missing.
3. Commit both changes.
4. Tag and push:
   ```
   git tag v1.4.1
   git push --tags
   ```
5. That's it -- [`.github/workflows/release-gui.yml`](../../.github/workflows/release-gui.yml)
   picks up the `v*` tag, builds, packs, and publishes the GitHub Release automatically. Check the
   Actions tab for progress; the release (with `Setup.exe` and the Velopack update assets) shows up
   on the repo's Releases page once it's green.

Every installed copy of the GUI picks up the new version the next time it launches (or is already
running -- the check happens on every `Loaded`, not just at startup).

### Manual (testing before a tag, or if CI is unavailable)

Run from the repo root, on Windows, with the .NET 8 SDK installed:

```
dotnet tool install -g vpk

dotnet publish src/PublishTool.Gui/PublishTool.Gui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=1.4.1 -p:GithubUpdateToken=<optional read-only token, see "Update-check rate limit" above> -o publish/PublishTool.Gui

vpk pack -u PublishTool.Gui -v 1.4.1 -p publish/PublishTool.Gui -e PublishTool.Gui.exe -i src/PublishTool.Gui/Assets/app.ico --releaseNotes release-notes/v1.4.1.md

vpk upload github --repoUrl https://github.com/marhano/lazyly --tag v1.4.1 --publish true --token <a GitHub PAT with repo scope>

copy Releases\PublishTool.Gui-win-Setup.exe Releases\PublishTool.Gui-Setup-v1.4.1.exe
gh release upload v1.4.1 Releases\PublishTool.Gui-Setup-v1.4.1.exe --repo marhano/lazyly
```

Notes:
- `--self-contained true` **without** `-p:PublishSingleFile=true` is required -- Velopack needs
  individual files (not one bundled exe) to compute delta patches between versions. This is
  different from the older single-file publish some builds used before auto-update existed.
- The version passed to `dotnet publish`, `vpk pack`, and the git tag (`v` prefix aside) must all
  match, or installed copies won't recognize the new release as newer.
- `-i`/`--icon` on `vpk pack` is what gives `Setup.exe` an actual icon instead of the generic
  blank-page one Windows shows for an unbranded exe -- without it, `Setup.exe` looks exactly like
  the kind of thing security-conscious people are right to be suspicious of.
- `vpk`'s own `Setup.exe` name is deliberately unversioned (it's meant to always mean "the latest
  installer"), which reads as ambiguous to someone grabbing it by hand from the Releases page --
  the last two commands upload an identically-signed renamed copy alongside it purely for
  clarity. Doesn't affect the update-check machinery, which keys off the versioned `.nupkg`
  assets `vpk pack` already creates, not this file.
- `vpk upload github` needs a token with permission to create releases on this repo when run
  manually. The automated workflow doesn't need this -- it uses the GitHub Actions-provided
  `GITHUB_TOKEN` instead.

## First-time distribution

Before any of the above has ever been run, there's no GitHub Release to point people at yet. Once
the first `vpk pack`/`upload` completes (via a pushed tag or manually), give people the `Setup.exe`
link from that release -- that one-time install is what registers the app for silent self-updates
going forward. Nobody needs to be handed a new build by hand again after that.
