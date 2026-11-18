namespace BotBrain.Core;

/// <summary>Small, fast, seedable random source. A struct: keep it in a field, not a property.</summary>
public struct Rng(ulong seed)
{
    private ulong _state = seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;

    public ulong NextU64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public float NextFloat() => (NextU64() >> 40) * (1f / (1 << 24));

    public float Range(float low, float high) => low + (high - low) * NextFloat();

    public int NextInt(int exclusiveMax) => (int)(NextU64() % (ulong)exclusiveMax);

    public float Gaussian()
    {
        float u = MathF.Max(NextFloat(), 1e-7f), v = NextFloat();
        return MathF.Sqrt(-2 * MathF.Log(u)) * MathF.Cos(2 * MathF.PI * v);
    }

    public float LogNormal(float median, float sigma) => median * MathF.Exp(sigma * Gaussian());
}

/// <summary>
/// A persistent bot identity: who it is and how it plays. Everything here is
/// game-independent; the same persona can be seated in any title.
/// </summary>
public sealed class Persona
{
    private static readonly string[] First =
    [
        "Shadow", "Toxic", "Silent", "Dark", "Mad", "Lucky", "Cyber", "Rogue", "Iron", "Ghost", "Frosty", "Crazy",
        "Sneaky", "Lazy", "Angry", "Salty", "Epic", "Rusty", "Turbo", "Grim", "Neon", "Hyper", "Dusty", "Lone",
    ];

    private static readonly string[] Second =
    [
        "Sniper", "Wolf", "Reaper", "Ninja", "Panda", "Killer", "Hawk", "Viper", "Pirate", "Wizard", "Tiger",
        "Bandit", "Noob", "Legend", "Camper", "Monkey", "Raptor", "Soldier", "Dragon", "Taco", "Hunter", "Mango",
    ];

    public ulong Seed { get; private init; }
    public string Name { get; private init; } = "";

    /// <summary>Overall ability, roughly -2 (poor) to +2 (very good).</summary>
    public float Skill { get; private init; }

    /// <summary>Median time to notice something new in view, seconds.</summary>
    public float Reaction { get; private init; }

    /// <summary>How often the aim is re-planned, seconds. Between plans the thumb carries on as it was.</summary>
    public float PlanInterval { get; private init; }

    /// <summary>Time the hand aims to close an aiming error in, seconds. Lower is more forceful.</summary>
    public float AimTimeConstant { get; private init; }

    /// <summary>Error in judging how far to turn, as a fraction of the turn.</summary>
    public float AimNoise { get; private init; }

    /// <summary>Constant small aiming error, radians.</summary>
    public float AimJitter { get; private init; }

    /// <summary>Thumb unsteadiness, in stick units.</summary>
    public float Tremor { get; private init; }

    /// <summary>How quickly the thumb reaches where it is going, rad/s.</summary>
    public float ThumbSpeed { get; private init; }

    /// <summary>How much of a moving target's motion is anticipated, 0 to 1.</summary>
    public float Pursuit { get; private init; }

    /// <summary>How far off target the crosshair may be when the trigger is pulled; 1 is on the body.</summary>
    public float TriggerSlop { get; private init; }

    public float Aggression { get; private init; }
    public float Obedience { get; private init; }
    public float StrafeHabit { get; private init; }
    public float BurstDiscipline { get; private init; }
    public float ScanHabit { get; private init; }
    public float MemorySeconds { get; private init; }
    public float FovHalfAngle { get; private init; }
    public float PreferredRange { get; private init; }

    /// <summary>Chance, after being killed, of going back for whoever did it.</summary>
    public float Grudge { get; private init; }

    /// <summary>Chance of heading straight for gunfire heard in the distance.</summary>
    public float Rush { get; private init; }

    /// <summary>Chance of coming at a known fight from the side rather than head on.</summary>
    public float Flank { get; private init; }

    /// <summary>Where it likes to roam, from 0 (near its own spawn) to 1 (the enemy's). Middle of the map is 0.5.</summary>
    public float FrontLine { get; private init; }

    /// <summary>
    /// How far away a person is still easy to pick out, metres: at this
    /// distance noticing takes twice as long as up close, at twice it five
    /// times. Eyesight, screen and attention together.
    /// </summary>
    public float SpotDistance { get; private init; }

    /// <summary>Chance of settling at a vantage point and waiting there instead of roaming on.</summary>
    public float Camper { get; private init; }

    /// <summary>
    /// How far off, radians (one sigma, each way), the player's idea is of
    /// where a target's middle is. It stays off until the player sees the
    /// shots land and walks them in; a small, far figure makes it count.
    /// </summary>
    public float AimBias { get; private init; }

    /// <summary>
    /// The trigger is pulled once the crosshair is this close, radians, even
    /// if the target is smaller than that: people do not wait for a perfect
    /// line-up on someone far away.
    /// </summary>
    public float TriggerFloor { get; private init; }

    /// <summary>Beyond this, metres, the player does not open fire (moves closer instead) unless camping or being shot.</summary>
    public float HoldFireRange { get; private init; }

    /// <summary>
    /// When the objective it plays is lost (or won), the chance it goes to
    /// the team's other objective of the same kind rather than simply the
    /// nearest objective there is to play (World.Objectives.cs).
    /// </summary>
    public float Rotate { get; private init; }

    public static Persona Generate(ulong seed)
    {
        Rng rng = new(seed);
        float skill = Math.Clamp(rng.Gaussian(), -2f, 2f);
        string name = First[rng.NextInt(First.Length)] + Second[rng.NextInt(Second.Length)];
        name = rng.NextInt(3) switch
        {
            0 => name + rng.NextInt(100),
            1 => "xX" + name + "Xx",
            _ => name,
        };
        // Traits added later come from their own stream, so the ones above stay the same for a seed.
        Rng style = new(seed ^ 0x5157A1E5UL);
        float aggression = rng.Range(0.2f, 0.9f);
        // About one player in three is a flanker; flankers rarely run straight at gunfire.
        float flank = style.NextFloat() < 0.3f ? style.Range(0.5f, 0.9f) : style.Range(0f, 0.2f);
        return new Persona
        {
            Seed = seed,
            Name = name,
            Skill = skill,
            Reaction = 0.26f - 0.035f * skill,
            PlanInterval = 0.12f - 0.015f * skill,
            AimTimeConstant = 0.20f - 0.03f * skill,
            AimNoise = 0.18f - 0.05f * skill,
            AimJitter = 0.004f - 0.001f * skill,
            Tremor = 0.015f - 0.004f * skill,
            ThumbSpeed = 26f + 3f * skill,
            Pursuit = 0.6f + 0.12f * skill,
            TriggerSlop = 1.6f - 0.3f * skill,
            Aggression = aggression,
            Obedience = rng.Range(0.3f, 1f),
            StrafeHabit = Math.Clamp(rng.Range(0.3f, 1f) + 0.1f * skill, 0f, 1f),
            BurstDiscipline = rng.NextFloat(),
            ScanHabit = rng.Range(0.3f, 1f),
            MemorySeconds = rng.Range(6f, 14f),
            // What the screen shows plus a little: people do not notice much at its edges anyway.
            FovHalfAngle = 0.7f + rng.Range(-0.08f, 0.08f),
            PreferredRange = rng.Range(12f, 35f),
            Grudge = Math.Clamp(style.Range(0.05f, 0.7f) + 0.2f * aggression, 0f, 1f),
            Rush = Math.Clamp(style.Range(0.1f, 0.6f) + 0.4f * aggression - 0.5f * flank, 0f, 1f),
            Flank = flank,
            FrontLine = Math.Clamp(0.3f + 0.4f * aggression + style.Range(-0.08f, 0.08f), 0.2f, 0.8f),
            SpotDistance = Math.Clamp(45f + 7f * skill + style.Range(-7f, 7f), 26f, 64f),
            // About one player in four camps; campers are rarely the most aggressive.
            Camper = style.NextFloat() < 0.25f + 0.15f * (0.55f - aggression) ? style.Range(0.6f, 0.95f) : style.Range(0f, 0.1f),
            AimBias = Math.Clamp(0.007f - 0.0015f * skill + style.Range(-0.0012f, 0.0012f), 0.003f, 0.011f),
            TriggerFloor = Math.Clamp(0.012f - 0.002f * skill + style.Range(-0.002f, 0.002f), 0.006f, 0.018f),
            HoldFireRange = style.Range(55f, 110f),
            Rotate = style.Range(0.3f, 0.7f),
        };
    }
}
