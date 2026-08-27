namespace PublishTool.Core.Models;

/// <summary>Allow-listed IIS application pool identities PublishTool can set. Deliberately limited
/// to IIS's own built-in service accounts -- no "SpecificUser" (arbitrary domain/local account +
/// password) option, since accepting an arbitrary identity/credential over the remote API would let
/// anyone holding the dev server's API key grant a site any Windows account's privileges, not just
/// pick among IIS's own fixed, already-scoped built-in identities.</summary>
public enum AppPoolIdentityType
{
    ApplicationPoolIdentity,
    NetworkService,
    LocalService,
    LocalSystem,
}
