# PublishTool.Gui

The WPF desktop app -- see the [root README](../../README.md) for what PublishTool is overall.
This file covers one thing specific to the GUI: how it's packaged, distributed, and kept
up to date on other people's machines.

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

1. Bump `<Version>` in [`PublishTool.Gui.csproj`](PublishTool.Gui.csproj), e.g. `1.2.1`.
2. Add `release-notes/v<version>.md` (e.g. `release-notes/v1.2.1.md`) -- markdown, written for the
   person receiving the update. This becomes both the GitHub Release body and the text shown in the
   installed app's "what's new" prompt (see Distribution model above), so keep it user-facing, not a
   raw commit log. The workflow fails fast with a clear error if this file is missing.
3. Commit both changes.
4. Tag and push:
   ```
   git tag v1.2.1
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

dotnet publish src/PublishTool.Gui/PublishTool.Gui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=1.2.1 -p:GithubUpdateToken=<optional read-only token, see "Update-check rate limit" above> -o publish/PublishTool.Gui

vpk pack -u PublishTool.Gui -v 1.2.1 -p publish/PublishTool.Gui -e PublishTool.Gui.exe --releaseNotes release-notes/v1.2.1.md

vpk upload github --repoUrl https://github.com/marhano/lazyly --tag v1.2.1 --publish true --token <a GitHub PAT with repo scope>
```

Notes:
- `--self-contained true` **without** `-p:PublishSingleFile=true` is required -- Velopack needs
  individual files (not one bundled exe) to compute delta patches between versions. This is
  different from the older single-file publish some builds used before auto-update existed.
- The version passed to `dotnet publish`, `vpk pack`, and the git tag (`v` prefix aside) must all
  match, or installed copies won't recognize the new release as newer.
- `vpk upload github` needs a token with permission to create releases on this repo when run
  manually. The automated workflow doesn't need this -- it uses the GitHub Actions-provided
  `GITHUB_TOKEN` instead.

## First-time distribution

Before any of the above has ever been run, there's no GitHub Release to point people at yet. Once
the first `vpk pack`/`upload` completes (via a pushed tag or manually), give people the `Setup.exe`
link from that release -- that one-time install is what registers the app for silent self-updates
going forward. Nobody needs to be handed a new build by hand again after that.
