using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotBrain.Protocol;

/// <summary>JSON encoding for the one-off messages (snake_case, as in the spec).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Decode<T>(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<T>(payload, Options) ?? throw new InvalidDataException($"empty {typeof(T).Name}");
}

/// <summary>What a body says about its game (spec section 5).</summary>
public sealed class Manifest
{
    public const int ProtocolVersion = 0;

    public int Protocol { get; set; } = ProtocolVersion;
    public string Title { get; set; } = "";
    public string Build { get; set; } = "";
    public string BodyKind { get; set; } = "server_side";
    public int TickHz { get; set; } = 30;
    public string Truth { get; set; } = "full";
    public string LookOutput { get; set; } = "stick";
    public Dictionary<string, ControlModeSpec> ControlModes { get; set; } = [];
    public TeamsSpec Teams { get; set; } = new();
    public List<ItemSpec> Items { get; set; } = [];
    public List<string> Objectives { get; set; } = [];
    public bool IdentityHidden { get; set; }
    public MapRef Map { get; set; } = new();
    public List<string> Capabilities { get; set; } = [];
}

public sealed class ControlModeSpec
{
    /// <summary>Action names in bit order: the n-th name is bit n of <see cref="ActRecord.Held"/>.</summary>
    public List<string> Actions { get; set; } = [];
    public LookSpec Look { get; set; } = new();
    public MoveSpec Move { get; set; } = new();
}

/// <summary>
/// How stick deflection becomes turn rate: below <see cref="Deadzone"/>
/// nothing; above it, rate = max × ((d − deadzone) / (1 − deadzone)) ^ exponent.
/// </summary>
public sealed class LookSpec
{
    public float MaxTurnDegS { get; set; } = 220;
    public float MaxPitchDegS { get; set; } = 140;
    public float Deadzone { get; set; } = 0.1f;
    public float CurveExponent { get; set; } = 2;
    public float AimScale { get; set; } = 1;
    public bool AimAssist { get; set; }
}

public sealed class MoveSpec
{
    public float WalkMS { get; set; } = 4.2f;
    public float SprintMS { get; set; } = 6.5f;
    public float CrouchMS { get; set; } = 2.0f;
}

public sealed class TeamsSpec
{
    public List<int> Ids { get; set; } = [];
    /// <summary>Pairs of team ids that are hostile to each other.</summary>
    public List<int[]> Hostile { get; set; } = [];
}

public sealed class ItemSpec
{
    public int Id { get; set; }
    public string Class { get; set; } = "";
    public string Fire { get; set; } = "";
    public int Mag { get; set; }
    public float ReloadS { get; set; }
    public float RoundsPerS { get; set; }
    public float[] RangeM { get; set; } = [];
    public float ProjectileMS { get; set; }
    public float BlastM { get; set; }
}

public sealed class MapRef
{
    public string Id { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed class Welcome
{
    public int Protocol { get; set; } = Manifest.ProtocolVersion;
    public int WorldId { get; set; }
    public bool MapLoaded { get; set; }
    public List<string> Accepted { get; set; } = [];
    public List<string> Rejected { get; set; } = [];
}

public sealed class SeatsMessage
{
    public List<SeatInfo> Slots { get; set; } = [];
}

public sealed class SeatInfo
{
    public int Slot { get; set; }
    public int Team { get; set; }
}

public sealed class SeatTake
{
    public int Slot { get; set; }
    public string Name { get; set; } = "";
    public string AccountId { get; set; } = "";
}

public sealed class SeatLeave
{
    public int Slot { get; set; }
}

public sealed class PhaseMessage
{
    public string Phase { get; set; } = "";
    public int? Slot { get; set; }
}

public sealed class ObjectivesMessage
{
    public List<ObjectiveInfo> Objectives { get; set; } = [];
}

public sealed class ObjectiveInfo
{
    public int Id { get; set; }
    public string Primitive { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; }
    /// <summary>Owning team, 255 for none.</summary>
    public int OwnerTeam { get; set; } = 255;
    public float Progress { get; set; }
    public List<int> Requires { get; set; } = [];
    public uint AttachedEntity { get; set; }
    /// <summary>Nobody can work it any more, whoever holds it (MAG locks Sabotage's uplinks once C opens). Added 2026-11-17; absent means false.</summary>
    public bool Locked { get; set; }
}
