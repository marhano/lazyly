namespace PublishTool.Core.Models;

/// <summary>PATCH body for the Remote Build Hosting API's update endpoint. Null fields are left
/// unchanged -- only non-null ones are applied, so a client can flip just one flag at a time.</summary>
public sealed class UpdateBuildRequest
{
    public bool? ListInHosting { get; set; }

    public bool? IsLatest { get; set; }
}
