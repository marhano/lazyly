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
  PublishTool.Gui/       WPF app: Publish / Projects / Settings / IIS / Event Logs / Command tabs.
                          Self-updates via Velopack + GitHub Releases -- see its own README.md.
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

## GUI

- **Publish** — pick a registered project, enter a version, publish. Shows a live status label
  and indeterminate progress bar while running.
- **Add Project** — form for registering/editing projects, with file/folder browse buttons and a
  list of existing registrations to click-to-edit.
- **Settings** — set the builds root, with a button to open it directly in Explorer.
- **Command** — free-text box that runs any CLI command through the same parser, with output
  streamed to the shared log pane below.
