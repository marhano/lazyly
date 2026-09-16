using System.Linq;
using System.Xml.Linq;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services.UnitTestRunners;

/// <summary>Parses a Visual Studio Test Results (.trx) file, the format <c>dotnet test --logger
/// trx</c> produces. Deliberately reads only <c>Results/UnitTestResult</c> by local name (ignoring
/// the TRX namespace declaration) -- simpler than binding the exact xmlns URI, and every dotnet test
/// version has used the same element/attribute names regardless of that URI's value.</summary>
public static class TrxTestResultParser
{
    public static IReadOnlyList<UnitTestCaseResult> Parse(string trxPath)
    {
        var doc = XDocument.Load(trxPath);
        var results = new List<UnitTestCaseResult>();

        foreach (var result in doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            var name = result.Attribute("testName")?.Value ?? "(unnamed test)";
            var outcome = result.Attribute("outcome")?.Value ?? "Unknown";
            var duration = TimeSpan.TryParse(result.Attribute("duration")?.Value, out var parsedDuration)
                ? parsedDuration
                : TimeSpan.Zero;

            var errorMessage = result.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;

            results.Add(new UnitTestCaseResult(name, outcome, duration, ClassName: null, errorMessage));
        }

        return results;
    }
}
