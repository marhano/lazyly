namespace PublishTool.Core;

/// <summary>
/// The team is in the Philippines, so every timestamp this tool shows a human -- GUI grids, audit
/// trails, the Build Archive web pages -- is explicitly converted to Philippine time, regardless of
/// what timezone the machine doing the displaying (a dev's own PC, or the dev server) happens to be
/// set to. Every persisted timestamp stays UTC (<see cref="DateTimeOffset"/>, <c>*AtUtc</c> naming,
/// sorted/compared as UTC) -- these extensions are a display-time-only conversion, never applied
/// before storing or comparing timestamps.
/// </summary>
public static class PhTime
{
    /// <summary>"Asia/Manila" resolves correctly via .NET's ICU-backed IANA/Windows time zone
    /// mapping (verified directly against a real machine, not assumed) -- (UTC+08:00, no DST).</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");

    public static DateTimeOffset ToPhTime(this DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    public static DateTimeOffset? ToPhTime(this DateTimeOffset? utc) => utc?.ToPhTime();
}
