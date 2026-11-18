using System.Numerics;
using BotBrain.Protocol;

namespace BotBrain.Core;

public sealed partial class World
{
    /// <summary>Turns the current goal into what the hands should be doing this moment.</summary>
    private void RunSkill(Agent a, float dt)
    {
        ref EntityRecord me = ref _entities[a.EntityIndex];
        Vector2 position = new(me.X, me.Y);
        a.Intent = default;
        if (a.Goal != Goal.Objective)
            a.Interacting = false;
        switch (a.Goal)
        {
            case Goal.Engage:
                Engage(a, ref me, position);
                break;
            case Goal.Investigate:
            case Goal.GoZone:
            case Goal.Roam:
            case Goal.Hunt:
                Travel(a, ref me, position);
                break;
            case Goal.Hold:
                Hold(a, ref me, position);
                break;
            case Goal.Camp:
                Camp(a, ref me, position);
                break;
            case Goal.Objective:
                PlaySite(a, ref me, position);
                break;
            default:
                LookAround(a, ref me, position);
                break;
        }
        // Topping up the magazine when nothing is happening.
        if (a.Goal != Goal.Engage && !a.Intent.Interact && a.Ammo < 18 && a.Reserve > 0)
            a.Intent.Reload = true;
    }

    /// <summary>Beyond this, metres, bursts are a few rounds with time to re-aim between.</summary>
    private const float LongRange = 40f;
    /// <summary>How quickly a held aiming error is walked in while firing, seconds (to about a third).</summary>
    private const float AimWalkInSeconds = 1.5f;

    private void Engage(Agent a, ref EntityRecord me, Vector2 position)
    {
        if (a.TargetTrack < 0 || a.Tracks[a.TargetTrack].Entity != a.TargetEntity)
        {
            a.NeedsDecision = true;
            return;
        }
        ref Track track = ref a.Tracks[a.TargetTrack];
        if (track.State != TrackState.Visible)
        {
            // Lost sight: keep the crosshair where they were until a new decision lands.
            a.Intent.HasLook = true;
            a.Intent.LookPoint = new Vector3(track.Position, track.Height + ChestHeight);
            a.NeedsDecision = true;
            return;
        }

        // Aim where they are believed to be now, not where they were last seen.
        Vector2 target = track.Position + track.Velocity * (float)(_now - track.SeenAt);
        Vector2 to = target - position;
        float distance = MathF.Max(0.5f, to.Length());
        Vector2 forward = to / distance;
        Vector2 right = new(forward.Y, -forward.X);
        Vector2 relative = track.Velocity - new Vector2(me.Vx, me.Vy);
        // Where the agent believes the middle of them is: off by a little that
        // stays put (an angle, so it matters on a small, far figure) and is
        // walked in while the shots go out. Drawn anew for a new target, or
        // after a while away from this one.
        if (a.AimBiasEntity != a.TargetEntity || _now - a.AimBiasAt > 2)
        {
            a.AimBiasEntity = a.TargetEntity;
            a.AimBias = new Vector2(a.Rng.Gaussian(), a.Rng.Gaussian()) * a.P.AimBias;
        }
        else if (a.FireHeld)
            a.AimBias *= MathF.Exp(-(float)(_now - a.AimBiasAt) / AimWalkInSeconds);
        a.AimBiasAt = _now;
        Vector2 aimAt = target + right * (a.AimBias.X * distance);
        float aimHeight = track.Height + ChestHeight + a.AimBias.Y * distance;
        a.Intent.HasLook = true;
        a.Intent.LookPoint = new Vector3(aimAt, aimHeight);
        a.Intent.LookYawRate = (to.X * relative.Y - to.Y * relative.X) / (distance * distance);

        // On target is judged against that belief, and far away "close enough" is an angle, not the body.
        Vector2 toAim = aimAt - position;
        float yawError = WrapAngle(MathF.Atan2(toAim.Y, toAim.X) - me.Yaw);
        float pitchError = MathF.Atan2(aimHeight - (me.Z + EyeHeight), distance) - me.Pitch;
        bool onTarget = MathF.Abs(yawError) < MathF.Max(MathF.Atan2(0.45f, distance) * a.P.TriggerSlop, a.P.TriggerFloor)
            && MathF.Abs(pitchError) < MathF.Max(MathF.Atan2(0.7f, distance) * a.P.TriggerSlop, a.P.TriggerFloor);
        // A camper fights from its spot: no closing in, and only the odd sidestep.
        bool camping = Camping(a, position);
        // Too far to bother: close in first, unless dug in or already under fire.
        bool hold = distance > a.P.HoldFireRange && !camping && _now - a.LastAttackedAt > 4;
        bool fire = onTarget && a.Ammo > 0 && !hold;
        if (fire)
        {
            // Held trigger up close, bursts further out, a few rounds at a time far away.
            if (a.BurstEnd == 0)
                a.BurstEnd = _now + (distance < 15f ? 1.5f
                    : distance < LongRange ? 0.8f - 0.5f * a.P.BurstDiscipline
                    : 0.12f + 0.25f * (1 - a.P.BurstDiscipline));
            else if (_now >= a.BurstEnd)
            {
                a.PauseUntil = _now + (distance < LongRange ? a.Rng.Range(0.12f, 0.35f) : a.Rng.Range(0.3f, 0.7f));
                a.BurstEnd = 0;
            }
            fire = _now >= a.PauseUntil;
        }
        a.Intent.Fire = fire;
        a.Intent.Reload = a.Ammo == 0;

        if (_now >= a.StrafeUntil)
        {
            float habit = camping ? a.P.StrafeHabit * 0.3f : a.P.StrafeHabit;
            a.StrafeDirection = a.Rng.NextFloat() > habit ? 0 : a.Rng.NextFloat() < 0.5f ? -1 : 1;
            a.StrafeUntil = _now + a.Rng.Range(0.4f, 1.4f);
        }
        if (a.StrafeDirection != 0 && !_map.OnFloor(new Vector3(position + right * a.StrafeDirection * 1.2f, me.Z)))
            a.StrafeDirection = -a.StrafeDirection;
        Vector2 move = right * a.StrafeDirection * 0.9f;
        if (distance > a.P.PreferredRange && !camping)
            move += forward * 0.8f;
        else if (distance < 6f)
            move -= forward * 0.6f;
        if (move.LengthSquared() > 0.01f)
        {
            a.Intent.Move = Vector2.Normalize(move);
            a.Intent.Speed = 1;
        }
    }

    private void Travel(Agent a, ref EntityRecord me, Vector2 position)
    {
        bool investigating = a.Goal == Goal.Investigate;
        // Where it expects an enemy at the end of this stretch: walks the last of it, eyes on that spot.
        Vector2? closingOn = investigating ? a.PathGoal : a.Goal == Goal.Hunt && !a.HasHuntVia ? a.HuntPoint : null;
        if (!a.HasSteer || _now >= a.SteerRefreshAt || Vector2.DistanceSquared(position, a.Steer) < 1.44f)
        {
            if (a.Goal == Goal.Hunt)
            {
                if (!HuntSteer(a, position))
                {
                    a.NeedsDecision = true;
                    return;
                }
            }
            else if (a.Goal == Goal.Camp)
            {
                if (a.CampSpot < 0 || !RouteTo(a, position, _campPoints![a.CampSpot].Position, _map.PointAt(_campPoints[a.CampSpot]).Z))
                {
                    EndCamp(a);
                    return;
                }
            }
            else if (investigating)
            {
                if (a.Path.Count == 0 || !FollowPath(a, position))
                {
                    // No route, or knocked off it: ask for a new one from here.
                    ForgetInvestigateRoute(a);
                    a.NeedsDecision = true;
                    return;
                }
                if (a.PathIndex == a.Path.Count - 1 && Vector2.DistanceSquared(position, a.Steer) < 4f)
                {
                    // Got there and nobody is here. Let it go.
                    if (a.TargetTrack >= 0 && a.Tracks[a.TargetTrack].State == TrackState.Lost)
                        a.Tracks[a.TargetTrack].State = TrackState.Free;
                    a.NeedsDecision = true;
                    return;
                }
            }
            else if (a.Flow is not null)
            {
                a.Steer = a.Flow.SteerPoint(new Vector3(position, me.Z));
                if (a.Goal == Goal.Roam && Vector2.DistanceSquared(position, a.Flow.Target) < 36f)
                {
                    PickRoamTarget(a, position, own: true); // got there; somewhere else next
                    ConsiderCamping(a, position);
                }
            }
            a.HasSteer = true;
            a.SteerRefreshAt = _now + 0.25;
        }

        Vector2 to = a.Steer - position;
        float length = to.Length();
        if (length < 0.05f)
            return;
        Vector2 direction = to / length;
        CheckStuck(a, position, ref direction);
        a.Intent.Move = direction;
        a.Intent.Speed = 1;
        a.Intent.Sprint = closingOn is not { } end || Vector2.DistanceSquared(position, end) > 30f * 30f;

        // Eyes: mostly where the feet are going, with the odd glance to the side.
        if (_now >= a.NextGlanceAt)
        {
            a.GlanceOffset = a.Rng.Gaussian() * 0.6f * a.P.ScanHabit * (a.Intent.Sprint ? 0.3f : 1f);
            a.GlanceUntil = _now + a.Rng.Range(0.4f, 1.0f);
            a.NextGlanceAt = a.GlanceUntil + a.Rng.Range(1.5f, 5f) / (0.5f + a.P.ScanHabit);
        }
        a.Intent.HasLook = true;
        if (closingOn is { } spot && Vector2.DistanceSquared(position, spot) < 20f * 20f)
        {
            a.Intent.LookPoint = new Vector3(spot, me.Z + ChestHeight);
        }
        else
        {
            float heading = MathF.Atan2(direction.Y, direction.X) + (_now < a.GlanceUntil ? a.GlanceOffset : 0);
            a.Intent.LookPoint = new Vector3(position + new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * 20f, me.Z + EyeHeight - 0.1f);
        }
    }

    /// <summary>
    /// Walks a stored route: drops waypoints already reached, skips ahead to
    /// the furthest of the next few that is a straight walk from here, and
    /// returns false when not even the next one is (pushed off the route), so
    /// the caller searches again from where the body really is.
    /// </summary>
    private bool FollowPath(Agent a, Vector2 position)
    {
        Vector3 feet = Feet(a);
        while (a.PathIndex < a.Path.Count - 1 && Vector2.DistanceSquared(position, Flat(a.Path[a.PathIndex])) < 1.44f)
            a.PathIndex++;
        for (int k = Math.Min(a.Path.Count - 1, a.PathIndex + 3); k > a.PathIndex; k--)
        {
            if (_map.ClearPath(feet, a.Path[k]))
            {
                a.PathIndex = k;
                break;
            }
        }
        a.Steer = Flat(a.Path[a.PathIndex]);
        // Routes hug corners, so a body a little off the line still counts as on it: only its centre must see the waypoint.
        if (_map.OpenLine(feet, a.Path[a.PathIndex]))
            return true;
        // Cut the corner it just rounded: back to that corner first.
        if (a.PathIndex > 0 && _map.OpenLine(feet, a.Path[a.PathIndex - 1]))
        {
            a.Steer = Flat(a.Path[a.PathIndex - 1]);
            return true;
        }
        return false;
    }

    /// <summary>Makes the next decision search a new route for the investigation instead of keeping this one.</summary>
    private static void ForgetInvestigateRoute(Agent a)
    {
        a.Path.Clear();
        a.PathGoal = new Vector2(1e9f);
    }

    /// <summary>
    /// No progress for a second means stuck on something: step sideways and
    /// pick a new line. A stored route is searched again from here; stuck three
    /// times running on the way to a guessed place, the agent gives up on it.
    /// </summary>
    private void CheckStuck(Agent a, Vector2 position, ref Vector2 direction)
    {
        if (_now >= a.StuckCheckAt)
        {
            if (a.StuckCheckAt > 0 && Vector2.DistanceSquared(position, a.StuckReference) < 0.25f)
            {
                a.SidestepUntil = _now + 0.5;
                a.SidestepDirection = a.Rng.NextFloat() < 0.5f ? -1 : 1;
                a.HasSteer = false;
                Stats.StuckEvents++;
                Stats.GoalStuck[(int)a.Goal]++;
                a.StuckRun++;
                if (a.Goal == Goal.Hunt)
                {
                    a.Path.Clear();
                    if (a.StuckRun >= 3)
                    {
                        a.Hunt = HuntReason.None;
                        a.NeedsDecision = true;
                    }
                }
                else if (a.Goal == Goal.Camp)
                {
                    a.Path.Clear();
                    if (a.StuckRun >= 3)
                        EndCamp(a);
                }
                else if (a.Goal == Goal.Investigate)
                {
                    ForgetInvestigateRoute(a);
                    if (a.StuckRun >= 3 && a.TargetTrack >= 0 && a.Tracks[a.TargetTrack].State == TrackState.Lost)
                        a.Tracks[a.TargetTrack].State = TrackState.Free;
                    a.NeedsDecision = true;
                }
            }
            else
            {
                a.StuckRun = 0;
            }
            a.StuckReference = position;
            a.StuckCheckAt = _now + 1;
        }
        if (_now < a.SidestepUntil)
            direction = Vector2.Normalize(direction * 0.3f + new Vector2(-direction.Y, direction.X) * a.SidestepDirection);
    }

    private void Hold(Agent a, ref EntityRecord me, Vector2 position)
    {
        ref Zone zone = ref _zones[a.Zone];
        if (!a.HasHoldPoint || _now >= a.HoldUntil)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float angle = a.Rng.Range(0, 2 * MathF.PI), radius = zone.Radius * 0.8f * MathF.Sqrt(a.Rng.NextFloat());
                Vector2 candidate = zone.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                if (!_map.Standable(new Vector3(candidate, zone.Height)))
                    continue;
                a.HoldPoint = candidate;
                a.HasHoldPoint = true;
                break;
            }
            a.HoldUntil = _now + a.Rng.Range(3f, 9f);
        }
        if (a.HasHoldPoint)
        {
            Vector2 to = a.HoldPoint - position;
            float length = to.Length();
            if (length > 1f)
            {
                a.Intent.Move = to / length;
                a.Intent.Speed = 0.7f;
            }
        }
        LookAround(a, ref me, position);
    }

    private void LookAround(Agent a, ref EntityRecord me, Vector2 position)
    {
        if (_now >= a.ScanUntil)
        {
            a.ScanYaw = WrapAngle(a.ScanYaw + a.Rng.Gaussian() * 1.0f);
            a.ScanUntil = _now + a.Rng.Range(1f, 3f) / (0.5f + a.P.ScanHabit);
        }
        a.Intent.HasLook = true;
        a.Intent.LookPoint = new Vector3(position + new Vector2(MathF.Cos(a.ScanYaw), MathF.Sin(a.ScanYaw)) * 20f, me.Z + EyeHeight - 0.1f);
    }

    /// <summary>
    /// The hands. Takes what the skill wants and produces stick and trigger
    /// values the way a thumb would: the aim is re-planned a few times a
    /// second from a noisy estimate of the error, the thumb takes time to get
    /// where it is going, and it is never perfectly still.
    /// </summary>
    private void RunMotor(Agent a, float dt, ActFrame act)
    {
        ref EntityRecord me = ref _entities[a.EntityIndex];
        ref Intent intent = ref a.Intent;
        Persona p = a.P;

        if (intent.HasLook)
        {
            if (_now >= a.NextPlanAt)
            {
                Vector3 eye = new(me.X, me.Y, me.Z + EyeHeight);
                Vector3 d = intent.LookPoint - eye;
                float yawError = WrapAngle(MathF.Atan2(d.Y, d.X) - me.Yaw);
                float pitchError = MathF.Atan2(d.Z, MathF.Sqrt(d.X * d.X + d.Y * d.Y)) - me.Pitch;
                // The error is judged, not measured: bigger turns are judged worse.
                yawError = yawError * (1 + p.AimNoise * a.Rng.Gaussian()) + p.AimJitter * a.Rng.Gaussian();
                pitchError = pitchError * (1 + p.AimNoise * a.Rng.Gaussian()) + p.AimJitter * a.Rng.Gaussian();
                float yawRate = yawError / p.AimTimeConstant + intent.LookYawRate * p.Pursuit;
                float pitchRate = pitchError / p.AimTimeConstant;
                // Stick right turns right, which is decreasing yaw.
                Vector2 target = new(-StickFor(yawRate, _maxTurn), StickFor(pitchRate, _maxPitch));
                float length = target.Length();
                a.LookTarget = length > 1 ? target / length : target;
                a.NextPlanAt = _now + p.PlanInterval * a.Rng.Range(0.7f, 1.3f);
            }
        }
        else
        {
            a.LookTarget = default;
        }
        Spring(ref a.LookStick, ref a.LookVelocity, a.LookTarget, p.ThumbSpeed, dt);
        float unsteady = p.Tremor * (0.3f + a.LookStick.Length());
        Vector2 look = a.LookStick + new Vector2(a.Rng.Gaussian(), a.Rng.Gaussian()) * unsteady;

        // Movement is relative to the view: forward on the stick is wherever the camera faces.
        Vector2 wanted = default;
        if (intent.Speed > 0)
        {
            Vector2 forward = new(MathF.Cos(me.Yaw), MathF.Sin(me.Yaw));
            Vector2 right = new(forward.Y, -forward.X);
            wanted = new Vector2(Vector2.Dot(intent.Move, right), Vector2.Dot(intent.Move, forward)) * intent.Speed;
        }
        a.MoveStick += (wanted - a.MoveStick) * (1 - MathF.Exp(-dt / 0.07f));

        if (intent.Fire != a.FireWanted)
        {
            a.FireWanted = intent.Fire;
            a.FireChangeAt = _now + (intent.Fire ? a.Rng.Range(0.05f, 0.12f) : a.Rng.Range(0.03f, 0.1f));
        }
        if (a.FireHeld != a.FireWanted && _now >= a.FireChangeAt)
            a.FireHeld = a.FireWanted;

        uint held = 0;
        if (a.FireHeld)
            held |= _bitFire;
        if (intent.Sprint)
            held |= _bitSprint;
        if (intent.Reload)
            held |= _bitReload;
        if (intent.Interact)
            held |= _bitInteract;

        a.Sent = new ActRecord
        {
            Slot = (ushort)a.Slot,
            FromTick = _tick,
            Axis0 = ToAxis(a.MoveStick.X),
            Axis1 = ToAxis(a.MoveStick.Y),
            Axis2 = ToAxis(look.X),
            Axis3 = ToAxis(look.Y),
            Held = held,
        };
        act.Add(a.Sent);
    }

    /// <summary>The stick deflection that produces a turn rate, inverting the game's response curve.</summary>
    private float StickFor(float rate, float maxRate)
    {
        float r = MathF.Min(1f, MathF.Abs(rate) / maxRate);
        if (r < 1e-3f)
            return 0;
        return MathF.Sign(rate) * (_deadzone + (1 - _deadzone) * MathF.Pow(r, _inverseExponent));
    }

    /// <summary>A critically damped spring, stepped exactly so it is stable at any tick length.</summary>
    private static void Spring(ref Vector2 value, ref Vector2 velocity, Vector2 target, float omega, float dt)
    {
        float decay = MathF.Exp(-omega * dt);
        Vector2 offset = value - target;
        Vector2 temp = (velocity + omega * offset) * dt;
        value = target + (offset + temp) * decay;
        velocity = (velocity - omega * temp) * decay;
    }

    private static short ToAxis(float value) => (short)(Math.Clamp(value, -1f, 1f) * 32767f);
}
