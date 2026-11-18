using System.Numerics;
using BotBrain.Protocol;

namespace BotBrain.Core;

public sealed partial class World
{
    /// <summary>
    /// What one agent can see right now. The body sent the truth about
    /// everyone; this is where an agent is denied most of it. An enemy must be
    /// inside the field of view and in line of sight, and even then is only
    /// registered after a reaction time.
    /// </summary>
    private void Perceive(Agent a)
    {
        ref EntityRecord me = ref _entities[a.EntityIndex];
        Vector2 eye = new(me.X, me.Y);
        Vector3 eye3 = new(me.X, me.Y, me.Z + EyeHeight);
        Vector2 facing = new(MathF.Cos(me.Yaw), MathF.Sin(me.Yaw));
        float cosFov = MathF.Cos(a.P.FovHalfAngle);

        for (int t = 0; t < MaxTracks; t++)
            a.Tracks[t].Touched = false;

        int count = QueryNear(eye, ViewRange);
        for (int i = 0; i < count; i++)
        {
            int index = _near[i];
            if (index == a.EntityIndex)
                continue;
            ref EntityRecord other = ref _entities[index];
            if (!_hostile[a.Team * 256 + other.Team])
                continue;
            Vector2 to = new(other.X - eye.X, other.Y - eye.Y);
            float distance = to.Length();
            float cos = distance > 0.01f ? Vector2.Dot(to, facing) / distance : 1f;
            // Seen if the chest or, failing that, the head is in the open: someone behind low cover still shows.
            bool visible = (cos >= cosFov || distance < 3f)
                && (_map.LineOfSight(eye3, new Vector3(other.X, other.Y, other.Z + ChestHeight))
                    || _map.LineOfSight(eye3, new Vector3(other.X, other.Y, other.Z + EyeHeight - 0.05f)));

            int slot = FindTrack(a, other.Id);
            if (!visible)
            {
                if (slot >= 0)
                    LoseSight(ref a.Tracks[slot]);
                continue;
            }
            if (slot < 0)
            {
                slot = AllocateTrack(a, distance);
                if (slot < 0)
                    continue; // attention is full of nearer things
                float offCentre = MathF.Acos(Math.Clamp(cos, -1f, 1f));
                float delay = a.Rng.LogNormal(a.P.Reaction, 0.3f) * NoticeDifficulty(a, index, distance, offCentre);
                a.Tracks[slot] = new Track { State = TrackState.Noticing, Entity = other.Id, NoticeAt = _now + delay };
            }
            ref Track track = ref a.Tracks[slot];
            track.Touched = true;
            track.Distance = distance;
            if (track.State == TrackState.Lost)
            {
                // Seen again where it was expected: quicker than a cold notice.
                track.State = TrackState.Noticing;
                track.NoticeAt = _now + a.Rng.LogNormal(a.P.Reaction, 0.3f) * 0.6f;
            }
            if (track.State == TrackState.Noticing && _now >= track.NoticeAt)
            {
                track.State = TrackState.Visible;
                a.NeedsDecision = true;
                Stats.Noticed++;
            }
            if (track.State == TrackState.Visible)
            {
                track.Position = new Vector2(other.X, other.Y);
                track.Height = other.Z;
                track.Velocity = new Vector2(other.Vx, other.Vy);
                track.SeenAt = _now;
            }
        }

        for (int t = 0; t < MaxTracks; t++)
        {
            ref Track track = ref a.Tracks[t];
            if (track.State == TrackState.Free)
                continue;
            if (!track.Touched)
            {
                // Out of range, or dead. A death is seen or heard, so it is forgotten at once.
                bool dead = !_entityIndex.TryGetValue(track.Entity, out int index)
                    || (_entities[index].Flags & EntityFlags.Alive) == 0;
                if (dead)
                    track.State = TrackState.Free;
                else
                    LoseSight(ref track);
            }
            if (track.State == TrackState.Lost && _now - track.SeenAt > a.P.MemorySeconds)
                track.State = TrackState.Free;
        }
    }

    /// <summary>
    /// How many times longer than a plain reaction it takes to pick someone out.
    /// A person far away is a few pixels tall: twice as slow at the bot's
    /// <c>SpotDistance</c>, five times at double it. Off the centre of view it
    /// grows fast (twice at 26 degrees, five times at 52). Someone standing
    /// still is hard to see, crouched or prone harder; someone firing gives
    /// themselves away at almost any distance. The target has to stay in view
    /// the whole time, or noticing starts over.
    /// </summary>
    private float NoticeDifficulty(Agent a, int index, float distance, float offCentre)
    {
        ref EntityRecord other = ref _entities[index];
        float far = distance / a.P.SpotDistance;
        float size = 1f + far * far;
        float edge = 1f + offCentre * offCentre / (0.45f * 0.45f);
        float speed = MathF.Sqrt(other.Vx * other.Vx + other.Vy * other.Vy);
        float motion = speed < 0.3f ? 2.5f : speed < 3f ? 1.4f : 1f;
        if ((other.Flags & EntityFlags.Prone) != 0)
            motion *= 3f;
        else if ((other.Flags & EntityFlags.Crouched) != 0)
            motion *= 1.6f;
        if ((other.Flags & EntityFlags.Firing) != 0 || _now - _shotAt[index] < 1.0)
        {
            size = 1f + 0.3f * far * far;
            motion = 0.6f;
        }
        return size * edge * motion;
    }

    private void LoseSight(ref Track track)
    {
        if (track.State == TrackState.Visible)
            track.State = TrackState.Lost;
        else if (track.State == TrackState.Noticing)
            track.State = TrackState.Free; // looked away before it registered
    }

    private static int FindTrack(Agent a, uint entity)
    {
        for (int t = 0; t < MaxTracks; t++)
        {
            if (a.Tracks[t].State != TrackState.Free && a.Tracks[t].Entity == entity)
                return t;
        }
        return -1;
    }

    /// <summary>A slot for something new: a free one, else the stalest memory, else the farthest thing if this is nearer.</summary>
    private static int AllocateTrack(Agent a, float distance)
    {
        int stalest = -1, farthest = -1;
        for (int t = 0; t < MaxTracks; t++)
        {
            ref Track track = ref a.Tracks[t];
            if (track.State == TrackState.Free)
                return t;
            if (track.State == TrackState.Lost && (stalest < 0 || track.SeenAt < a.Tracks[stalest].SeenAt))
                stalest = t;
            if (farthest < 0 || track.Distance > a.Tracks[farthest].Distance)
                farthest = t;
        }
        if (stalest >= 0)
            return stalest;
        return a.Tracks[farthest].Distance > distance ? farthest : -1;
    }

    /// <summary>Things that happen at an instant: being hit, hearing gunfire, objectives changing hands.</summary>
    public void OnEvents(EventBatch batch)
    {
        RecordEvents(batch);
        for (int i = 0; i < batch.Count; i++)
        {
            ref EventRecord e = ref batch.Events[i];
            switch (e.Kind)
            {
                case EventKind.Damage:
                    OnDamage(ref e);
                    break;
                case EventKind.Shot:
                    OnShot(ref e);
                    break;
                case EventKind.Objective:
                    OnObjective((int)e.A, (int)e.B, e.Value, (ObjectiveFlags)e.C);
                    break;
            }
        }
    }

    /// <summary>Being hit tells you roughly which way it came from, not where the shooter is.</summary>
    private void OnDamage(ref EventRecord e)
    {
        if (!_entityIndex.TryGetValue(e.A, out int victim) || _agentOfEntity[victim] < 0)
            return;
        Agent a = _agents[_agentOfEntity[victim]];
        if (e.B == 0)
            return;
        // Remembered even on the killing blow, so its death knows who did it.
        a.LastAttacker = e.B;
        a.LastAttackedAt = _now;
        if (!a.Alive)
            return;
        // Shot at its spot by someone it cannot see: the spot is found out; move.
        if (a.AtCamp && FindTrack(a, e.B) is int seen && (seen < 0 || a.Tracks[seen].State != TrackState.Visible))
            EndCamp(a, burned: true);
        Vector2 position = PositionOf(victim);
        float distance = 25f;
        if (_entityIndex.TryGetValue(e.B, out int attacker))
            distance = Vector2.Distance(position, PositionOf(attacker));
        distance = Math.Clamp(distance * a.Rng.Range(0.6f, 1.4f), 5f, 80f);
        float bearing = MathF.Atan2(e.Y, e.X) + a.Rng.Gaussian() * 0.25f;
        Alert(a, e.B, position + new Vector2(MathF.Cos(bearing), MathF.Sin(bearing)) * distance);
    }

    /// <summary>Gunfire nearby is heard, with a poor idea of where it was.</summary>
    private void OnShot(ref EventRecord e)
    {
        if (!_entityIndex.TryGetValue(e.A, out int shooter))
            return;
        _shotAt[shooter] = _now;
        byte team = _entities[shooter].Team;
        Vector2 source = new(e.X, e.Y);
        HearFarGunfire(team, source, e.Z - EyeHeight);
        int count = QueryNear(source, HearRange);
        for (int i = 0; i < count; i++)
        {
            int agentIndex = _agentOfEntity[_near[i]];
            if (agentIndex < 0)
                continue;
            Agent a = _agents[agentIndex];
            if (!a.Alive || !_hostile[a.Team * 256 + team] || a.Goal == Goal.Engage || a.Rng.NextFloat() > 0.25f)
                continue;
            Alert(a, e.A, source + new Vector2(a.Rng.Gaussian(), a.Rng.Gaussian()) * 8f);
        }
    }

    /// <summary>Remember a guessed position for someone not currently in view.</summary>
    private void Alert(Agent a, uint entity, Vector2 guess)
    {
        int slot = FindTrack(a, entity);
        if (slot >= 0 && a.Tracks[slot].State != TrackState.Lost)
            return; // already looking at them
        if (slot < 0)
        {
            slot = AllocateTrack(a, float.MaxValue);
            if (slot < 0)
                return;
        }
        a.Tracks[slot] = new Track
        {
            State = TrackState.Lost,
            Entity = entity,
            Position = guess,
            Height = _map.HasGround ? _map.GroundAt(guess) : _entities[a.EntityIndex].Z,
            SeenAt = _now,
            Distance = Vector2.Distance(guess, PositionOf(a.EntityIndex)),
        };
        a.NeedsDecision = true;
        AddContact(a.Team, guess);
        Stats.Alerts++;
    }
}
