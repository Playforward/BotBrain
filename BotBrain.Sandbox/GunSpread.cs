using System.Numerics;

namespace BotBrain.Sandbox;

/// <summary>
/// The stand-in game's rifle: shots leave within a cone that widens with each
/// shot and while moving, and closes again when the player stops and lets
/// go. The numbers are MAG's M4A1 standing (weapons.lua reticule_params_stand
/// and aim_camera_reticule_modifier), read the same way the MAG body reads
/// them, so the sandbox's hit rates are comparable. How MAG itself uses these
/// fields was not read from its code: see the MAG body's WeaponSpread [guess].
/// </summary>
public sealed class GunSpread
{
    /// <summary>Full width of the cone at rest and fully spread, degrees.</summary>
    public float ConeMin = 1.6f, ConeMax = 2.6f;
    /// <summary>Spread added by one shot, and the least spread while moving at full speed (0 to 1).</summary>
    public float PerShot = 0.1f, Moving = 0.3f;
    /// <summary>How fast spread falls back (per second), and how fast it rises to the moving level.</summary>
    public float Recover = 3f, MoveRecover = 3f;
    /// <summary>The cone is this much narrower when aiming down the sight (players fire that way).</summary>
    public float AimScale = 0.8f;

    public static readonly GunSpread Rifle = new();

    /// <summary>Moves the spread on by one step: toward the moving level, or back down.</summary>
    public float Settle(float spread, float speedFraction, float dt)
    {
        float floor = Moving * Math.Clamp(speedFraction, 0f, 1f);
        return spread > floor ? MathF.Max(floor, spread - Recover * dt) : MathF.Min(floor, spread + MoveRecover * dt);
    }

    /// <summary>Half the cone's width now, radians.</summary>
    public float HalfAngle(float spread) =>
        0.5f * (ConeMin + (ConeMax - ConeMin) * Math.Clamp(spread, 0f, 1f)) * AimScale * MathF.PI / 180f;

    /// <summary>A shot's direction off the aim, (yaw, pitch) radians: anywhere in the cone, evenly.</summary>
    public Vector2 Scatter(float spread, float u, float v)
    {
        float radius = HalfAngle(spread) * MathF.Sqrt(u);
        float angle = 2 * MathF.PI * v;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
    }
}
