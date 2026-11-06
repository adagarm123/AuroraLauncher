namespace AuroraCore;

/// <summary>Build identity constants. Bump <see cref="CurrentRound"/> on
/// every release — the doc-baseline guard verifies it matches the round
/// markers in README / ROADMAP / TEST_RESULTS, and the update service uses
/// it for downgrade rejection (only strictly higher rounds are updates).</summary>
public static class BuildInfo
{
    public const int CurrentRound = 61;
    public const string Version = "0.61.0";
}
