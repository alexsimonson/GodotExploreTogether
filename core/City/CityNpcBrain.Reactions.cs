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

public readonly record struct NpcIntent(NpcIntentKind Kind, long Player = 0, float X = 0, float Z = 0, float Radius = 0,
    int Severity = 0, string Text = "", int NpcId = -1)
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
        public int Parcel = -1;
        public Conduct Conduct;
        public double LastSeen = -1000;
        public bool Visible;
        /// <summary>Alarmed by them before: recognized on sight.</summary>
        public bool Grudge;
        public bool Reported;
        public bool Resisted;
    }

    private Act _act;
    private double _clockSeconds;
    private float _pace = 1;
    private long _target;
    private (float X, float Z) _point;
    private double _actStarted, _actUntil, _nextReplan, _nextAttack, _grabPendingUntil;
    private (float X, float Z) _plannedTo = (float.NaN, float.NaN);
    private bool _warned, _arrestWarned, _reportOnArrival, _detainOnly;
    private int _reportSeverity, _reviveNpc = -1;
    private readonly Dictionary<long, Suspect> _suspects = new();
    private readonly HashSet<int> _knownBodies = new(), _knownDoors = new();
    private readonly Queue<NpcIntent> _intents = new();
    private long _attackedBy;

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
    public Func<float, float, float, float, CityNav.Obstacle>? Obstacles { get; set; }
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
        if (Obstacles?.Invoke(to.Item1, to.Item2, to.Item1 + ux * 0.4f, to.Item2 + uz * 0.4f) == CityNav.Obstacle.Wall) return false;
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
        Y = HopHeight * 4 * hop.T * (1 - hop.T);
        if (hop.T < 1) return;
        Y = 0;
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
            (sus.X, sus.Z, sus.Parcel, sus.Conduct, sus.LastSeen, sus.Visible) = (seen.X, seen.Z, seen.Parcel, seen.Conduct, now, true);
            var (rate, severity, reason) = Stealth.Judge(Person, seen, _city, hour);
            if (sus.Grudge && sus.Level < Stealth.Alarmed)
            {
                (rate, reason) = (float.PositiveInfinity, "recognized");
                severity = Math.Max(severity, sus.Severity);
            }
            if (rate > 0)
            {
                sus.Level = float.IsPositiveInfinity(rate) ? Stealth.Alarmed : MathF.Min(Stealth.Alarmed, sus.Level + rate * seconds);
                if (severity >= sus.Severity) (sus.Severity, sus.Reason) = (severity, reason);
            }
            else if (_act != Act.Attack) sus.Level = MathF.Max(0, sus.Level - Stealth.Fade * seconds);
        }
        foreach (var s in _suspects.Values)
            if (!s.Visible && !s.Grudge) s.Level = MathF.Max(0, s.Level - Stealth.Fade * 0.5f * seconds);

        foreach (var body in p.Bodies)
            if (_knownBodies.Add(body.NpcId)) SeeBody(body);
        foreach (var door in p.DamagedDoors)
            if (_knownDoors.Add(door)) SeeDamagedDoor(door);

        Decide();
        UpdateMood();
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
        sus.Grudge = true;
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
        Emit(new NpcIntent(NpcIntentKind.Report, player, sus.X, sus.Z, Severity: Math.Max(sus.Severity, Severity.Suspicious), Text: sus.Reason));
    }

    private void HearNoise(Noise n)
    {
        if (_act is Act.Attack or Act.Holding or Act.Arrest or Act.Flee) return;
        var mine = _city.ParcelAt(n.X, n.Z) is { } p && Person.HasAuthorityOver(p.Id) && p.Id >= 0;
        if (n.Severity >= Severity.Violent)
        {
            if (OnDuty) StartRespond(n.X, n.Z, n.Player, n.Severity);
            else if (Timid || !Brave && !mine) StartFleeFrom(n.X, n.Z, report: Person.Has(NpcTraits.Nosy) || mine);
            else StartInvestigate(n.X, n.Z, "investigating", 1.6f);
            return;
        }
        if (n.Cause == "footsteps" && !mine) return;
        if (mine || OnDuty || Person.Has(NpcTraits.Nosy) || n.Cause is "scream" or "shout" && Brave)
        {
            if (mine) Say(Pick("Who's there?", "Hello? Someone there?", "I heard that!"));
            StartInvestigate(n.X, n.Z, "investigating", mine || OnDuty ? 1.5f : 1f);
            if (mine && n.Severity >= Severity.Crime) Emit(new NpcIntent(NpcIntentKind.Report, n.Player, n.X, n.Z, Severity: Severity.Crime, Text: "break-in"));
        }
        else if (_act == Act.None) FaceToward(n.X, n.Z);
    }

    private void SeeBody(BodySighting b)
    {
        if (_act is Act.Attack or Act.Holding or Act.Arrest or Act.Flee) return;
        // Anyone near it looks bad.
        foreach (var s in _suspects.Values)
            if (s.Visible && Dist(s.X, s.Z, b.X, b.Z) < 8)
            {
                s.Level = MathF.Min(Stealth.Alarmed, s.Level + 40);
                if (s.Severity < Severity.Crime) (s.Severity, s.Reason) = (Severity.Crime, "near a body");
            }
        if (!b.Dead && (Brave || OnDuty || Person.Has(NpcTraits.Nosy)) && !Timid)
        {
            Say(Pick("Hey! Are you all right?", "Oh no, wake up!"));
            Begin(Act.Revive, 0, "helping", 1.8f, 30);
            (_point, _reviveNpc) = ((b.X, b.Z), b.NpcId);
            Emit(new NpcIntent(NpcIntentKind.Report, 0, b.X, b.Z, Severity: Severity.Crime, Text: "someone knocked out"));
            return;
        }
        if (b.Dead)
        {
            Say(Pick("Aaah! Somebody help!", "Oh my god, he's dead!", "Murder! MURDER!"));
            Emit(NpcIntent.MakeNoise(X, Z, Stealth.ScreamRadius, "scream", Severity.Crime));
        }
        Emit(new NpcIntent(NpcIntentKind.Report, 0, b.X, b.Z, Severity: b.Dead ? Severity.Violent : Severity.Crime, Text: b.Dead ? "a body" : "someone knocked out"));
        if (OnDuty) StartInvestigate(b.X, b.Z, "examining the body", 1.6f);
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
        if (!Timid) StartInvestigate(p.Spots[0].X, p.Spots[0].Z, "checking the house", 1.3f);
    }

    // ================================================================ calls from the server

    /// <summary>
    /// The police are told about trouble. Every resident told notes who it was (recognized on sight from now on);
    /// with <paramref name="respond"/>, an officer who isn't busy goes to look. Returns true if they're on their way.
    /// </summary>
    public bool Dispatch(float x, float z, long player, int severity, bool respond = true)
    {
        if (player != 0)
        {
            if (!_suspects.TryGetValue(player, out var sus)) _suspects[player] = sus = new Suspect();
            sus.Severity = Math.Max(sus.Severity, severity);
            sus.Reason = sus.Reason == "" ? "wanted" : sus.Reason;
            sus.Reported = true;
            if (severity >= Severity.Crime) sus.Grudge = true; // a description went out: recognized on sight
            (sus.X, sus.Z) = (x, z);
        }
        if (!respond || Person.Role != NpcRole.Police || IsDown || Held || Asleep) return false;
        if (_act is not (Act.None or Act.Watch or Act.Search or Act.Investigate)) return false;
        StartRespond(x, z, player, severity);
        Say(Pick("On my way.", "Copy that.", "Heading there now."));
        return true;
    }

    /// <summary>A player hurt or tried to hurt them: they know who did it.</summary>
    public void Attacked(long player, float px, float pz)
    {
        _attackedBy = player;
        if (IsDown || Held) return;
        if (!_suspects.TryGetValue(player, out var sus)) _suspects[player] = sus = new Suspect();
        (sus.X, sus.Z, sus.LastSeen, sus.Level, sus.Severity, sus.Reason) = (px, pz, _clockSeconds, Stealth.Alarmed, Severity.Violent, "attacked me");
        if (_act == Act.Holding) Emit(new NpcIntent(NpcIntentKind.Release, _target));
        _act = Act.None;
        Escalate(player, sus);
        UpdateMood();
    }

    /// <summary>A player took hold of them (<paramref name="surprised"/>: from behind, unseen: they can't cry out).</summary>
    public void GrabbedBy(long player, float px, float pz, bool surprised)
    {
        if (_act == Act.Holding) Emit(new NpcIntent(NpcIntentKind.Release, _target));
        EndReaction(returnToRoutine: false);
        Held = true;
        Moving = false;
        _route.Clear();
        _doorReady = false;
        Activity = "struggling";
        if (!_suspects.TryGetValue(player, out var sus)) _suspects[player] = sus = new Suspect();
        (sus.X, sus.Z, sus.LastSeen, sus.Level, sus.Severity, sus.Reason, sus.Grudge) =
            (px, pz, _clockSeconds, Stealth.Alarmed, Severity.Violent, "grabbed me", !surprised);
        if (!surprised)
        {
            Say(Pick("Get off me!", "Help! HELP!", "Let go of me!"));
            Emit(NpcIntent.MakeNoise(X, Z, Stealth.ShoutRadius, "shout", Severity.Crime));
        }
        Mood = Mood.Alarmed;
    }

    /// <summary>A held resident moves where they're pulled.</summary>
    public void MoveHeld(float x, float z, float yaw)
    {
        if (Held) (X, Z, Yaw) = (x, z, yaw);
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
            sus.Grudge = true;
            Escalate(player, sus);
        }
        UpdateMood();
    }

    /// <summary>The server's answer to a <see cref="NpcIntentKind.Grab"/>.</summary>
    public void GrabResult(long player, bool held)
    {
        _grabPendingUntil = 0;
        if (_act != Act.Arrest || player != _target) return;
        if (!held) return;
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
                Emit(new NpcIntent(NpcIntentKind.Report, player, sus.X, sus.Z, Severity: Severity.Violent, Text: "resisting arrest"));
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
        sus.Grudge = true;
        sus.Reported = false;
        Say(Pick("Ugh... my head... POLICE!", "Help! I was attacked!"));
        Escalate(_attackedBy, sus);
    }

    // ================================================================ starting reactions

    private void Begin(Act act, long target, string activity, float pace, double maxSeconds)
    {
        if (_hop != null) { (X, Z, Y, _hop) = (_hop.To.X, _hop.To.Z, 0, null); }
        if (_act == Act.Holding && act != Act.Holding) Emit(new NpcIntent(NpcIntentKind.Release, _target));
        _act = act;
        _target = target;
        _pace = pace;
        _actStarted = _clockSeconds;
        _actUntil = _clockSeconds + maxSeconds;
        _nextReplan = 0;
        _plannedTo = (float.NaN, float.NaN);
        _warned = _arrestWarned = false;
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
        Say(Pick("Help! Police!", "Somebody help!", "Aaah!"));
        Emit(NpcIntent.MakeNoise(X, Z, Stealth.ScreamRadius, "scream", Severity.Crime));
        _point = (x, z);
        _reportOnArrival = report;
        _reportSeverity = severity;
        if (sus != null) sus.Reported = true;
        // Home, unless that's where the trouble is (or they're nowhere near it); otherwise away along the streets.
        var home = Person.Home >= 0 ? _city.Parcels[Person.Home] : null;
        if (home != null && Dist(home.Door.X, home.Door.Z, x, z) > 20 && Dist(home.Door.X, home.Door.Z, X, Z) < 90)
            _plannedTo = home.Spots[_random.Next(home.Spots.Count)];
        else
        {
            var far = _paths.StreetCells.Where(c =>
            {
                var (cx, cz) = CityLayout.CellCenter(c);
                return Dist(cx, cz, x, z) > 30 && Dist(cx, cz, X, Z) < 60;
            }).ToList();
            var cell = far.Count > 0 ? far[_random.Next(far.Count)] : RandomStreetCell();
            _plannedTo = CityLayout.CellCenter(cell);
        }
        SetRoute(_plannedTo);
    }

    private void StartInvestigate(float x, float z, string activity, float pace)
    {
        Begin(Act.Investigate, 0, activity, pace, 40);
        _point = (x, z);
    }

    private void StartRespond(float x, float z, long player, int severity)
    {
        Begin(Act.Respond, player, "responding", severity >= Severity.Crime ? 2.6f : 1.8f, 120);
        _point = (x, z);
    }

    /// <summary>Calm again: back to the day's routine, finding the way back to the street (or their building) first.</summary>
    private void EndReaction(bool returnToRoutine = true)
    {
        if (_act == Act.None) return;
        if (_hop != null) { (X, Z, Y, _hop) = (_hop.To.X, _hop.To.Z, 0, null); }
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
            if (_act == Act.Arrest && sus != null) StartSearch(sus.X, sus.Z);
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
                if (d > 2.5f) GoTo(sus.X, sus.Z);
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
                if (now - sus.LastSeen > 10) { StartSearch(sus.X, sus.Z); break; }
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
                else GoTo(sus.X, sus.Z);
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
                if (now - sus.LastSeen > 12) { StartSearch(sus.X, sus.Z); break; }
                var dt = Dist(sus.X, sus.Z, X, Z);
                WeaponOut = true;
                if (HasGun)
                {
                    if (!sus.Visible || dt > 18) GoTo(sus.X, sus.Z);
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
                    if (dt > 1.5f) GoTo(sus.X, sus.Z);
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
                    Emit(new NpcIntent(NpcIntentKind.Report, _target, _point.X, _point.Z, Severity: _reportSeverity, Text: "a call for help"));
                }
                if (_route.Count == 0)
                {
                    if (_reportOnArrival)
                    {
                        _reportOnArrival = false;
                        Activity = "calling the police";
                        Say(Pick("Hello, police? Come quick!", "Police! There's someone here!"));
                        Emit(new NpcIntent(NpcIntentKind.Report, _target, _point.X, _point.Z, Severity: _reportSeverity, Text: "a call for help"));
                        _actUntil = now + 6;
                        StandStill();
                    }
                    else if (now > _actUntil - 0.1 || Activity != "calling the police") EndReaction();
                }
                break;

            case Act.Investigate:
            case Act.Respond:
                if (_route.Count == 0 && Dist(_point.X, _point.Z, X, Z) > 2f && double.IsNaN(_plannedTo.X)) SetRoute(_point);
                else if (_route.Count == 0)
                {
                    if (_act == Act.Respond) StartSearch(_point.X, _point.Z);
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
                    var a = _random.NextDouble() * Math.PI * 2;
                    var r = 3 + _random.NextDouble() * 7;
                    var spot = ((float)(_point.X + Math.Cos(a) * r), (float)(_point.Z + Math.Sin(a) * r));
                    if (Obstacles?.Invoke(X, Z, spot.Item1, spot.Item2) is CityNav.Obstacle.Wall) spot = _point;
                    SetRoute(spot);
                }
                break;

            case Act.Revive:
                var dr = Dist(_point.X, _point.Z, X, Z);
                if (dr > 1.3f) GoTo(_point.X, _point.Z);
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

    private void StartSearchOrEnd(Suspect? sus)
    {
        if (sus != null && (OnDuty || Person.HasAuthorityOver(sus.Parcel))) StartSearch(sus.X, sus.Z);
        else EndReaction();
    }

    private void StartSearch(float x, float z)
    {
        var target = _target;
        Begin(Act.Search, target, "searching", OnDuty ? 1.4f : 1.1f, 20);
        _point = (x, z);
        Say(Pick("Where did they go?", "I know you're here somewhere...", "Come out!"));
        SetRoute(_point);
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
    private void GoTo(float x, float z)
    {
        var moved = float.IsNaN(_plannedTo.X) || Dist(_plannedTo.X, _plannedTo.Z, x, z) > 2.5f;
        if (_route.Count > 0 && !moved && _clockSeconds < _nextReplan) return;
        SetRoute((x, z));
    }

    private void SetRoute((float X, float Z) to)
    {
        _plannedTo = to;
        _nextReplan = _clockSeconds + 1.0;
        _route.Clear();
        _doorReady = false;
        foreach (var p in CityNav.Route(_city, _paths, (X, Z), to, _lane, Obstacles, CanHop && _act is Act.Arrest or Act.Attack or Act.Flee))
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
