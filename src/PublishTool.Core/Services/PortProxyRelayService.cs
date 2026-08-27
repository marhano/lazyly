using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace PublishTool.Core.Services;

/// <summary>
/// Lets this machine act as a <c>netsh interface portproxy</c> relay -- forwarding a local port to
/// a dev server this machine can already reach, so a colleague who can't reach the dev server
/// directly (but can reach this machine, e.g. over a VPN that only routes client-to-client) can go
/// through it instead. See the "Dev Server Relays" Settings section for the consuming side (saving
/// and switching to someone else's relay URL) -- this is the providing side, run on whichever
/// machine is physically in-office/already connected.
///
/// Listing is a plain unelevated <c>netsh</c> query. Adding/removing a rule needs Administrator, so
/// those run a tiny generated .cmd file elevated via the "runas" shell verb -- a single UAC prompt,
/// no manually opened command prompt.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PortProxyRelayService
{
    private const string FirewallRuleNamePrefix = "PublishTool relay ";

    public sealed record PortProxyRule(int ListenPort, string ConnectAddress, int ConnectPort);

    public static async Task<IReadOnlyList<PortProxyRule>> ListAsync(CancellationToken ct = default)
    {
        var (_, output) = await ProcessRunner.RunCapturedAsync("netsh.exe", "interface portproxy show v4tov4", ct);
        return ParseShowOutput(output);
    }

    internal static IReadOnlyList<PortProxyRule> ParseShowOutput(string output)
    {
        var rules = new List<PortProxyRule>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && int.TryParse(parts[1], out var listenPort)
                && int.TryParse(parts[3], out var connectPort))
            {
                rules.Add(new PortProxyRule(listenPort, parts[2], connectPort));
            }
        }

        return rules;
    }

    /// <summary>Non-loopback IPv4 addresses of this machine, labeled by adapter, so the person
    /// hosting a relay can tell a colleague which one to actually use.</summary>
    public static IReadOnlyList<(string InterfaceName, string Address)> GetLocalIPv4Addresses()
    {
        var results = new List<(string, string)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress_IsLoopback(addr.Address))
                {
                    results.Add((nic.Name, addr.Address.ToString()));
                }
            }
        }

        return results;

        static bool IPAddress_IsLoopback(System.Net.IPAddress address) => System.Net.IPAddress.IsLoopback(address);
    }

    public static Task<(bool Success, string Output)> AddAsync(
        int listenPort, string connectAddress, int connectPort, CancellationToken ct = default)
    {
        var ruleName = FirewallRuleNamePrefix + listenPort;
        var commands = new[]
        {
            $"netsh interface portproxy add v4tov4 listenport={listenPort} listenaddress=0.0.0.0 connectport={connectPort} connectaddress={connectAddress}",
            "if errorlevel 1 exit /b 1",
            $"netsh advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={listenPort}",
        };
        return RunElevatedScriptAsync(commands, ct);
    }

    public static Task<(bool Success, string Output)> RemoveAsync(int listenPort, CancellationToken ct = default)
    {
        var ruleName = FirewallRuleNamePrefix + listenPort;
        var commands = new[]
        {
            $"netsh interface portproxy delete v4tov4 listenport={listenPort} listenaddress=0.0.0.0",
            $"netsh advfirewall firewall delete rule name=\"{ruleName}\"",
        };
        return RunElevatedScriptAsync(commands, ct);
    }

    /// <summary>Adds several rules in one elevated invocation -- a single UAC prompt instead of one
    /// per port, for "relay everything the dev server's IIS is using" in one click.</summary>
    public static Task<(bool Success, string Output)> AddManyAsync(
        IEnumerable<(int ListenPort, string ConnectAddress, int ConnectPort)> rules, CancellationToken ct = default)
    {
        var commands = new List<string>();
        foreach (var (listenPort, connectAddress, connectPort) in rules)
        {
            var ruleName = FirewallRuleNamePrefix + listenPort;
            commands.Add($"netsh interface portproxy add v4tov4 listenport={listenPort} listenaddress=0.0.0.0 connectport={connectPort} connectaddress={connectAddress}");
            commands.Add($"netsh advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={listenPort}");
        }

        return commands.Count == 0 ? Task.FromResult((true, string.Empty)) : RunElevatedScriptAsync(commands, ct);
    }

    /// <summary>Removes several rules in one elevated invocation -- the "stop all" counterpart to
    /// <see cref="AddManyAsync"/>.</summary>
    public static Task<(bool Success, string Output)> RemoveManyAsync(
        IEnumerable<int> listenPorts, CancellationToken ct = default)
    {
        var commands = new List<string>();
        foreach (var listenPort in listenPorts)
        {
            var ruleName = FirewallRuleNamePrefix + listenPort;
            commands.Add($"netsh interface portproxy delete v4tov4 listenport={listenPort} listenaddress=0.0.0.0");
            commands.Add($"netsh advfirewall firewall delete rule name=\"{ruleName}\"");
        }

        return commands.Count == 0 ? Task.FromResult((true, string.Empty)) : RunElevatedScriptAsync(commands, ct);
    }

    private static async Task<(bool Success, string Output)> RunElevatedScriptAsync(
        IEnumerable<string> commands, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"publishtool-relay-{Guid.NewGuid():N}.cmd");
        var logPath = Path.ChangeExtension(scriptPath, ".log");

        var script = "@echo off\r\n"
            + string.Join("\r\n", commands.Select(c => c.StartsWith("if ", StringComparison.OrdinalIgnoreCase) ? c : $"{c} >> \"{logPath}\" 2>&1"))
            + "\r\nexit /b %errorlevel%\r\n";
        await File.WriteAllTextAsync(scriptPath, script, ct);

        var psi = new ProcessStartInfo(scriptPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, "Failed to start the elevated process.");
            }

            await process.WaitForExitAsync(ct);
            var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath, ct) : string.Empty;
            return (process.ExitCode == 0, log.Trim());
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Elevation was cancelled.");
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(logPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best-effort cleanup of a temp file -- not worth failing the operation over.
        }
    }
}
