using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Where the Settings tab's list of deployment environment names lives -- local to this machine, or
/// shared via the dev server, matching <see cref="IProjectRegistry"/>'s local/remote split and
/// chosen the same way (see <see cref="EnvironmentRegistryFactory"/>).
/// </summary>
public interface IEnvironmentRegistry
{
    Task<EnvironmentSettings> GetAsync(CancellationToken ct = default);

    Task SaveAsync(EnvironmentSettings settings, CancellationToken ct = default);
}
