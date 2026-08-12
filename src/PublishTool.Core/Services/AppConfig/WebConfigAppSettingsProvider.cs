using System.Xml.Linq;

namespace PublishTool.Core.Services.AppConfig;

/// <summary>Reads/writes &lt;appSettings&gt;&lt;add key="..." value="..." /&gt; entries in a
/// classic .NET Framework Web.config.</summary>
public sealed class WebConfigAppSettingsProvider : IAppConfigProvider
{
    public string TypeName => "WebConfigAppSettings";

    public string DisplayName => "Web.config (appSettings)";

    public Dictionary<string, string> ReadSettings(string configPath)
    {
        var doc = XDocument.Load(configPath);
        var appSettings = doc.Root?.Element("appSettings");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (appSettings is null)
        {
            return result;
        }

        foreach (var add in appSettings.Elements("add"))
        {
            var key = (string?)add.Attribute("key");
            if (key is not null)
            {
                result[key] = (string?)add.Attribute("value") ?? string.Empty;
            }
        }

        return result;
    }

    public void WriteSettings(string configPath, IReadOnlyDictionary<string, string> settings)
    {
        // PreserveWhitespace keeps the rest of the file's formatting intact -- this should only
        // touch the specific <add> elements it's updating, not reformat the whole document.
        var doc = XDocument.Load(configPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException($"'{configPath}' has no root element.");

        var appSettings = root.Element("appSettings");
        if (appSettings is null)
        {
            appSettings = new XElement("appSettings");
            root.AddFirst(appSettings);
        }

        foreach (var (key, value) in settings)
        {
            var existing = appSettings.Elements("add")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("key"), key, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.SetAttributeValue("value", value);
            }
            else
            {
                var newElement = new XElement("add", new XAttribute("key", key), new XAttribute("value", value));
                var lastExisting = appSettings.Elements("add").LastOrDefault();

                // Match the indentation already used for sibling <add> elements (by copying the
                // whitespace text node that precedes the last one) instead of appending the new
                // element with no surrounding whitespace, which would land on the same line as
                // the closing </appSettings> tag.
                if (lastExisting?.PreviousNode is XText { Value.Length: > 0 } indent && string.IsNullOrWhiteSpace(indent.Value))
                {
                    lastExisting.AddAfterSelf(new XText(indent.Value), newElement);
                }
                else
                {
                    appSettings.Add(newElement);
                }
            }
        }

        doc.Save(configPath);
    }
}
