using System.Numerics;
using BotBrain.Maps;
using BotBrain.Protocol;

namespace BotBrain.Core;

/// <summary>How much effort an agent gets (spec section 8).</summary>
public enum Tier : byte
{
    Full,
    Reduced,
}

internal enum Goal : byte
{
    None,
    Engage,
    Investigate,
    GoZone,
    Hold,
    /// <summary>Nothing to capture: head for places where a fight is likely.</summary>
    Roam,
    /// <summary>Going after an enemy believed to be at a place: the one who killed it, or gunfire it heard.</summary>
    Hunt,
    /// <summary>Going to, or waiting at, a spot with a long view (see World.Camping.cs).</summary>
    Camp,
    /// <summary>Playing an objective it stands at or works (see World.Objectives.cs): attacking, defending, guarding a teammate at work.</summary>
    Objective,
}

/// <summary>What a player is in the match for (see World.Objectives.cs).</summary>
internal enum Role : byte
{
    /// <summary>Not given one yet: the match has no objectives to play.</summary>
    None,
    /// <summary>Plays for kills and ignores the objectives.</summary>
    Seeker,
    /// <summary>Keeps the team's objectives.</summary>
    Defender,
    /// <summary>Takes the other side's objectives.</summary>
    Attacker,
}

/// <summary>Why an agent is hunting.</summary>
internal enum HuntReason : byte
{
    None,
    /// <summary>Back to where it was killed from.</summary>
    Revenge,
    /// <summary>Toward gunfire heard in the distance.</summary>
    Gunfire,
}

internal enum TrackState : byte
{
    Free,
    /// <summary>In view but not yet registered: the reaction time is running.</summary>
    Noticing,
    Visible,
    /// <summary>Remembered at its last known position.</summary>
    Lost,
}

/// <summary>What an agent believes about one other player.</summary>
internal struct Track
{
    public TrackState State;
    public uint Entity;
    public Vector2 Position, Velocity;
    /// <summary>Height of the ground they stand on.</summary>
    public float Height;
    public double SeenAt, NoticeAt;
    public float Distance;
    public bool Touched;
}

/// <summary>What a skill wants the hands to do. The motor model decides how.</summary>
internal struct Intent
{
    public bool HasLook;
    public Vector3 LookPoint;
    /// <summary>How fast the look point is sweeping across the view, rad/s, for anticipation.</summary>
    public float LookYawRate;
    public Vector2 Move;
    public float Speed;
    public bool Fire, Sprint, Reload, Interact;
}

internal sealed class Agent(int slot, byte team, Persona persona)
{
    public readonly int Slot = slot;
    public readonly byte Team = team;
    public readonly Persona P = persona;
    public Rng Rng = new(persona.Seed ^ 0xA5A5A5A5UL);
    public int Squad;

    // Body state, from OBSERVE.
    public uint EntityId;
    public int EntityIndex = -1;
    public bool Alive;
    public int Ammo, Reserve;

    public Tier Tier = Tier.Full;
    public double TierHoldUntil;

    // Belief.
    public readonly Track[] Tracks = new Track[World.MaxTracks];

    // Goal.
    public Goal Goal;
    public int TargetTrack = -1;
    public uint TargetEntity;
    public bool NeedsDecision = true;
    public int Zone = -1;
    public int RoamTarget = -1;

    // Hunting a place (see World.Roaming.cs).
    public HuntReason Hunt;
    public Vector2 HuntPoint, HuntVia;
    /// <summary>The floor heights of the hunt's place and of its way round.</summary>
    public float HuntZ, HuntViaZ;
    public bool HasHuntVia;
    public double HuntStarted, HuntUntil;
    public FlowField? HuntFlow;
    public double NextHearAt;

    // Camping (see World.Camping.cs).
    public int CampSpot = -1, CampAvoid = -1;
    public bool AtCamp, CampStaySet;
    public double CampUntil, CampLookUntil, CampCooldownUntil, CampAvoidUntil;
    public float CampWatch, CampLook;

    // Objectives (see World.Objectives.cs).
    public Role Role;
    /// <summary>The objective it is playing, an index into the world's sites; -1 for none.</summary>
    public int Site = -1;
    public double SiteReviewAt = double.MaxValue, SiteRecheckAt;
    public bool Interacting;
    public double InteractSince, InteractCooldownUntil;
    public Vector2 PostPoint;
    public bool HasPost;
    public double PostUntil;
    public float PostLook;

    // Who hurt it last, to know its killer when it dies.
    public uint LastAttacker;
    public double LastAttackedAt;
    public bool RevengePending;
    public Vector2 RevengePoint;
    public float RevengeZ;

    // Navigation.
    public FlowField? Flow;
    /// <summary>A searched route's waypoints, with the height of the floor at each.</summary>
    public readonly List<Vector3> Path = [];
    public int PathIndex;
    public Vector2 PathGoal;
    public Vector2 Steer;
    public bool HasSteer;
    public double SteerRefreshAt;
    public Vector2 StuckReference;
    public double StuckCheckAt;
    /// <summary>Stuck checks failed in a row.</summary>
    public int StuckRun;
    public double SidestepUntil;
    public float SidestepDirection;

    // Skill state.
    public Vector2 HoldPoint;
    public bool HasHoldPoint;
    public double HoldUntil;
    public float ScanYaw;
    public double ScanUntil;
    public float StrafeDirection;
    public double StrafeUntil;
    public double BurstEnd, PauseUntil;
    /// <summary>Where the agent believes its target's middle is, off the truth, radians (right, up); see Persona.AimBias.</summary>
    public Vector2 AimBias;
    public uint AimBiasEntity;
    public double AimBiasAt;
    public float GlanceOffset;
    public double GlanceUntil, NextGlanceAt;

    public Intent Intent;

    // Motor state.
    public Vector2 LookStick, LookVelocity, LookTarget, MoveStick;
    public double NextPlanAt;
    public bool FireWanted, FireHeld;
    public double FireChangeAt;
    public bool SentNeutral;
    /// <summary>The controls last sent for this agent.</summary>
    public ActRecord Sent;

    public void ResetForSpawn()
    {
        Array.Clear(Tracks);
        Goal = Goal.None;
        TargetTrack = -1;
        NeedsDecision = true;
        Path.Clear();
        HasSteer = false;
        HasHoldPoint = false;
        LookStick = LookVelocity = LookTarget = MoveStick = default;
        FireWanted = FireHeld = false;
        BurstEnd = PauseUntil = 0;
        SentNeutral = false;
        Sent = default;
        StuckCheckAt = 0;
        Hunt = HuntReason.None;
        HasHuntVia = false;
        HuntFlow = null;
        LastAttacker = 0;
        Interacting = false;
        HasPost = false;
    }
}
