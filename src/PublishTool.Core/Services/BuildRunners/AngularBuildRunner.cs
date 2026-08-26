using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Builds an Angular project via its own <c>npm run build</c> script -- deliberately not <c>ng
/// build</c> directly, so this works for any project whose "build" script wraps Angular (or, later,
/// something else entirely) however it wants. Passes an explicit <c>--output-path</c> so the build
/// lands directly in the staging directory, instead of parsing angular.json to find its configured
/// output path.
/// </summary>
public sealed class AngularBuildRunner : IBuildRunner
{
    public ProjectType ProjectType => ProjectType.Angular;

    public string DisplayName => "Angular";

    public async Task<BuildResult> BuildAsync(BuildContext context, CancellationToken ct)
    {
        var project = context.Project;
        var settings = project.Angular
            ?? throw new InvalidOperationException(
                $"'{project.Name}' is registered as an Angular project but has no Angular settings configured.");

        Directory.CreateDirectory(context.StagingDir);

        var args = new List<string> { "run", "build", "--" };
        if (!string.IsNullOrWhiteSpace(settings.WorkspaceProjectName))
        {
            args.Add(settings.WorkspaceProjectName);
        }

        if (!string.IsNullOrWhiteSpace(context.Options.BuildConfiguration))
        {
            args.Add($"--configuration={context.Options.BuildConfiguration}");
        }

        args.Add($"--output-path=\"{context.StagingDir}\"");

        context.Output.Stage("Running npm run build...");
        var exitCode = await ShellCommandRunner.RunAsync(
            "npm " + string.Join(' ', args), settings.ProjectRootPath!, context.Output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"npm run build exited with code {exitCode}. See log output above for details.");
        }

        var contentRoot = ResolveContentRoot(context.StagingDir);
        EnsureSpaWebConfig(contentRoot, context.Output);

        return new BuildResult(BuildArtifactKind.Directory, contentRoot);
    }

    /// <summary>Angular 17+'s default "application" builder always nests the actual static site
    /// under a "browser" subfolder of --output-path (with a sibling "server" folder for the
    /// Node-hosted SSR bundle when prerendering/SSR is configured), even for a plain client-side-only
    /// app -- unlike older Angular (or the legacy "browser" builder), which writes index.html
    /// straight into --output-path with no nesting. Deploying the staging root as-is in the newer
    /// case would put index.html at "hostPath/browser/index.html" instead of "hostPath/index.html",
    /// 404ing the site entirely -- detecting by whether "browser" actually exists (rather than by
    /// Angular version) keeps this correct either way without needing to know which builder a given
    /// project uses. The "server" folder, if present, is never included in what gets archived/
    /// deployed -- it has no purpose in a static IIS site anyway (nothing hosts the Node bundle).</summary>
    private static string ResolveContentRoot(string stagingDir)
    {
        var browserDir = Path.Combine(stagingDir, "browser");
        return Directory.Exists(browserDir) ? browserDir : stagingDir;
    }

    /// <summary>Without a URL Rewrite rule falling back to index.html, IIS 404s any deep link into
    /// an Angular route (e.g. hitting "/dashboard" directly instead of navigating there from "/") --
    /// there's no server-side file at that path for IIS to serve, and Angular's own client-side
    /// router never gets the chance to handle it. Only writes one when the build output doesn't
    /// already have one (e.g. from the Angular project's own angular.json "assets" copying a
    /// custom web.config into dist) -- never overwrites a project-provided file.</summary>
    private static void EnsureSpaWebConfig(string stagingDir, IOutputSink output)
    {
        var webConfigPath = Path.Combine(stagingDir, "web.config");
        if (File.Exists(webConfigPath))
        {
            return;
        }

        output.Info("No web.config found in the build output -- writing a default one with an Angular SPA routing rule.");
        File.WriteAllText(webConfigPath, SpaWebConfigContent);
    }

    // Requires the IIS URL Rewrite module (not something PublishTool can install itself -- a
    // server-side IIS component, see the Dependencies tab for what PublishTool itself checks for).
    // The condition pair only matches requests that aren't an existing real file or directory, so a
    // request for an actual asset (main.js, styles.css, assets/logo.png, ...) is left alone --
    // only an unmatched route falls back to index.html for Angular's router to take over.
    private const string SpaWebConfigContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <system.webServer>
            <rewrite>
              <rules>
                <rule name="Angular Routes" stopProcessing="true">
                  <match url=".*" />
                  <conditions logicalGrouping="MatchAll">
                    <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
                    <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
                  </conditions>
                  <action type="Rewrite" url="/" />
                </rule>
              </rules>
            </rewrite>
          </system.webServer>
        </configuration>
        """;
}
