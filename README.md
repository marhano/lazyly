# PublishTool

A standalone publisher for .NET, Angular, and hybrid-mobile (Capacitor/Cordova) Android projects:
build, stamp a version, archive the result in a shared build repository organized by project name,
and — for .NET/Angular — mirror the latest build into an IIS-hosted folder, all from one command or
one click, with no need to open Visual Studio. Android builds skip IIS entirely (there's no
"deploy" equivalent for an installable APK/AAB) and just land on the build-hosting site as a
download.

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
  PublishTool.Gui/       WPF app: Publish / Projects / IIS / Firewall / Event Logs / Audit Logs /
                          Command / Settings / Help tabs. Self-updates via Velopack + GitHub
                          Releases -- see its own README.md.
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
                         [--assembly-info <path>] [--extra-publish-targets <targets>]
publishtool add-project --name <Name> --project-type angular --project-root <path> [--build-configuration <config>]
publishtool add-project --name <Name> --project-type android --project-root <path> [--artifact-type apk|aab]
publishtool remove-project --name <Name>
publishtool list-projects
publishtool publish --project <Name> --version <Version>
publishtool list-builds [--project <Name>]
publishtool set-builds-root --path <path>
publishtool set-msbuild-path --path <path-to-MSBuild.exe>
```

- `--project-type` picks which of the other options apply and how `publish` builds the project:
  `dotnet` (the default — `--pubxml` is required), `angular`, or `android` (both require
  `--project-root`; PublishTool auto-detects Capacitor vs. Cordova from what's in that folder). Run
  `publishtool add-project --help` for the full per-type option list.
- `--pubxml` (dotnet only) is the publish profile name only (no extension) — e.g. `FolderProfile`
  for `Properties\PublishProfiles\FolderProfile.pubxml`.
- `--version` accepts any string (e.g. `1.0.0.R0001B`). It's used verbatim for the zip filename
  and manifest; only `AssemblyVersion`/`AssemblyFileVersion` (which require strict
  `major.minor.build.revision`) get the leading numeric prefix — the full string still lands in
  `AssemblyInformationalVersion`.
- `--extra-publish-targets` is an escape hatch for projects whose NuGet package `.targets` files
  don't hook into this MSBuild toolset's publish pipeline (e.g. `CollectSQLiteInteropFiles` for
  older `System.Data.SQLite` packages, whose native interop DLL copy is gated on a
  `VisualStudioVersion` whitelist that can predate your installed toolset).

## What `publish` does

The build step is the one part that differs by project type; archiving, release notes, and
uploading/deploying afterward are identical regardless.

**.NET** (`--project-type dotnet`, the default):
1. Stamps the version into `AssemblyInfo.cs`, if `--assembly-info` was registered.
2. Runs `MSBuild.exe /p:DeployOnBuild=true /p:PublishProfile=... /p:PublishUrl=<staging>`.

**Angular** (`--project-type angular`): runs `npm run build -- --configuration=<config>
--output-path=<staging>` in the project root.

**Android** (`--project-type android`): auto-detects Capacitor or Cordova in the project root, then
either (Capacitor) `npm run build`, `npx cap sync android`, and `gradlew assembleRelease`/
`bundleRelease`, or (Cordova) `ionic cordova build android` / `cordova build android` — producing a
single `.apk`/`.aab` instead of a folder.

Then, for every type:
3. Zips the staged output (or, for Android, copies the `.apk`/`.aab` as-is) into
   `<BuildsRoot>\<ProjectName>\<Version>_<timestamp>.zip` (or `.apk`/`.aab`), with a sibling
   `.manifest.json` (project, version, who/when, artifact path).
4. .NET/Angular only: mirrors the staged output into the project's IIS host folder via
   `robocopy /MIR`, overwriting whatever was there before. Android has no deploy step — the built
   file is only ever downloaded from the build-hosting site.
5. Shows a Windows notification with the project, version, and a click-through to the artifact.

`BuildsRoot` defaults to `%APPDATA%\PublishTool\Builds`; override with `set-builds-root`.
Project registrations live in `%APPDATA%\PublishTool\projects.json`.

## Using the GUI

The GUI is a 9-tab window (Publish / Projects / IIS / Firewall / Event Logs / Audit Logs / Command /
Settings / Help) built around the same registry and commands the CLI uses — anything the GUI does,
`publishtool <command>` can do too (see the Command tab). Administrator is only ever needed for
managing *this machine's own* IIS/Firewall; the same actions against a team dev server (remote
mode) don't need it. It self-updates via GitHub Releases, and only one copy runs at a time.

**For the full guide — getting started, setting up remote hosting and deployment environments, and
every tab's features — see [`src/PublishTool.Gui/README.md`](src/PublishTool.Gui/README.md).**
