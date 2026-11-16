namespace ExploreTogether.Core.City;

/// <summary>Something a resident does that only the server can carry out (it touches players, items or the world).</summary>
public enum NpcIntentKind
{
    /// <summary>A sound (a scream, a shout, a door kicked in) that others may hear.</summary>
    Noise,
    /// <summary>Tell the police: something at (X, Z), maybe by <see cref="NpcIntent.Player"/>, this serious.</summary>
    Report,
    /// <summary>Fire at a player (the server rolls the hit).</summary>
    Shoot,
    /// <summary>Hit a player within arm's reach.</summary>
    Strike,
    /// <summary>Take hold of a player (the server answers with <see cref="CityNpcBrain.GrabResult"/>).</summary>
    Grab,
    /// <summary>Let go of the player they're holding.</summary>
    Release,
    /// <summary>Police: the held player is arrested (taken out of the city).</summary>
    Arrest,
    /// <summary>Bring round an unconscious resident (<see cref="NpcIntent.NpcId"/>).</summary>
    Revive,
}

/// <param name="Look">Reports: what the culprit looked like, as the reporter remembers it (null: they didn't see).</param>
/// <param name="Confidence">Reports: how sure the reporter is of that description (0..1).</param>
public readonly record struct NpcIntent(NpcIntentKind Kind, long Player = 0, float X = 0, float Z = 0, float Radius = 0,
    int Severity = 0, string Text = "", int NpcId = -1, Look? Look = null, float Confidence = 0)
{
    public static NpcIntent MakeNoise(float x, float z, float radius, string cause, int severity) =>
        new(NpcIntentKind.Noise, X: x, Z: z, Radius: radius, Severity: severity, Text: cause);
}

/// <summary>How worked up a resident is, for clients (a "?" or "!" over their head) and logs.</summary>
public enum Mood { Calm, Curious, Suspicious, Alarmed, Hostile }

/// <summary>
/// Reactions: what a resident does about what they see and hear (rules in <see cref="Stealth"/>). The server feeds
/// <see cref="Perceive"/> five times a second; reactions run in <see cref="Update"/> in place of the daily schedule,
/// and when it's over the resident walks back to their routine.
///
/// - <b>Watch</b>: stop and stare (curious, or a bystander keeping an eye on someone).
/// - <b>Confront</b>: those with authority over the place (and police anywhere) walk up and warn ("You can't be in
///   here!", "Put that away!"). Comply (leave, put the weapon away) and they calm down; ignore them and it escalates.
/// - <b>Arrest</b> (police) / <b>detain</b> (security, brave owners): run up and grab. Police holding someone for
///   <see cref="Breaching.ArrestSeconds"/> arrest them; others hold on while the police come. Struggle free and
///   it's resisting: police (and the armed and brave) attack.
/// - <b>Attack</b>: with whatever they carry: shoot from a distance with a gun, close in with a melee weapon, or
///   punch. Lose the target for a while and they search where they last saw them.
/// - <b>Flee</b>: run home (or away), shouting for help, and call the police.
/// - <b>Report</b>: the server dispatches the nearest free police officer to look (<see cref="Dispatch"/>).
/// - <b>Investigate</b>: go and look at a noise, a body, a broken door. <b>Revive</b>: help someone knocked out.
/// Residents remember faces: someone who alarmed them before is recognized at once.
/// </summary>
public sealed partial class CityNpcBrain
{
    private enum Act { None, Watch, Investigate, Confront, Flee, Respond, Arrest, Attack, Holding, Revive, Search }

    private sealed class Suspect
    {
        public float Level;
        public int Severity;
        public string Reason = "";
        public float X, Z;
        public int Floor;
        public int Parcel = -1;
        public Conduct Conduct;
        public double LastSeen = -1000;
        public bool Visible;
        /// <summary>
        /// They have a description of this person (<see cref="Description"/>, this sure: <see cref="Confidence"/>),
        /// from seeing them do something, or from being told. Recognizing them from it takes a look
        /// (<see cref="Recognition"/> builds up to 1); <see cref="Recognized"/> lasts while they keep them in sight.
        /// </summary>
        public bool Grudge;
        public Look Description = Look.Unknown;
        public float Confidence;
        public float Recognition;
        public bool Recognized;
        /// <summary>How they looked, and how clearly, when last seen; and which way they were heading.</summary>
        public Look LastLook = Look.Unknown;
        public float LastClarity;
        public float HeadingX, HeadingZ;
        public bool Reported;
        public bool Resisted;
    }

    private Act _act;
    private double _clockSeconds;
    private float _pace = 1;
    private long _target;
    private (float X, float Z) _point;
    private int _pointFloor, _plannedFloor;
    private double _actStarted, _actUntil, _nextReplan, _nextAttack, _grabPendingUntil;
    private (float X, float Z) _plannedTo = (float.NaN, float.NaN);
    private bool _warned, _arrestWarned, _reportOnArrival, _detainOnly;
    private int _reportSeverity, _reviveNpc = -1;
    private readonly Dictionary<long, Suspect> _suspects = new();
    private readonly HashSet<int> _knownBodies = new(), _knownDoors = new();
    private readonly Queue<NpcIntent> _intents = new();
    private long _attackedBy;
    private double _lookingSince = -1; // lost sight of whoever they were after: looking around since then
    private Look? _reportLook;
    private float _reportConfidence;

    private double _frozenUntil = -1;

    /// <summary>Frozen by frost magic: can't move or act (but still sees, and remembers) until it wears off.</summary>
    public bool Frozen => _clockSeconds < _frozenUntil;

    /// <summary>Frost magic: frozen in place for <paramref name="seconds"/> (longer freezes don't shorten).</summary>
    public void Freeze(float seconds)
    {
        if (IsDown) return;
        _frozenUntil = Math.Max(_frozenUntil, _clockSeconds + seconds);
        Moving = false;
    }

    /// <summary>Held in a player's grip: doesn't move or act (the server moves them along).</summary>
    public bool Held { get; private set; }
    public Mood Mood { get; private set; }
    /// <summary>The last thing they said, and when (seconds of their clock): clients show it for a few seconds.</summary>
    public string Speech { get; private set; } = "";
    public double SpeechAt { get; private set; } = -1000;
    /// <summary>Seconds of real time this resident has lived (their own clock for reaction timers).</summary>
    public double ClockSeconds => _clockSeconds;
    /// <summary>The player they're dealing with (0: nobody).</summary>
    public long Target => _act is Act.None ? 0 : _target;
    /// <summary>Weapon drawn (shown in their hand).</summary>
    public bool WeaponOut { get; private set; }
    /// <summary>Set by the server from what they carry: a loaded (or reloadable) gun, a melee weapon, or bare hands.</summary>
    public bool HasGun { get; set; }
    public bool HasMeleeWeapon { get; set; }
    /// <summary>What's between two points (the server's sight lines); null: nothing ever (tests).</summary>
    public CityNav.ObstacleTest? Obstacles { get; set; }
    /// <summary>For logs: what they're reacting to.</summary>
    public string ReactionNote { get; private set; } = "";

    /// <summary>Takes the next thing they want the server to do.</summary>
    public bool TryTakeIntent(out NpcIntent intent) => _intents.TryDequeue(out intent);

    private void Emit(NpcIntent intent) => _intents.Enqueue(intent);

    private void Say(string text)
    {
        Speech = text;
        SpeechAt = _clockSeconds;
    }

    private string Pick(params string[] lines) => lines[_random.Next(lines.Length)];

    private bool Asleep => Activity == "sleeping";
    private bool Brave => Person.Has(NpcTraits.Brave);
    private bool Timid => Person.Has(NpcTraits.Timid);
    private bool Armed => HasGun || HasMeleeWeapon;
    /// <summary>Will climb a fence (police, security, and anyone not timid), when that's the shorter way.</summary>
    private bool CanHop => Person.Role is NpcRole.Police or NpcRole.Security || !Timid;

    /// <summary>A jump over a fence in progress: from, to, and how far along (0..1).</summary>
    private sealed record Hop((float X, float Z) From, (float X, float Z) To) { public float T; }
    private Hop? _hop;
    public const float HopSeconds = 0.55f, HopLength = 1.3f, HopHeight = 0.75f;

    /// <summary>A fence just ahead along (ux, uz): jump it, landing past it, if the landing is clear.</summary>
    private bool StartHop(float ux, float uz)
    {
        var to = (X + ux * HopLength, Z + uz * HopLength);
        if (Obstacles?.Invoke(to.Item1, to.Item2, to.Item1 + ux * 0.4f, to.Item2 + uz * 0.4f, _level) == CityNav.Obstacle.Wall) return false;
        _hop = new Hop((X, Z), to);
        Yaw = MathF.Atan2(-ux, -uz);
        Moving = true;
        return true;
    }

    private void ContinueHop(Hop hop, float seconds, double time)
    {
        hop.T = MathF.Min(1, hop.T + seconds / HopSeconds);
        X = hop.From.X + (hop.To.X - hop.From.X) * hop.T;
        Z = hop.From.Z + (hop.To.Z - hop.From.Z) * hop.T;
        _hopY = HopHeight * 4 * hop.T * (1 - hop.T);
        if (hop.T < 1) return;
        _hopY = 0;
        _hop = null;
        _nextReplan = 0; // look again from the other side
        _plannedTo = (float.NaN, float.NaN);
        _route.Clear();
    }

    /// <summary>On duty to keep order right now: police (awake) and security at their post.</summary>
    private bool OnDuty => Person.Role == NpcRole.Police && !Asleep
                           || Person.Role == NpcRole.Security && Activity == "on watch";

    // ================================================================ perception

    /// <summary>One perception tick (<paramref name="seconds"/> since the last): what they see and hear.</summary>
    public void Perceive(Percepts p, float seconds, float hour)
    {
        if (IsDown || Held) return;
        var now = _clockSeconds;

        foreach (var noise in p.Noises)
        {
            if (Asleep)
            {
                if (!Stealth.Wakes(noise, Person)) continue;
                Activity = "woken";
                Say(Pick("What was that?", "Huh?! Who's there?"));
            }
            HearNoise(noise);
        }
        if (Asleep) return;

        foreach (var s in _suspects.Values) s.Visible = false;
        foreach (var seen in p.Players)
        {
            if (!_suspects.TryGetValue(seen.Id, out var sus)) _suspects[seen.Id] = sus = new Suspect();
            float mx = seen.X - sus.X, mz = seen.Z - sus.Z;
            var moved = MathF.Sqrt(mx * mx + mz * mz);
            if (now - sus.LastSeen < 1.5 && moved > 0.3f) (sus.HeadingX, sus.HeadingZ) = (mx / moved, mz / moved);
            (sus.X, sus.Z, sus.Floor, sus.Parcel, sus.Conduct, sus.LastSeen, sus.Visible) = (seen.X, seen.Z, Place.FloorAt(seen.Y), seen.Parcel, seen.Conduct, now, true);
            (sus.LastLook, sus.LastClarity) = (seen.Look, seen.Clarity);
            var (rate, severity, reason) = Stealth.Judge(Person, seen, _city, hour);
            if (sus.Grudge && !sus.Recognized)
            {
                // Is that them? Watching builds recognition: a good match, a sure description, a clear view.
                // Seeing someone matching near where it happened (when looking for them) helps.
                var context = _act is Act.Respond or Act.Search && _target == seen.Id && Dist(seen.X, seen.Z, _point.X, _point.Z) < 30 ? 1.5f : 1f;
                sus.Recognition += Witness.RecognitionRate(Witness.Match(sus.Description, seen.Look), sus.Confidence, seen.Clarity, context) * seconds;
                if (sus.Recognition >= 1) sus.Recognized = true;
            }
            if (sus.Grudge && sus.Recognized && sus.Level < Stealth.Alarmed)
            {
                // Recognized: a crime they know of means alarm; something lesser, a closer look.
                if (sus.Severity >= Severity.Crime)
                {
                    (rate, reason) = (float.PositiveInfinity, "recognized");
                    severity = Math.Max(severity, sus.Severity);
                }
                else sus.Level = MathF.Max(sus.Level, Stealth.Suspicious);
            }
            if (rate > 0)
            {
                sus.Level = float.IsPositiveInfinity(rate) ? Stealth.Alarmed : MathF.Min(Stealth.Alarmed, sus.Level + rate * seconds);
                if (severity >= sus.Severity) (sus.Severity, sus.Reason) = (severity, reason);
            }
            else if (_act != Act.Attack) sus.Level = MathF.Max(0, sus.Level - Stealth.Fade * seconds);
        }
        foreach (var s in _suspects.Values)
        {
            if (!s.Visible && now - s.LastSeen > Witness.LoseAfterSeconds && (s.Recognized || s.Recognition > 0))
            {
                // Lost them: next time, they have to recognize them all over again.
                (s.Recognized, s.Recognition) = (false, 0);
                if (_act is not (Act.Attack or Act.Arrest) || _target != PlayerOf(s)) s.Level = MathF.Min(s.Level, Stealth.Curious - 5);
            }
            if (!s.Visible && !s.Grudge) s.Level = MathF.Max(0, s.Level - Stealth.Fade * 0.5f * seconds);
            if (s.Grudge && (s.Confidence -= Witness.FadePerSecond * seconds) < Witness.Useless)
                (s.Grudge, s.Description, s.Recognized) = (false, Look.Unknown, false); // the memory has faded
        }

        // Someone seen doing something terrible: anyone close by them draws a little suspicion too (once per incident),
        // falling off fast with distance. Each player is otherwise judged on their own conduct.
        foreach (var culprit in p.Players.Where(s => (s.Conduct & (Conduct.Violent | Conduct.Dragging | Conduct.Holding)) != 0))
        {
            if (!_associated.Add((culprit.Id, (int)(_clockSeconds / 10)))) continue;
            foreach (var other in p.Players)
                if (other.Id != culprit.Id && _suspects.TryGetValue(other.Id, out var s))
                    Associate(s, Dist(other.X, other.Z, culprit.X, culprit.Z), $"with {culprit.Name}");
        }

        foreach (var body in p.Bodies)
            if (_knownBodies.Add(body.NpcId)) SeeBody(body);
        foreach (var door in p.DamagedDoors)
            if (_knownDoors.Add(door)) SeeDamagedDoor(door);

        Decide();
        UpdateMood();
    }

    private readonly HashSet<(long, int)> _associated = new();

    private long PlayerOf(Suspect s) => _suspects.FirstOrDefault(kv => kv.Value == s).Key;

    /// <summary>
    /// Seen with their own eyes (doing something, or attacking them): they remember the look, as sure as they saw
    /// clearly, and know them right now.
    /// </summary>
    private static void Learn(Suspect sus)
    {
        if (sus.LastLook.ColourKnown || sus.LastLook.Weapon != WeaponKind.Unknown)
        {
            var sure = Witness.FirstHand(sus.LastClarity);
            if (!sus.Grudge || sure >= sus.Confidence) sus.Description = sus.LastLook;
            sus.Confidence = MathF.Max(sus.Confidence, sure);
        }
        sus.Grudge = sus.Confidence >= Witness.Useless;
        sus.Recognized = true;
    }

    /// <summary>What they'd tell the police about someone: their description (or what they last saw), and how sure.</summary>
    private static (Look? Look, float Confidence) Account(Suspect sus) =>
        sus.Grudge ? (sus.Description, sus.Confidence)
        : sus.LastLook.ColourKnown ? (sus.LastLook, Witness.FirstHand(sus.LastClarity) * 0.8f)
        : (null, 0);

    /// <summary>What a description they know (of a player) says, for others to ask: null if they have none.</summary>
    public (Look Look, float Confidence)? DescriptionOf(long player) =>
        _suspects.TryGetValue(player, out var s) && s.Grudge ? (s.Description, s.Confidence) : null;

    /// <summary>Guilt by association (see <see cref="Stealth.Association"/>): capped below alarm, at "suspicious".</summary>
    private static void Associate(Suspect s, float distance, string reason)
    {
        var bump = Stealth.Association(distance);
        if (bump <= 0 || s.Level >= Stealth.Alarmed - 1) return;
        s.Level = MathF.Min(MathF.Max(s.Level, Stealth.Alarmed - 25), s.Level + bump); // never tipped into alarm by this
        if (s.Severity < Severity.Suspicious) (s.Severity, s.Reason) = (Severity.Suspicious, reason);
    }

    private void Decide()
    {
        // Busy with something that runs its course.
        if (_act is Act.Attack or Act.Holding or Act.Flee or Act.Revive) return;
        if (_act == Act.Arrest && _target != 0) return;

        var top = _suspects.Where(kv => kv.Value.Visible || _clockSeconds - kv.Value.LastSeen < 1.5)
            .OrderByDescending(kv => kv.Value.Level).FirstOrDefault();
        if (top.Value is not { } sus) return;
        if (sus.Level >= Stealth.Alarmed) Escalate(top.Key, sus);
        else if (sus.Level >= Stealth.Suspicious && _act is Act.None or Act.Watch or Act.Investigate or Act.Search or Act.Respond)
            BecomeSuspicious(top.Key, sus);
        else if (sus.Level >= Stealth.Curious && _act == Act.None)
            Begin(Act.Watch, top.Key, "watching", 1f, 8);
    }

    /// <summary>Alarmed: what they do depends on role, personality, what they carry and how bad it is.</summary>
    private void Escalate(long player, Suspect sus)
    {
        if (sus.Visible || sus.Recognized) Learn(sus);
        var severity = sus.Severity;
        var violent = severity >= Severity.Violent || sus.Resisted;
        ReactionNote = $"{sus.Reason} ({severity})";
        switch (Person.Role)
        {
            case NpcRole.Police:
                if (violent) StartAttack(player);
                else StartArrest(player, detainOnly: false);
                if (!sus.Reported && severity >= Severity.Crime) ReportOnce(sus, player);
                return;
            case NpcRole.Security:
                var onPost = sus.Parcel == Person.Work || Near(_city.Parcels[Person.Work], 25);
                if (onPost || violent)
                {
                    if (violent && Armed) StartAttack(player);
                    else if (!violent) StartArrest(player, detainOnly: true);
                    else StartFlee(sus, player);
                    ReportOnce(sus, player);
                    return;
                }
                break;
        }
        if (Brave && !Timid)
        {
            if (violent && Armed) { StartAttack(player); ReportOnce(sus, player); return; }
            if (!violent && Person.HasAuthorityOver(sus.Parcel)) { StartArrest(player, detainOnly: true); ReportOnce(sus, player); return; }
            if (!violent) { StartConfront(player, sus); ReportOnce(sus, player); return; }
        }
        if (violent || Timid || Person.HasAuthorityOver(sus.Parcel)) StartFlee(sus, player);
        else
        {
            // A bystander: keep watching, and call it in.
            Begin(Act.Watch, player, "watching", 1f, 10);
            ReportOnce(sus, player);
        }
    }

    private void BecomeSuspicious(long player, Suspect sus)
    {
        var minded = Person.HasAuthorityOver(sus.Parcel) && sus.Parcel >= 0
                     || OnDuty && (sus.Conduct & (Conduct.GunOut | Conduct.Armed | Conduct.TrespassYard | Conduct.TrespassInside)) != 0;
        if (minded && !Timid) StartConfront(player, sus);
        else
        {
            if (_act != Act.Watch) Begin(Act.Watch, player, "watching", 1f, 8);
            if (Person.Has(NpcTraits.Nosy) && sus.Level >= 75) ReportOnce(sus, player);
        }
    }

    private void ReportOnce(Suspect sus, long player)
    {
        if (sus.Reported) return;
        sus.Reported = true;
        var (look, sure) = Account(sus);
        Emit(new NpcIntent(NpcIntentKind.Report, player, sus.X, sus.Z, Severity: Math.Max(sus.Severity, Severity.Suspicious), Text: sus.Reason,
            Look: look, Confidence: sure));
    }

    private void HearNoise(Noise n)
    {
        if (_act is Act.Attack or Act.Holding or Act.Arrest or Act.Flee) return;
        var mine = _city.ParcelAt(n.X, n.Z) is { } p && Person.HasAuthorityOver(p.Id) && p.Id >= 0;
        if (n.Severity >= Severity.Violent)
        {
            if (OnDuty) StartRespond(n.X, n.Z, n.Player, n.Severity, Place.FloorAt(n.Y));
            else if (Timid || !Brave && !mine) StartFleeFrom(n.X, n.Z, report: Person.Has(NpcTraits.Nosy) || mine);
            else StartInvestigate(n.X, n.Z, "investigating", 1.6f, Place.FloorAt(n.Y));
            return;
        }
        if (n.Cause == "footsteps" && !mine) return;
        if (mine || OnDuty || Person.Has(NpcTraits.Nosy) || n.Cause is "scream" or "shout" && Brave)
        {
            if (mine) Say(Pick("Who's there?", "Hello? Someone there?", "I heard that!"));
            StartInvestigate(n.X, n.Z, "investigating", mine || OnDuty ? 1.5f : 1f, Place.FloorAt(n.Y));
            if (mine && n.Severity >= Severity.Crime) Emit(new NpcIntent(NpcIntentKind.Report, n.Player, n.X, n.Z, Severity: Severity.Crime, Text: "break-in"));
        }
        else if (_act == Act.None) FaceToward(n.X, n.Z);
    }

    private void SeeBody(BodySighting b)
    {
        if (_act is Act.Attack or Act.Holding or Act.Arrest or Act.Flee) return;
        // Anyone near it looks a little suspicious (much less so further away; never more than suspicious).
        foreach (var s in _suspects.Values)
            if (s.Visible) Associate(s, Dist(s.X, s.Z, b.X, b.Z), "near a body");
        if (!b.Dead && (Brave || OnDuty || Person.Has(NpcTraits.Nosy)) && !Timid)
        {
            Say(Pick("Hey! Are you all right?", "Oh no, wake up!"));
            Begin(Act.Revive, 0, "helping", 1.8f, 30);
            (_point, _pointFloor, _reviveNpc) = ((b.X, b.Z), Place.FloorAt(b.Y), b.NpcId);
            Emit(new NpcIntent(NpcIntentKind.Report, 0, b.X, b.Z, Severity: Severity.Crime, Text: "someone knocked out"));
            return;
        }
        if (b.Dead)
        {
            Say(Pick("Aaah! Somebody help!", "Oh my god, he's dead!", "Murder! MURDER!"));
            Emit(NpcIntent.MakeNoise(X, Z, Stealth.ScreamRadius, "scream", Severity.Crime));
        }
        Emit(new NpcIntent(NpcIntentKind.Report, 0, b.X, b.Z, Severity: b.Dead ? Severity.Violent : Severity.Crime, Text: b.Dead ? "a body" : "someone knocked out"));
        if (OnDuty) StartInvestigate(b.X, b.Z, "examining the body", 1.6f, Place.FloorAt(b.Y));
        else if (Timid || b.Dead) StartFleeFrom(b.X, b.Z, report: false);
        else Begin(Act.Watch, 0, "staring", 1f, 4);
    }

    private void SeeDamagedDoor(int parcel)
    {
        if (!Person.HasAuthorityOver(parcel) && !Person.Has(NpcTraits.Nosy)) return;
        if (_act is not (Act.None or Act.Watch or Act.Search)) return;
        var p = _city.Parcels[parcel];
        Say(Pick("Someone's broken in!", "What happened to the door?!", "Has someone been in here?"));
        Emit(new NpcIntent(NpcIntentKind.Report, 0, p.Door.X, p.Door.Z, Severity: Severity.Crime, Text: "a break-in"));
        if (!Timid) { var spot = RandomSpot(p); StartInvestigate(spot.X, spot.Z, "checking the house", 1.3f, spot.Floor); }
    }

    // ================================================================ calls from the server

    /// <summary>
    /// Told about trouble (the police, by a call; people nearby, by word of mouth). With a description, they may
    /// recognize the culprit later, if they get a good look (see <see cref="Witness"/>); without one, they only know
    /// where. With <paramref name="respond"/>, an officer who isn't busy goes to look. Returns true if they're on their way.
    /// </summary>
    public bool Dispatch(float x, float z, long player, int severity, bool respond = true, Look? description = null, float confidence = 0)
    {
        if (player != 0)
        {
            if (!_suspects.TryGetValue(player, out var sus)) _suspects[player] = sus = new Suspect();
            sus.Severity = Math.Max(sus.Severity, severity);
            sus.Reason = sus.Reason == "" ? "wanted" : sus.Reason;
            sus.Reported = true;
            if (description is { } d && confidence >= Witness.Useless)
            {
                // A second account adds to what they knew; a better one replaces the details.
                if (!sus.Grudge || confidence > sus.Confidence) sus.Description = d;
                sus.Confidence = sus.Grudge ? Witness.Combine(sus.Confidence, confidence * 0.5f) : confidence;
                sus.Grudge = true;
            }
            if (!sus.Visible) (sus.X, sus.Z) = (x, z);
        }
        if (!respond || Person.Role != NpcRole.Police || IsDown || Held || Asleep) return false;
        if (_act is not (Act.None or Act.Watch or Act.Search or Act.Investigate)) return false;
        StartRespond(x, z, player, severity);
        Say(Pick("On my way.", "Copy that.", "Heading there now."));
        return true;
    }

    /// <summary>A player hurt or tried to hurt them: they know who did it.</summary>
    /// <param name="look">How the attacker looked to them (null: they didn't see), and how clearly.</param>
    public void Attacked(long player, float px, float pz, float py = 0, Look? look = null, float clarity = 0.8f)
    {
        _attackedBy = player;
        if (!_suspects.TryGetValue(player, out var sus)) _suspects[player] = sus = new Suspect();
        if (look is { } l)
        {
            (sus.LastLook, sus.LastClarity) = (l, clarity);
            Learn(sus); // remembered even if this knocks them out
        }
        if (IsDown || Held) return;
        (sus.X, sus.Z, sus.Floor, sus.LastSeen, sus.Level, sus.Severity, sus.Reason) = (px, pz, Place.FloorAt(py), _clockSeconds, Stealth.Alarmed, Severity.Violent, "attacked me");
        if (_act == Act.Holding) Emit(new NpcIntent(NpcIntentKind.Release, _target));
        _act = Act.None;
        Escalate(player, sus);
        UpdateMood();
    }

    /// <summary>A player took hold of them (<paramref name="surprised"/>: from behind, unseen: they can't cry out).</summary>
    public void GrabbedBy(long player, float px, float pz, bool surprised, Look? look = null)
    {
        if (_act == Act.Holding) Emit(new NpcIntent(NpcIntentKind.Release, _target));
        EndReaction(returnToRoutine: false);
        Held = true;
        Moving = false;
        _route.Clear();
        _doorReady = false;
        Activity = "struggling";
        if (!_suspects.TryGetValue(player, out var sus)) _suspects[player] = sus = new Suspect();
        (sus.X, sus.Z, sus.LastSeen, sus.Level, sus.Severity, sus.Reason) =
            (px, pz, _clockSeconds, Stealth.Alarmed, Severity.Violent, "grabbed me");
        if (!surprised && look is { } l)
        {
            (sus.LastLook, sus.LastClarity) = (l, 1);
            Learn(sus); // face to face
        }
        if (!surprised)
        {
            Say(Pick("Get off me!", "Help! HELP!", "Let go of me!"));
            Emit(NpcIntent.MakeNoise(X, Z, Stealth.ShoutRadius, "shout", Severity.Crime));
        }
        Mood = Mood.Alarmed;
    }

    /// <summary>Struck by whoever holds them: they give in for now.</summary>
    public void Cowed()
    {
        if (!Held || IsDown) return;
        Say(Pick("Okay! Okay! Don't hurt me!", "Please... I'll do what you want.", "Ow! Stop, stop!"));
        Activity = "cowering";
    }

    /// <summary>A held resident moves where they're pulled.</summary>
    public void MoveHeld(float x, float z, float yaw, float y = 0)
    {
        if (Held) (X, Z, Yaw, _level) = (x, z, yaw, y);
    }

    /// <summary>Let go of (or wriggled free): they react to whoever held them.</summary>
    public void Released(long player, bool escaped)
    {
        if (!Held) return;
        Held = false;
        Inside = -1;
        if (IsDown) return;
        Activity = "shaken";
        if (escaped) Say(Pick("Get away from me!", "Help! Police!", "You'll pay for that!"));
        if (_suspects.TryGetValue(player, out var sus))
        {
            if (escaped) Learn(sus); // they turned round and got a look at them
            Escalate(player, sus);
        }
        UpdateMood();
    }

    /// <summary>The server's answer to a <see cref="NpcIntentKind.Grab"/>.</summary>
    public void GrabResult(long player, bool held)
    {
        _grabPendingUntil = 0;
        if (_act is not (Act.Arrest or Act.Attack) || player != _target) return; // attackers grab hostage-takers too
        if (!held) return;
        if (_act == Act.Attack) _detainOnly = Person.Role != NpcRole.Police;
        _act = Act.Holding;
        _actStarted = _clockSeconds;
        _route.Clear();
        Activity = _detainOnly ? "holding someone" : "arresting";
        Say(_detainOnly ? Pick("Got you! Somebody call the police!", "You're not going anywhere!")
                        : Pick("You're under arrest!", "Hands behind your back!", "Stop resisting!"));
        UpdateMood();
    }

    /// <summary>The player they were holding broke free: that's resisting.</summary>
    public void GripBroken(long player)
    {
        if (_act != Act.Holding || player != _target) return;
        if (_suspects.TryGetValue(player, out var sus))
        {
            sus.Resisted = true;
            sus.Severity = Severity.Violent;
            sus.Reason = Person.Role == NpcRole.Police ? "resisting arrest" : "broke free";
            _act = Act.None;
            Say(Person.Role == NpcRole.Police ? "Resisting arrest! Stop!" : Pick("Hey! Come back!", "Help! Police!"));
            Escalate(player, sus);
            if (Person.Role == NpcRole.Police)
                Emit(new NpcIntent(NpcIntentKind.Report, player, sus.X, sus.Z, Severity: Severity.Violent, Text: "resisting arrest",
                    Look: sus.Description, Confidence: sus.Confidence));
        }
        else EndReaction();
        UpdateMood();
    }

    /// <summary>How suspicious of a player they are (0..100), and whether they're dealing with them right now.</summary>
    public (float Level, bool Engaged, bool Hostile) SuspicionOf(long player)
    {
        var level = _suspects.TryGetValue(player, out var s) ? s.Level : 0;
        var engaged = _act != Act.None && _target == player;
        return (level, engaged, engaged && _act is Act.Attack or Act.Arrest or Act.Holding);
    }

    /// <summary>A colleague already has hold of the person they were after: stand by and keep watch.</summary>
    public void StandBy(long player)
    {
        if (_act != Act.Arrest || _target != player) return;
        Say(Pick("I've got your back.", "Backup's here."));
        Begin(Act.Watch, player, "standing by", 1f, 10);
    }

    /// <summary>Someone else (the police) took over the person they were holding: step back and watch.</summary>
    public void HandOver()
    {
        if (_act != Act.Holding) return;
        _act = Act.None;
        Say(Pick("All yours, officer.", "Took you long enough!"));
        Begin(Act.Watch, _target, "watching", 1f, 6);
    }

    /// <summary>Only on the server's say-so (e.g. the person they held is out of reach): stop holding.</summary>
    public void LostGrip()
    {
        if (_act != Act.Holding) return;
        _act = Act.None;
        EndReaction();
    }

    /// <summary>The player left the city, died or was arrested: forget them (no more chasing).</summary>
    public void ForgetPlayer(long player)
    {
        _suspects.Remove(player);
        if (_target == player && _act != Act.None)
        {
            _act = _act == Act.Holding ? Act.Watch : _act; // the server has already let go
            EndReaction();
        }
        if (_attackedBy == player) _attackedBy = 0;
    }

    /// <summary>Came round after being knocked out: if they know who did it, they run and tell the police.</summary>
    private void ReactToWaking()
    {
        if (_attackedBy == 0 || !_suspects.TryGetValue(_attackedBy, out var sus)) return;
        _recovering = false;
        sus.Reported = false;
        Say(Pick("Ugh... my head... POLICE!", "Help! I was attacked!"));
        Escalate(_attackedBy, sus);
    }

    // ================================================================ starting reactions

    private void Begin(Act act, long target, string activity, float pace, double maxSeconds)
    {
        if (_hop != null) { (X, Z, _hopY, _hop) = (_hop.To.X, _hop.To.Z, 0, null); }
        if (_act == Act.Holding && act != Act.Holding) Emit(new NpcIntent(NpcIntentKind.Release, _target));
        _act = act;
        _target = target;
        _pace = pace;
        _actStarted = _clockSeconds;
        _actUntil = _clockSeconds + maxSeconds;
        _nextReplan = 0;
        _plannedTo = (float.NaN, float.NaN);
        _warned = _arrestWarned = false;
        _lookingSince = -1;
        Activity = activity;
        if (act != Act.Watch) _route.Clear();
        _doorReady = false;
        if (Inside >= 0 && !(_city.Parcels[Inside].Footprint.Contains(X, Z))) Inside = -1;
        WeaponOut = act == Act.Attack;
        UpdateMood();
    }

    private void StartConfront(long player, Suspect sus)
    {
        if (_act == Act.Confront && _target == player) return;
        Begin(Act.Confront, player, "confronting", Person.Role == NpcRole.Police ? 1.8f : 1.4f, 14);
        ReactionNote = sus.Reason;
    }

    private void StartArrest(long player, bool detainOnly)
    {
        if (_act == Act.Arrest && _target == player) return;
        Begin(Act.Arrest, player, detainOnly ? "chasing" : "arresting", 3.0f, 90);
        _detainOnly = detainOnly;
    }

    private void StartAttack(long player)
    {
        if (_act == Act.Attack && _target == player) return;
        Begin(Act.Attack, player, "fighting", 2.8f, 180);
        Say(HasGun ? Pick("Drop it!", "Freeze! Or I'll shoot!", "Get down!") : Pick("You asked for it!", "Come here!"));
        _nextAttack = _clockSeconds + 0.6;
    }

    private void StartFlee(Suspect sus, long player) =>
        StartFleeFrom(sus.X, sus.Z, report: !sus.Reported, player, Math.Max(sus.Severity, Severity.Crime), sus);

    private void StartFleeFrom(float x, float z, bool report, long player = 0, int severity = Severity.Crime, Suspect? sus = null)
    {
        Begin(Act.Flee, player, "fleeing", 2.6f, 25);
        (_reportLook, _reportConfidence) = sus != null ? Account(sus) : (null, 0);
        Say(Pick("Help! Police!", "Somebody help!", "Aaah!"));
        Emit(NpcIntent.MakeNoise(X, Z, Stealth.ScreamRadius, "scream", Severity.Crime));
        _point = (x, z);
        _pointFloor = 0;
        _reportOnArrival = report;
        _reportSeverity = severity;
        if (sus != null) sus.Reported = true;
        // Home, unless that's where the trouble is (or they're nowhere near it); otherwise away along the streets.
        var home = Person.Home >= 0 ? _city.Parcels[Person.Home] : null;
        if (home != null && Dist(home.Door.X, home.Door.Z, x, z) > 20 && Dist(home.Door.X, home.Door.Z, X, Z) < 90)
        {
            var spot = RandomSpot(home);
            (_plannedTo, _plannedFloor) = ((spot.X, spot.Z), spot.Floor);
        }
        else
        {
            var far = _paths.StreetCells.Where(c =>
            {
                var (cx, cz) = CityLayout.CellCenter(c);
                return Dist(cx, cz, x, z) > 30 && Dist(cx, cz, X, Z) < 60;
            }).ToList();
            var cell = far.Count > 0 ? far[_random.Next(far.Count)] : RandomStreetCell();
            (_plannedTo, _plannedFloor) = (CityLayout.CellCenter(cell), 0);
        }
        SetRoute(_plannedTo, _plannedFloor);
    }

    private void StartInvestigate(float x, float z, string activity, float pace, int floor = 0)
    {
        Begin(Act.Investigate, 0, activity, pace, 40);
        (_point, _pointFloor) = ((x, z), floor);
    }

    private void StartRespond(float x, float z, long player, int severity, int floor = 0)
    {
        Begin(Act.Respond, player, "responding", severity >= Severity.Crime ? 2.6f : 1.8f, 120);
        (_point, _pointFloor) = ((x, z), floor);
    }

    /// <summary>Calm again: back to the day's routine, finding the way back to the street (or their building) first.</summary>
    private void EndReaction(bool returnToRoutine = true)
    {
        if (_act == Act.None) return;
        if (_hop != null) { (X, Z, _hopY, _hop) = (_hop.To.X, _hop.To.Z, 0, null); }
        _act = Act.None;
        _target = 0;
        _pace = 1;
        WeaponOut = false;
        _detainOnly = false;
        _reviveNpc = -1;
        ReactionNote = "";
        if (!returnToRoutine) return;
        _route.Clear();
        _doorReady = false;
        var parcel = _city.ParcelAt(X, Z);
        if (parcel != null && parcel.Footprint.Contains(X, Z))
        {
            Inside = parcel.Id;
            _hasGoal = false;
        }
        else if (parcel != null)
        {
            Inside = -1;
            foreach (var pt in CityPaths.WayToStreet(parcel, (X, Z)).Points) _route.Enqueue(pt);
            (_routeEndsIn, _routeLabel, _dwellHours, _recovering) = (-1, "walking", 0, true);
        }
        else if (_city.AlleyAt(X, Z) is { } alley)
        {
            Inside = -1;
            _route.Enqueue(alley.Mouths.MinBy(m => Dist(m.X, m.Z, X, Z)));
            (_routeEndsIn, _routeLabel, _dwellHours, _recovering) = (-1, "walking", 0, true);
        }
        else
        {
            Inside = -1;
            _hasGoal = false;
        }
        Activity = "walking";
        UpdateMood();
    }

    // ================================================================ running reactions

    private void React(float seconds, double time)
    {
        var now = _clockSeconds;
        _suspects.TryGetValue(_target, out var sus);
        if (now > _actUntil && _act is not (Act.Holding or Act.Attack))
        {
            if (_act == Act.Arrest && sus != null) StartSearch(sus.X, sus.Z, sus.Floor);
            else EndReaction();
            return;
        }
        switch (_act)
        {
            case Act.Watch:
                StandStill();
                if (sus != null) FaceToward(sus.X, sus.Z);
                else if (_target == 0 && now - _actStarted > 3) EndReaction();
                if (sus != null && (sus.Level < Stealth.Curious - 5 || now - sus.LastSeen > 6)) EndReaction();
                break;

            case Act.Confront:
                if (sus == null || now - sus.LastSeen > 4) { StartSearchOrEnd(sus); break; }
                var d = Dist(sus.X, sus.Z, X, Z);
                if (d > 2.5f) GoTo(sus.X, sus.Z, sus.Floor);
                else { StandStill(); FaceToward(sus.X, sus.Z); }
                if (!_warned && d < 7)
                {
                    _warned = true;
                    _actUntil = now + 9; // time to comply
                    Say(WarningFor(sus));
                }
                var stillBad = (sus.Conduct & (Conduct.TrespassInside | Conduct.TrespassYard | Conduct.GunOut | Conduct.Armed)) != 0;
                if (_warned && !stillBad && now - _actStarted > 2)
                {
                    sus.Level = Stealth.Curious;
                    Say(Pick("Good. Keep it that way.", "And stay out.", "That's better."));
                    Begin(Act.Watch, _target, "watching", 1f, 6);
                }
                else if (_warned && now > _actUntil - 0.1)
                {
                    sus.Level = Stealth.Alarmed;
                    sus.Severity = Math.Max(sus.Severity, Severity.Crime);
                    sus.Reason = "ignored a warning";
                    _act = Act.None;
                    Escalate(_target, sus);
                }
                break;

            case Act.Arrest:
                if (sus == null) { EndReaction(); break; }
                if (!sus.Visible && Chase(sus, seconds)) break;
                if (sus.Visible && _lookingSince >= 0) { _lookingSince = -1; Activity = _detainOnly ? "chasing" : "arresting"; Say(Pick("There you are!", "Stop!")); }
                var da = Dist(sus.X, sus.Z, X, Z);
                if (!_arrestWarned && da < 9)
                {
                    _arrestWarned = true;
                    Say(_detainOnly ? Pick("Stop right there!", "Hey, you! Don't move!") : Pick("Police! Stop right there!", "Police! Don't move!"));
                }
                if (da <= Breaching.GrabReach)
                {
                    StandStill();
                    FaceToward(sus.X, sus.Z);
                    if (now >= _grabPendingUntil && sus.Visible)
                    {
                        _grabPendingUntil = now + 0.6;
                        Emit(new NpcIntent(NpcIntentKind.Grab, _target));
                    }
                }
                else GoTo(sus.X, sus.Z, sus.Floor);
                break;

            case Act.Holding:
                StandStill();
                if (sus != null) FaceToward(sus.X, sus.Z);
                var held = now - _actStarted;
                if (!_detainOnly && held >= Breaching.ArrestSeconds)
                {
                    Say(Pick("You're coming with me.", "That's it, you're done."));
                    Emit(new NpcIntent(NpcIntentKind.Arrest, _target));
                    if (sus != null) sus.Grudge = false;
                    _suspects.Remove(_target);
                    EndReaction();
                }
                else if (_detainOnly && held >= 15)
                {
                    Emit(new NpcIntent(NpcIntentKind.Release, _target));
                    Say(Pick("Get out of here, and don't come back!", "Go on, get lost!"));
                    Begin(Act.Watch, _target, "watching", 1f, 8);
                }
                break;

            case Act.Attack:
                if (sus == null) { EndReaction(); break; }
                if (!sus.Visible && Chase(sus, seconds)) break;
                if (sus.Visible && _lookingSince >= 0) { _lookingSince = -1; Activity = "fighting"; Say(Pick("There!", "Got eyes on them!")); }
                var dt = Dist(sus.X, sus.Z, X, Z);
                WeaponOut = true;
                if (sus.Visible && (sus.Conduct & Conduct.Holding) != 0 && !TakesTheShot(dt))
                {
                    // A human shield: hold fire. Close in and grab them instead (that frees the hostage).
                    if (now >= _nextAttack)
                    {
                        _nextAttack = now + 3 + _random.NextDouble() * 2;
                        Say(Pick("Let them go!", "Drop them, now!", "Nobody needs to get hurt. Let them go.", "Hold your fire, there's a hostage!"));
                    }
                    if (dt > Breaching.GrabReach) GoTo(sus.X, sus.Z, sus.Floor);
                    else
                    {
                        StandStill();
                        FaceToward(sus.X, sus.Z);
                        if (now >= _grabPendingUntil)
                        {
                            _grabPendingUntil = now + 0.6;
                            Emit(new NpcIntent(NpcIntentKind.Grab, _target));
                        }
                    }
                    break;
                }
                if (HasGun)
                {
                    if (!sus.Visible || dt > 18) GoTo(sus.X, sus.Z, sus.Floor);
                    else if (dt < 3) { StandStill(); FaceToward(sus.X, sus.Z); }
                    else { StandStill(); FaceToward(sus.X, sus.Z); }
                    if (sus.Visible && dt <= 30 && now >= _nextAttack)
                    {
                        _nextAttack = now + 1.0 + _random.NextDouble() * 0.6;
                        Emit(new NpcIntent(NpcIntentKind.Shoot, _target, sus.X, sus.Z));
                    }
                }
                else
                {
                    if (dt > 1.5f) GoTo(sus.X, sus.Z, sus.Floor);
                    else { StandStill(); FaceToward(sus.X, sus.Z); }
                    if (sus.Visible && dt <= 1.9f && now >= _nextAttack)
                    {
                        _nextAttack = now + 1.1;
                        Emit(new NpcIntent(NpcIntentKind.Strike, _target, sus.X, sus.Z));
                    }
                }
                break;

            case Act.Flee:
                if (_reportOnArrival && _route.Count > 0 && now - _actStarted > 8)
                {
                    // Calls for help on the way (shouting to someone, or on the phone).
                    _reportOnArrival = false;
                    Emit(new NpcIntent(NpcIntentKind.Report, _target, _point.X, _point.Z, Severity: _reportSeverity, Text: "a call for help",
                        Look: _reportLook, Confidence: _reportConfidence));
                }
                if (_route.Count == 0)
                {
                    if (_reportOnArrival)
                    {
                        _reportOnArrival = false;
                        Activity = "calling the police";
                        Say(Pick("Hello, police? Come quick!", "Police! There's someone here!"));
                        Emit(new NpcIntent(NpcIntentKind.Report, _target, _point.X, _point.Z, Severity: _reportSeverity, Text: "a call for help",
                            Look: _reportLook, Confidence: _reportConfidence));
                        _actUntil = now + 6;
                        StandStill();
                    }
                    else if (now > _actUntil - 0.1 || Activity != "calling the police") EndReaction();
                }
                break;

            case Act.Investigate:
            case Act.Respond:
                if (_route.Count == 0 && Dist(_point.X, _point.Z, X, Z) > 2f && double.IsNaN(_plannedTo.X)) SetRoute(_point, _pointFloor);
                else if (_route.Count == 0)
                {
                    if (_act == Act.Respond)
                    {
                        Interview();
                        StartSearch(_point.X, _point.Z, _pointFloor);
                    }
                    else
                    {
                        // Look around for a few seconds.
                        Activity = "looking around";
                        StandStill();
                        Yaw += seconds * 1.4f;
                        if (_actUntil > now + 6) _actUntil = now + 4 + _random.NextDouble() * 3;
                    }
                }
                break;

            case Act.Search:
                if (_route.Count == 0)
                {
                    if (now > _actUntil - 0.1) { EndReaction(); break; }
                    // Walk to another spot near where they last saw them.
                    // Upstairs: another room on that floor. Elsewhere: somewhere near, not behind a wall.
                    if (_pointFloor > 0 && _city.ParcelAt(_point.X, _point.Z) is { } searched
                        && searched.Places.Where(pl => pl.Floor == _pointFloor).ToList() is { Count: > 0 } rooms)
                    {
                        var room = rooms[_random.Next(rooms.Count)];
                        SetRoute((room.X, room.Z), room.Floor);
                        break;
                    }
                    var a = _random.NextDouble() * Math.PI * 2;
                    var r = 3 + _random.NextDouble() * 7;
                    var spot = ((float)(_point.X + Math.Cos(a) * r), (float)(_point.Z + Math.Sin(a) * r));
                    if (Obstacles?.Invoke(X, Z, spot.Item1, spot.Item2, _level) is CityNav.Obstacle.Wall) spot = _point;
                    SetRoute(spot, _pointFloor);
                }
                break;

            case Act.Revive:
                var dr = Dist(_point.X, _point.Z, X, Z);
                if (dr > 1.3f) GoTo(_point.X, _point.Z, _pointFloor);
                else
                {
                    StandStill();
                    FaceToward(_point.X, _point.Z);
                    Activity = "helping someone up";
                    if (_actUntil > now + 5) _actUntil = now + 4.5;
                    else if (now > _actUntil - 0.2)
                    {
                        Emit(new NpcIntent(NpcIntentKind.Revive, NpcId: _reviveNpc));
                        EndReaction();
                    }
                }
                break;
        }
        UpdateMood();
    }

    /// <summary>
    /// Someone holding a hostage in front of them: only a brave police officer, close by (under 8 m), risks a careful
    /// shot (and the hostage may still take it). Everyone else holds fire.
    /// </summary>
    private bool TakesTheShot(float distance) =>
        HasGun && Person.Role == NpcRole.Police && Brave && distance < 8 && Person.Id % 3 == 0;

    /// <summary>
    /// The one they're after is out of sight: run to where they were last seen, then stop and look around; still
    /// nothing, and they search the area (leaning the way they were heading). A blink doesn't count. Returns false
    /// (carry on as usual) only for a moment's blink.
    /// </summary>
    private bool Chase(Suspect sus, float seconds)
    {
        var since = _clockSeconds - sus.LastSeen;
        if (since < 0.6) return false;
        if (_lookingSince < 0 && since < 15 && (Dist(sus.X, sus.Z, X, Z) > 1.3f || Floor != sus.Floor))
        {
            Activity = "chasing";
            GoTo(sus.X, sus.Z, sus.Floor);
            return true;
        }
        if (_lookingSince < 0)
        {
            _lookingSince = _clockSeconds;
            Say(Pick("Where'd they go?", "Where are you?!", "Lost them..."));
        }
        StandStill();
        Activity = "looking around";
        Yaw += seconds * 2.2f;
        if (_clockSeconds - _lookingSince > 3.5)
        {
            _lookingSince = -1;
            StartSearch(sus.X + sus.HeadingX * 5, sus.Z + sus.HeadingZ * 5, sus.Floor);
        }
        return true;
    }

    /// <summary>
    /// Arrived where trouble was reported: ask anyone around who saw it what they saw. Their accounts sharpen (or give)
    /// a description of the culprit.
    /// </summary>
    private void Interview()
    {
        if (_target == 0 || _crowd == null) return;
        if (!_suspects.TryGetValue(_target, out var sus)) _suspects[_target] = sus = new Suspect();
        var asked = 0;
        foreach (var other in _crowd.Npcs)
        {
            if (other == this || other.IsDown || Dist(other.X, other.Z, X, Z) > 8 || MathF.Abs(other.Y - Y) > 2) continue;
            if (other.DescriptionOf(_target) is not { } told) continue;
            var sure = told.Confidence * Witness.InterviewFactor;
            if (!sus.Grudge || sure > sus.Confidence) sus.Description = Witness.Retell(told.Look, told.Confidence, _random);
            sus.Confidence = sus.Grudge ? Witness.Combine(sus.Confidence, sure) : sure;
            sus.Grudge = sus.Confidence >= Witness.Useless;
            asked++;
        }
        if (asked > 0) Say(Pick("What did they look like?", "Which way did they go?", "Tell me exactly what you saw."));
    }

    private void StartSearchOrEnd(Suspect? sus)
    {
        if (sus != null && (OnDuty || Person.HasAuthorityOver(sus.Parcel))) StartSearch(sus.X, sus.Z, sus.Floor);
        else EndReaction();
    }

    private void StartSearch(float x, float z, int floor = 0)
    {
        var target = _target;
        Begin(Act.Search, target, "searching", OnDuty ? 1.4f : 1.1f, 20);
        (_point, _pointFloor) = ((x, z), floor);
        Say(Pick("Where did they go?", "I know you're here somewhere...", "Come out!"));
        SetRoute(_point, _pointFloor);
    }

    private string WarningFor(Suspect sus)
    {
        if ((sus.Conduct & Conduct.GunOut) != 0)
            return Person.Role == NpcRole.Police ? Pick("Police! Put the gun away. Now.", "Lower your weapon!") : Pick("Whoa, put that away!", "Hey, put that gun down!");
        if ((sus.Conduct & Conduct.TrespassInside) != 0)
            return Person.Role == NpcRole.Police ? "Police! Step outside, now." : Pick("What are you doing in here? Get out!", "Hey! This is private! Out!", "You can't be in here!");
        if ((sus.Conduct & Conduct.TrespassYard) != 0)
            return Pick("Hey! This is private property.", "Can I help you? Off my property.", "You can't be here. Leave.");
        if ((sus.Conduct & Conduct.Armed) != 0)
            return Pick("Put that away, will you?", "What's the weapon for? Put it away.");
        return Pick("Hey. What are you up to?", "Move along.");
    }

    // ================================================================ movement while reacting

    private void StandStill()
    {
        _route.Clear();
        _doorReady = false;
        Moving = false;
    }

    private void FaceToward(float x, float z)
    {
        float dx = x - X, dz = z - Z;
        if (dx * dx + dz * dz > 1e-4f) Yaw = MathF.Atan2(-dx, -dz);
    }

    /// <summary>Head for a moving point: replan every second or so, or when it has moved a few meters.</summary>
    private void GoTo(float x, float z, int floor = 0)
    {
        var moved = float.IsNaN(_plannedTo.X) || Dist(_plannedTo.X, _plannedTo.Z, x, z) > 2.5f || floor != _plannedFloor;
        if (_route.Count > 0 && !moved && _clockSeconds < _nextReplan) return;
        SetRoute((x, z), floor);
    }

    private void SetRoute((float X, float Z) to, int floor = 0)
    {
        (_plannedTo, _plannedFloor) = (to, floor);
        _nextReplan = _clockSeconds + 1.0;
        _route.Clear();
        _doorReady = false;
        foreach (var p in CityNav.Route(_city, _paths, Here, new Place(to.X, to.Z, floor), _lane, Obstacles, CanHop && _act is Act.Arrest or Act.Attack or Act.Flee))
            _route.Enqueue(p);
        var target = _city.ParcelAt(to.X, to.Z);
        _routeEndsIn = target != null && target.Footprint.Contains(to.X, to.Z) ? target.Id : -1;
        _routeLabel = Activity;
        _dwellHours = 0;
    }

    /// <summary>A locked door in the way of a reaction (and they can't open it): watch from here instead.</summary>
    private void DoorBlocked(int parcel, double time)
    {
        _route.Clear();
        _doorReady = false;
        if (_act is Act.Flee) { _reportOnArrival = true; _actUntil = Math.Min(_actUntil, _clockSeconds + 1); return; }
        if (_act is Act.Arrest or Act.Attack or Act.Confront) StartSearch(_city.Parcels[parcel].Door.X, _city.Parcels[parcel].Door.Z);
        else EndReaction();
    }

    private bool Near(Parcel p, float distance) => Dist(p.Door.X, p.Door.Z, X, Z) < distance;

    private static float Dist(float ax, float az, float bx, float bz) =>
        MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

    private void UpdateMood()
    {
        Mood = Held ? Mood.Alarmed : _act switch
        {
            Act.Attack => Mood.Hostile,
            Act.Arrest or Act.Holding or Act.Flee => Mood.Alarmed,
            Act.Confront or Act.Investigate or Act.Search or Act.Respond or Act.Revive => Mood.Suspicious,
            Act.Watch => Mood.Curious,
            _ => _suspects.Values.Any(s => s.Level >= Stealth.Curious) ? Mood.Curious : Mood.Calm,
        };
    }
}
