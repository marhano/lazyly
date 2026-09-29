namespace PublishTool.Core.Models;

/// <summary>The closed set of test suite kinds PublishTool knows how to run and give first-class
/// treatment to (an E2E suite gets an environment picker; a Unit Test suite doesn't). Unlike the
/// original free-text suite "Name", this is a real enum -- the whole point of the redesign that
/// introduced it was "pick a TYPE, not type a name", so a project can have at most one suite per
/// type (enforced where suites are added, not by this enum itself).</summary>
public enum TestSuiteType
{
    UnitTest = 0,
    E2E = 1,
}

/// <summary>Single source of truth for how a <see cref="TestSuiteType"/> is displayed and matched by
/// name everywhere downstream (test suite names threaded through <see cref="TestSuiteConfig"/>,
/// <see cref="TestRunStatus"/>, <see cref="TestBundleSuiteEntry"/>, etc. all still key off a plain
/// string -- this is the one place that string is decided, so every layer below it never needed to
/// learn about the enum at all).</summary>
public static class TestSuiteTypeNames
{
    public static string DisplayName(TestSuiteType type) => type switch
    {
        TestSuiteType.UnitTest => "Unit Test",
        TestSuiteType.E2E => "E2E",
        _ => type.ToString(),
    };

    /// <summary>Reverses <see cref="DisplayName"/> -- needed wherever only the display string
    /// survived (e.g. <see cref="TestBundleSuiteEntry.Name"/>, read back off a build's manifest with
    /// no direct reference to the enum value that produced it).</summary>
    public static bool TryParseDisplayName(string displayName, out TestSuiteType type)
    {
        foreach (var candidate in Enum.GetValues<TestSuiteType>())
        {
            if (string.Equals(DisplayName(candidate), displayName, StringComparison.Ordinal))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }
}
