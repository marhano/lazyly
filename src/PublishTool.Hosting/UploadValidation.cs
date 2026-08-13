namespace PublishTool.Hosting;

/// <summary>Path-safety checks shared by every upload path (manual entry and build-files upload)
/// -- project/version names end up as directory segments under BuildsRoot, so both need the same
/// defenses against path traversal and invalid file-name characters.</summary>
internal static class UploadValidation
{
    public static bool IsValidPathSegment(string value) =>
        value.Length > 0 &&
        value != "." &&
        value != ".." &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public static bool IsValidZip(string zipPath)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            return archive.Entries.Count > 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
