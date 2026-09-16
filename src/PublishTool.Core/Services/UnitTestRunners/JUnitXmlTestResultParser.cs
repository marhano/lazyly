using System.Xml.Linq;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services.UnitTestRunners;

/// <summary>Parses a JUnit-format XML test report -- the common output format for Karma/Jest JUnit
/// reporters (karma-junit-reporter, jest-junit, etc.), which is what a JS project's own test setup
/// is expected to produce at the fixed path <see cref="JsUnitTestRunner.ResultFileRelativePath"/>.
/// Reads every &lt;testcase&gt; regardless of nesting depth, since some reporters wrap them in a
/// &lt;testsuites&gt; root and others emit a bare &lt;testsuite&gt;.</summary>
public static class JUnitXmlTestResultParser
{
    public static IReadOnlyList<UnitTestCaseResult> Parse(string junitXmlPath)
    {
        var doc = XDocument.Load(junitXmlPath);
        var results = new List<UnitTestCaseResult>();

        foreach (var testCase in doc.Descendants("testcase"))
        {
            var name = testCase.Attribute("name")?.Value ?? "(unnamed test)";
            var className = testCase.Attribute("classname")?.Value;
            var duration = double.TryParse(testCase.Attribute("time")?.Value, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;

            var failure = testCase.Element("failure") ?? testCase.Element("error");
            var skipped = testCase.Element("skipped");

            var outcome = failure is not null ? "Failed" : skipped is not null ? "Skipped" : "Passed";
            var errorMessage = failure?.Attribute("message")?.Value ?? failure?.Value;

            results.Add(new UnitTestCaseResult(name, outcome, duration, className, errorMessage));
        }

        return results;
    }
}
