using System.Text.Json;
using PublishTool.Core.Models;

// ProjectRegistry lives in the PublishTool.Core root namespace, not .Services.
using PublishTool.Core;

namespace PublishTool.Core.Services;

public sealed class ProjectConfigExportFile
{
    public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<ProjectConfig> Projects { get; set; } = new();
}

/// <summary>One project as seen while previewing an import -- whether importing it would create a
/// new project locally or overwrite one that already exists.</summary>
public sealed record ProjectImportPreviewItem(string Name, bool AlreadyExists);

/// <summary>
/// Exports/imports <see cref="ProjectConfig"/> entries to/from a standalone JSON file, so configs
/// can be shared between teammates or machines without hand re-entering every field. Export always
/// strips <see cref="ProjectConfig.EventLogProtectedPassword"/> -- it's DPAPI-protected to the
/// exporting Windows user, so it can never be decrypted by anyone else anyway, and shipping the
/// encrypted blob around would be misleading (looks like a saved password, isn't usable as one).
/// </summary>
public static class ProjectConfigPortability
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Export(IEnumerable<ProjectConfig> projects, string filePath)
    {
        var file = new ProjectConfigExportFile
        {
            Projects = projects.Select(StripSecrets).ToList(),
        };

        File.WriteAllText(filePath, JsonSerializer.Serialize(file, JsonOptions));
    }

    public static ProjectConfigExportFile Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var file = JsonSerializer.Deserialize<ProjectConfigExportFile>(json);
        if (file is null || file.Projects.Count == 0)
        {
            throw new InvalidDataException("That file doesn't contain any PublishTool project configs.");
        }

        return file;
    }

    public static IReadOnlyList<ProjectImportPreviewItem> Preview(ProjectConfigExportFile file, ProjectRegistry registry) =>
        file.Projects
            .Select(p => new ProjectImportPreviewItem(p.Name, registry.Get(p.Name) is not null))
            .ToList();

    /// <summary>Imports only the named projects, leaving everything else in the file untouched.
    /// When a project already exists locally, its <see cref="ProjectConfig.LastReleaseNotesSequence"/>
    /// is preserved rather than taken from the file -- that counter is locally-managed publish
    /// state, not portable config, and overwriting it could duplicate or skip release note
    /// numbers.</summary>
    public static void Import(ProjectConfigExportFile file, ProjectRegistry registry, IEnumerable<string> namesToImport)
    {
        var wanted = new HashSet<string>(namesToImport, StringComparer.OrdinalIgnoreCase);
        foreach (var project in file.Projects.Where(p => wanted.Contains(p.Name)))
        {
            var existing = registry.Get(project.Name);
            if (existing is not null)
            {
                project.LastReleaseNotesSequence = existing.LastReleaseNotesSequence;
            }

            registry.AddOrUpdate(project);
        }
    }

    private static ProjectConfig StripSecrets(ProjectConfig source) => new()
    {
        Name = source.Name,
        ProjectId = source.ProjectId,
        LastReleaseNotesSequence = source.LastReleaseNotesSequence,
        CsprojPath = source.CsprojPath,
        PubxmlName = source.PubxmlName,
        AssemblyInfoPath = source.AssemblyInfoPath,
        IisHostPath = source.IisHostPath,
        ExtraPublishTargets = source.ExtraPublishTargets,
        AutoCreateIisSite = source.AutoCreateIisSite,
        IisBindings = source.IisBindings,
        SdkStyleProject = source.SdkStyleProject,
        ListInHosting = source.ListInHosting,
        UseAppConfig = source.UseAppConfig,
        AppConfigType = source.AppConfigType,
        AppConfigPath = source.AppConfigPath,
        UseEventLog = source.UseEventLog,
        EventLogName = source.EventLogName,
        EventLogFilterType = source.EventLogFilterType,
        EventLogFilterValue = source.EventLogFilterValue,
        EventLogMachineName = source.EventLogMachineName,
        EventLogUsername = source.EventLogUsername,
        EventLogProtectedPassword = null,
    };
}
