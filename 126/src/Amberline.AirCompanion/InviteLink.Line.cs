// The 1.0.0.126 line's values for InviteLink (the shared part is host/src/Amberline.AirCompanion/InviteLink.cs). The 1.0.0.132 line has its
// own copy at this path. Linked as source into the Platform as well (126/src/Amberline.Platform/Amberline.Platform.csproj).
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>NOT RETAIL (batch 917): this line in the invite format (b917-918 invite spec).</summary>
internal static partial class InviteLink
{
    /// <summary>The invite's <c>line</c>.</summary>
    internal const string Line = "126";

    /// <summary>This line's release tags (patch.json releaseTagPrefix, PlatformLine.ReleaseTagPrefix): v0.1.31.</summary>
    internal const string TagPrefix = "v";

    /// <summary>The per-user link scheme the Join page's button opens (HKCU\Software\Classes\amberline-player).</summary>
    internal const string Scheme = "amberline-player";

    /// <summary>
    /// NOT RETAIL (batch 917 follow-up, orchestrator): the line's first release that carries the player app. The invite's tag only picks
    /// which release's player app the Join page downloads (on Yes the app downloads the host's own player tool anyway), so a host without a
    /// release version (a development build, the dev previews) or with an older one (a guide build from a checkout is 0.0.N) names this
    /// release instead (InviteLink.Tag).
    /// </summary>
    internal const string FirstPlayerRelease = "0.1.31";
}
