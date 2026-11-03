namespace Amberline.Simulation.Movement;

/// <summary>
/// Values recovered from the patch-1.0.0.126 client collision routine.
/// The names follow the Python constants and the comments retain the binary
/// source so later data readers can replace only the values they own.
/// </summary>
public sealed record MovementCollisionSettings
{
    // Structural query radii from the client's query code.
    public double HardRadiusMultiplier { get; init; } = 4.0;
    public double HardRadiusMultiplierFlagged { get; init; } = 0.6;
    public double SoftRadiusMultiplier { get; init; } = 2.0;
    public double SoftRadiusMultiplierFlagged { get; init; } = 0.3;

    // Pair gate and branch skins.
    public double SkinRatio { get; init; } = 0.25;
    public double SkinLow { get; init; } = 12.0;
    public double SkinHighPairGate { get; init; } = 17.0;
    public double SkinHighAvoidance { get; init; } = 15.0;
    public double SkinHighCollider { get; init; } = 20.0;
    public double ColliderEpsilonSquared { get; init; } = 144.0;
    public double AvoidMinimumDistanceSquared { get; init; } = 9.765627328306437e-05;

    // Governing and steering constants.
    public double SpeedDeadband { get; init; } = 0.75;
    public double SpeedLowRatio { get; init; } = 0.625;
    public double SpeedHighRatio { get; init; } = 1.375;
    public double SpeedDeadbandAvoidance { get; init; } = 0.25;
    public double SpeedLowRatioAvoidance { get; init; } = 0.875;
    public double SpeedHighRatioAvoidance { get; init; } = 1.125;
    public double SlidePerpendicularDot { get; init; } = 0.0;
    public double SlideHeadOnDot { get; init; } = 0.707;
    public double AvoidSteerWeight { get; init; } = 1.0;
    public double AvoidSteerSignFlip { get; init; } = -1.0;
    public double AvoidMagnitudeHighRatio { get; init; } = 0.4;
    public double SteerMagnitudeLow { get; init; } = 0.01;
    public double SteerScale { get; init; } = 5.1;
    public double ReflectScale { get; init; } = 2.0;
    public double Epsilon { get; init; } = 1e-05;

    // [Collision] client fallbacks. The shipped DATA/CFG/defaults/NSEAI.cfg holds 0.25, 50 and
    // 200 ms for the three below, and the loader (ReadConfigVariables) reads like it would load that file.
    // The real-client contact oracle decides for the fallbacks (batch 248 A/B, movement.client-contact-oracle, 813 frames): with the shipped
    // StuckDelayInMS (200 ms) one capture-F frame's attempts counter no longer matches the client (812 to 811 of 813), with 50 ms it does;
    // ExtraSeparationSpeed 50 and MinSpeedRatioBeforeStuck 0.25 move none of the 813 frames. The three keys come from one file, so the
    // client ran the fallbacks for all three. Open (owner): one log-only read of the three fallback cells on the throwaway client copy.
    public double ExtraSeparationSpeed { get; init; } = 0.1;
    public double MinimumSpeedRatioBeforeStuck { get; init; } = 0.2;
    public double StuckDelaySeconds { get; init; } = 0.050;
    public double TimeBetweenRepathsSeconds { get; init; } = 0.25;
    public double EscapePushCap { get; init; } = 95.0;
    public double EscapePushDistanceMultiplier { get; init; } = 1.5;
    public IReadOnlyList<double> RepathTimings { get; init; } =
        [0.0, 0.1, 0.5, 1.0, 2.0, 2.0, 3.0, 4.0,
         4.0, 4.0, 10.0, 20.0, 20.0, 20.0, 20.0, 20.0];
    public int CanCollideRepathLimit { get; init; } = 45;
    public int CanCollideRepathLimitFlagged { get; init; } = 15;

    public static MovementCollisionSettings Default { get; } = new();
}

public sealed record MovementSettings
{
    public double FixedTickSeconds { get; init; } = 0.1;
    public MovementCollisionSettings Collision { get; init; } = MovementCollisionSettings.Default;
    public double MaximumUnitRadius { get; init; } = 213.75;
    /// <summary>
    /// Movement-only scale for mobile AI bodies. Entity.Radius remains the
    /// shipped gameplay, attack, missile and replication radius.
    /// </summary>
    // Production keeps shipped bodies; explicit historical checks may still
    // select the former reduced movement shell.
    public double NpcMovementRadiusScale { get; init; } = 1.0;

    // Production uses full stored radii plus the native skin. Only explicit
    // historical settings retain the former reduced NPC-pair shell (DEV-03).
    internal double NpcToNpcMovementRadiusScale { get; init; } = 1.0;

    // Historical tuning for lane-minion/champion blocking only; the
    // minion term of a minion/champion pair is scaled. Lane-minion pairs use
    // the unscaled radius (playtest B2 AE5, 26 Sept 2026). The recovered
    // kernel, terrain clearance and gameplay radii stay unchanged.
    internal double MinionUnitCollisionScale { get; init; } = 1.0;

    public void Validate()
    {
        if (!double.IsFinite(FixedTickSeconds) || FixedTickSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(FixedTickSeconds));
        if (!double.IsFinite(MaximumUnitRadius) || MaximumUnitRadius <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(MaximumUnitRadius));
        if (!double.IsFinite(NpcMovementRadiusScale) ||
            NpcMovementRadiusScale <= 0.0 || NpcMovementRadiusScale > 1.0)
            throw new ArgumentOutOfRangeException(nameof(NpcMovementRadiusScale));
        if (!double.IsFinite(NpcToNpcMovementRadiusScale) ||
            NpcToNpcMovementRadiusScale <= 0.0 || NpcToNpcMovementRadiusScale > 1.0)
            throw new ArgumentOutOfRangeException(nameof(NpcToNpcMovementRadiusScale));
        if (!double.IsFinite(MinionUnitCollisionScale) ||
            MinionUnitCollisionScale <= 0.0 || MinionUnitCollisionScale > 1.0)
            throw new ArgumentOutOfRangeException(nameof(MinionUnitCollisionScale));
        if (Collision.RepathTimings.Count != 16)
            throw new ArgumentException("The client repath table contains sixteen entries.", nameof(Collision));
    }
}
