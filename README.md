# PublishTool

A standalone publisher for classic .NET Framework (4.8+) projects: build via MSBuild, stamp a
version, archive the result as a zip in a shared build repository organized by project name, and
mirror the latest build into an IIS-hosted folder — all from one command or one click, with no
need to open Visual Studio.

Two front ends, one shared pipeline: a CLI for scripting/automation, and a WPF GUI with a form for
common actions plus an embedded command panel that runs the exact same commands as the CLI.

## Status

The publisher (this repo) is done and in real use, including a build-hosting web app
(`PublishTool.Hosting`) where the team browses/downloads archived builds and PublishTool's
GUI/CLI can publish straight to a shared dev server. See
[`src/PublishTool.Hosting/README.md`](src/PublishTool.Hosting/README.md) for how to deploy it.

## Solution layout

```
PublishTool.slnx
src/
  PublishTool.Core/      Registry, settings, and all publish logic (MSBuild, version stamping,
                          zip archiving, IIS mirror via robocopy) — no UI code.
  PublishTool.Commands/  System.CommandLine command tree, shared by the CLI and the GUI's
                          Command tab so both surfaces run identical parsing/handlers.
  PublishTool.Cli/       Console entry point.
  PublishTool.Gui/       WPF app: Publish / Projects / IIS / Event Logs / Command / Settings / Help
                          tabs. Self-updates via Velopack + GitHub Releases -- see its own README.md.
  PublishTool.Hosting/   ASP.NET Core dev server: build archive site + key-protected /api/*
                          surface (upload, shared project registry, remote deploy, remote IIS).
                          See its own README.md for deployment steps.
```

## Requirements

- .NET 8 SDK (or later) to build PublishTool itself.
- Visual Studio or Build Tools with the ASP.NET/web development workload installed, so
  `MSBuild.exe` (auto-discovered via `vswhere`) has the Web Publishing targets that classic
  `dotnet build` doesn't ship.
- Windows (uses `robocopy`, `explorer.exe`, and WinForms interop for folder pickers/notifications).

## Building

```
dotnet build PublishTool.slnx
```

## CLI usage

```
publishtool add-project --name <Name> --csproj <path.csproj> --pubxml <ProfileName>
                         --iis-host <path> [--assembly-info <path>] [--extra-publish-targets <targets>]
publishtool remove-project --name <Name>
publishtool list-projects
publishtool publish --project <Name> --version <Version>
publishtool list-builds [--project <Name>]
publishtool set-builds-root --path <path>
publishtool set-msbuild-path --path <path-to-MSBuild.exe>
```

- `--pubxml` is the publish profile name only (no extension) — e.g. `FolderProfile` for
  `Properties\PublishProfiles\FolderProfile.pubxml`.
- `--version` accepts any string (e.g. `1.0.0.R0001B`). It's used verbatim for the zip filename
  and manifest; only `AssemblyVersion`/`AssemblyFileVersion` (which require strict
  `major.minor.build.revision`) get the leading numeric prefix — the full string still lands in
  `AssemblyInformationalVersion`.
- `--extra-publish-targets` is an escape hatch for projects whose NuGet package `.targets` files
  don't hook into this MSBuild toolset's publish pipeline (e.g. `CollectSQLiteInteropFiles` for
  older `System.Data.SQLite` packages, whose native interop DLL copy is gated on a
  `VisualStudioVersion` whitelist that can predate your installed toolset).

## What `publish` does

1. Stamps the version into `AssemblyInfo.cs`, if `--assembly-info` was registered.
2. Runs `MSBuild.exe /p:DeployOnBuild=true /p:PublishProfile=... /p:PublishUrl=<staging>`.
3. Zips the staged output into `<BuildsRoot>\<ProjectName>\<Version>_<timestamp>.zip`, with a
   sibling `.manifest.json` (project, version, who/when, zip path).
4. Mirrors the staged output into the project's IIS host folder via `robocopy /MIR`, overwriting
   whatever was there before.
5. Shows a Windows notification with the project, version, and a click-through to the zip.

`BuildsRoot` defaults to `%APPDATA%\PublishTool\Builds`; override with `set-builds-root`.
Project registrations live in `%APPDATA%\PublishTool\projects.json`.

## Using the GUI

The GUI is a 7-tab window (Publish / Projects / IIS / Event Logs / Command / Settings / Help) built
around the same registry and commands the CLI uses — anything the GUI does, `publishtool <command>`
can do too (see the Command tab below).

A few things that apply everywhere:

- **Closing the window doesn't quit the app.** The X button hides it to the system tray (so a
  background publish or IIS monitoring keeps running); only the tray icon's **Exit** — or Windows
  shutting down — actually terminates it. Double-click the tray icon to bring the window back.
- **Some actions need Administrator.** Auto-creating IIS sites and starting/stopping/recycling
  sites or app pools require elevation. If you're not elevated, a banner at the top of the window
  says so — right-click the app and **Run as administrator**, then restart it.
- **It updates itself.** On every launch it silently checks GitHub for a newer version; if one's
  found, it downloads in the background (you'll see "Downloading update..." in the status bar) and
  then asks once, with what's new, whether to restart now or later. See
  [`src/PublishTool.Gui/README.md`](src/PublishTool.Gui/README.md) if you're the one shipping
  updates.
- **Local vs. remote mode.** Everything below works purely against files on this machine by
  default. If your team has a [`PublishTool.Hosting`](src/PublishTool.Hosting/README.md) dev
  server set up, turning on "Use dev server for projects" in Settings switches the Projects tab,
  IIS tab, and environment lists over to that server instead — see the Settings section below.

### Publish

Build, version, annotate, and (optionally) deploy one publish of a project.

1. Pick a **Project** from the dropdown — this loads its git branches, existing versions, and (if
   the project has it enabled) its App Config settings.
2. Optionally change **Git branch** — it's pre-filled with the repo's current branch. Publishing
   checks out whatever branch is typed here automatically before building; use the **Checkout**
   button instead if you want to switch (and inspect) the working tree right away, e.g. to preview
   that branch's App Config first. If the branch has uncommitted changes, a dialog offers to
   discard, stash, or commit them before switching.
3. Enter a **Version**, or pick an existing one from the dropdown to republish it (a warning
   appears if you're about to overwrite an existing build).
4. If the project has Local IIS and/or Remote IIS deployment enabled, a **Deploy target** /
   **Deploy to** pair of dropdowns appears — pick Local or Remote, then which named environment
   (e.g. Staging, Production) to deploy to. If neither is enabled for this project, publishing just
   archives (and, in remote mode, uploads) the build with no deploy step.
5. Toggle **Mark as latest release** if this build should replace whichever one is currently
   flagged "latest" on the hosting site (only one build per project can hold that flag).
6. If the project has "Edit user-visible app config" enabled, an **App Config** panel shows the
   live config file's key/value pairs — edit them here and they're written to the config file at
   publish time. Picking an already-published version above shows *that build's* saved config
   instead, for reference.
7. Fill in **Release notes for this build** — four lists (Features and Enhancements, Fixes, Other
   Updates, Backlog Items) that get archived alongside the build and shown on the hosting site.
8. Click **Publish**.

### Projects

Register new projects and manage each one's build history.

- **Add project** / **Edit** / **Remove** on the left manage the registration list itself — see
  [Adding or editing a project](#adding-or-editing-a-project) below for what goes into one.
  Removing a project only unregisters it; archived builds and its IIS host folder are untouched.
- Selecting a project shows its **Build history** on the right — every archived version, when it
  was published, by whom, and whether it's flagged latest/listed on the hosting site. Each row has:
  - **Deploy this version** — redeploys that specific build (asks which environment first, if the
    project has more than one configured).
  - **Mark latest** — flags that build as the project's latest.
  - **Delete** — permanently removes the build's archived zip and release notes.

### IIS

View and control IIS sites and application pools — on this machine, or on the dev server if remote
mode is on (Settings).

- **Sites** sub-tab: **Start site** / **Stop site** / **Browse site** (opens it in your browser).
- **Application Pools** sub-tab: **Start pool** / **Stop pool** / **Recycle pool**.

Starting/stopping/recycling needs Administrator elevation, same as auto-creating sites during a
deploy.

### Event Logs

Browse a per-project slice of the Windows Event Log, for projects that have this turned on (see
[Adding or editing a project](#adding-or-editing-a-project)).

Pick a **Project**, then filter by free-text search, **Level** (Critical/Error/Warning/Info/
Verbose), **Method**, or **Type**. Click a row to see its full message. **Export CSV** saves the
currently-filtered rows.

If the project's event log is configured to read from a specific remote machine (local mode only —
remote/dev-server mode never needs this), you may be prompted once for that machine's credentials;
check "Remember" to save the password (encrypted, this Windows user only) so you're not asked again.

### Command

A single free-text box that runs the exact same commands as the CLI, with output streamed into the
log panel below — useful for anything that doesn't have a dedicated button yet. Press Enter or
click **Run**. See [CLI usage](#cli-usage) above for the full command list; every one of those works
here too, including a couple with no GUI equivalent (`set-msbuild-path`, `list-builds`).

### Settings

- **Builds root** — where archived builds are stored on this machine (`Browse...` / `Save` /
  **Open in Explorer**).
- **Dark mode** — leave it alone to follow your OS theme automatically, or toggle it to pin
  Light/Dark regardless of OS setting.
- **Accent color** — pick a swatch; applying it restarts the app.
- **Start on Windows startup** — launches PublishTool automatically at login, running in the tray.
- **Project configs** — **Export projects...** saves selected registrations to a `.ptproj.json`
  file (e.g. to hand to a teammate); **Import projects...** reads one back in, flagging any name
  collisions with what's already registered here so nothing gets silently overwritten.
- **Remote Build Hosting** — the URL and API key for your team's `PublishTool.Hosting` dev server,
  if you have one (see [its README](src/PublishTool.Hosting/README.md) for how one gets set up).
  **Test connection** checks it works before you rely on it. **Use dev server for projects + IIS
  tab** switches the Projects tab, IIS tab, and environment lists over to that server — turn this
  on once the connection test succeeds.
- **Deployment Environments** — the named environments (e.g. Staging, Production) offered
  everywhere a project's environments are configured. Local to this machine unless remote mode is
  on, in which case they're shared with the whole team.

### Help

Shows the installed version, plus a dependency check (**Recheck**) for MSBuild (required for
Publish) and IIS/`appcmd.exe` (required for local site management). If anything's missing, a
warning pops up automatically the first time you open the app.

### Adding or editing a project

Opened from the Projects tab's **Add project** / **Edit** buttons. Required fields: **Name**,
**.csproj path**, and **Publish profile**.

Most fields here are **local to your machine** — every dev registering the same shared project sees
their own copy. If your team is in remote mode, a handful of fields are instead **shared** (project
ID, publish profile, extra publish targets, SDK-style toggle, hosting listing, app config settings,
Event Log settings, and the Remote IIS environments) — editing those needs an extra confirmation
since it affects everyone on the team, not just you.

- **Name**, **.csproj path**, **AssemblyInfo.cs** (optional, for version stamping).
- **Local IIS** toggle — turn on to deploy this project to IIS sites on *your own* machine. Set a
  host root path, add one or more named environments, and configure each environment's site
  bindings (protocol/IP/port/hostname).
- **Project ID** — a short code used as the release-notes reference prefix (e.g. `BPS` →
  `BPS-2026-0007`).
- **Publish profile** — the `.pubxml` profile name to build with.
- **Modern SDK-style project** — turn on for ASP.NET Core-style projects instead of classic .NET
  Framework Web Deploy projects.
- **List builds in hosting site** — on by default; only affects visibility on the hosting page,
  builds are always archived regardless.
- **Edit user-visible app config from the Publish tab** — turn on to expose a config file's
  key/value pairs on the Publish tab (config type + file path, e.g. `Web.config`).
- **Enable Event Logs tab for this project** — turn on to make this project selectable on the Event
  Logs tab; set the log name, and how entries are matched (by Event Source name, or by a substring
  in the message — useful for apps sharing a generic log via something like NLog), plus an optional
  remote machine/username for local-mode reads.
- **Remote IIS** toggle — same shape as Local IIS, but for deploying to environments on the team's
  dev server; only usable once remote mode is on in Settings.

### Other dialogs you might see

- **Git Conflict** — when checking out a branch with uncommitted changes: discard, stash, commit,
  or (if the checkout wasn't actually blocked) proceed anyway.
- **Environment Picker** — when redeploying a build from the Projects tab and the project has more
  than one environment configured on that side.
- **Update Available** — see "It updates itself" above.
