using System.Text.RegularExpressions;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services.AppConfig;

/// <summary>Reads/writes key/value pairs inside an Angular-style environment file's exported
/// object literal (<c>export const environment = { production: false, apiUrl: '...' };</c>, with
/// or without a type annotation, e.g. <c>export const environment: Environment = { ... };</c> --
/// not every project has that interface to annotate with). The file can be named anything
/// (environment.ts, environment.prod.ts, environment.beta.ts, or a custom name), it's just picked
/// via the usual "Config file path" field. Applies equally to a Capacitor/Cordova (Android)
/// project, since the environment file lives in the same underlying Angular/web frontend either
/// way. Parsing itself is <see cref="TsObjectLiteral"/> -- this just supplies the "environment"
/// variable name and this format's own file-naming convention.</summary>
public sealed partial class EnvironmentTsProvider : IAppConfigProvider
{
    private const string VariableName = "environment";

    public string TypeName => "EnvironmentTs";

    public string DisplayName => "Angular/Ionic environment file (.ts)";

    public IReadOnlyList<ProjectType> ApplicableProjectTypes => [ProjectType.Angular, ProjectType.Android];

    public Dictionary<string, string> ReadSettings(string configPath) => TsObjectLiteral.Read(configPath, VariableName);

    public void WriteSettings(string configPath, IReadOnlyDictionary<string, string> settings) =>
        TsObjectLiteral.Write(configPath, VariableName, settings);

    public IReadOnlyList<string> FindCandidateConfigPaths(string sourceRoot) =>
        ConfigFileSearch.FindFiles(sourceRoot, name => FileNameRegex().IsMatch(name));

    /// <summary>Derives an Angular <c>ng build --configuration=&lt;value&gt;</c> name from an
    /// environment file's own name, by Angular CLI's own naming convention -- e.g.
    /// <c>environment.prod.ts</c> -&gt; <c>"prod"</c>, <c>environment.beta.ts</c> -&gt; <c>"beta"</c>.
    /// Returns null for the bare <c>environment.ts</c> (the default, no explicit configuration to
    /// pass), or for a name that doesn't fit the convention at all.</summary>
    public static string? InferBuildConfiguration(string configPath)
    {
        var match = FileNameRegex().Match(Path.GetFileName(configPath));
        return match.Success && match.Groups["config"].Success ? match.Groups["config"].Value : null;
    }

    [GeneratedRegex(@"^environment(\.(?<config>[\w-]+))?\.ts$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();
}
