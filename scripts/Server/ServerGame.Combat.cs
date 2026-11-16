using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.Combat;
using ExploreTogether.Core.Items;
using ExploreTogether.Dungeon;
using ExploreTogether.Enemies;
using ExploreTogether.Net;
using ExploreTogether.Player;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Combat (phase 5), all decided by the server:
/// - Player attacks arrive as an aim direction; they're queued and resolved in the physics step with a
///   raycast in that instance's world (melee/tool 4 m, gun unlimited, magic places an area within 10 m).
/// - Enemies hurt players through InstanceManager.PlayerHitByEnemy.
/// - Death: back to the hub keeping items (not counted as escaped); hardcore deletes the character.
/// Numbers and rules: Core.Combat.CombatRules / EnemyBrain.
/// </summary>
public partial class ServerGame
{
    private sealed record PendingAttack(long Peer, Vector3 Direction);

    /// <summary>A placed magic area (port of the placed PlayerGhostAim portal with AreaOfEffect).</summary>
    private sealed class MagicAreaEffect
    {
        public required ServerInstance Instance { get; init; }
        public required Vector3 Center { get; init; }
        public required MagicStyle Style { get; init; }
        public required int Damage { get; init; }
        public required long OwnerPeer { get; init; }
        public double ExpiresAt { get; init; }
        public HashSet<EnemyCharacter> AlreadyHit { get; } = new();
    }

    private readonly Queue<PendingAttack> _pendingAttacks = new();
    private readonly List<MagicAreaEffect> _magicAreas = new();
    private readonly Random _combatRandom = new();

    private static double Now => Time.GetTicksMsec() / 1000.0;

    private void SetUpCombat()
    {
        _session.AttackRequested += (peer, dir) => _pendingAttacks.Enqueue(new PendingAttack(peer, dir));
        _session.ReloadRequested += OnReload;
        _session.AimReceived += OnAim;
        _instances.PlayerHitByEnemy += OnPlayerHitByEnemy;
        _instances.EnemyKilled += (instance, enemy) => GD.Print($"[server] {enemy.Name} killed in instance {instance.Id}");
    }

    public override void _PhysicsProcess(double delta)
    {
        while (_pendingAttacks.TryDequeue(out var attack))
            ResolveAttack(attack);
        TickMagicAreas();
    }

    private void ResolveAttack(PendingAttack attack)
    {
        if (!_sessions.TryGetValue(attack.Peer, out var s) || s.Gear == null || s.Health.IsDead) return;
        if (_instances.FindPlayer(attack.Peer) is not { } found) return;
        var weapon = s.Gear.EquippedWeapon;
        if (CombatRules.ProfileFor(weapon) is not { } profile) return;
        if (Now - s.LastAttackAt < profile.Cooldown) return;
        s.LastAttackAt = Now;

        var dir = attack.Direction.LengthSquared() > 0.0001f ? attack.Direction.Normalized() : -found.Player.GlobalBasis.Z;
        var origin = found.Player.GlobalPosition + new Vector3(0, 1.6f, 0); // the player's head (camera height)
        var space = found.Player.GetWorld3D().DirectSpaceState;

        switch (profile.Class)
        {
            case ItemClass.Gun:
                // The rounds are in this particular gun (they go with it if it changes hands).
                if (!GunRules.TryFire(s.Gear.WeaponSlot))
                {
                    _session.SendNotice(s.Peer, "Out of ammo. Press R to reload.");
                    return;
                }
                SendAmmoState(s);
                HitScan(s, found.Instance, space, origin, dir, profile, weapon!);
                break;

            case ItemClass.Melee:
                HitScan(s, found.Instance, space, origin, dir, profile, weapon!);
                break;

            case ItemClass.Tool:
                // Port of Tool.Attack: hit an enemy, otherwise use whatever interactable is in reach.
                if (!HitScan(s, found.Instance, space, origin, dir, profile, weapon!))
                {
                    var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(origin, origin + dir * profile.Range, collisionMask: 1 | 4));
                    if (hit.Count > 0 && hit["collider"].AsGodotObject() is Interactable interactable)
                        OnInteract(s.Peer, interactable.InteractionId);
                }
                break;

            case ItemClass.Magic:
                CastMagic(s, found.Instance, space, origin, dir, weapon!);
                break;
        }
    }

    /// <summary>Ray from the head: walls block it; the first enemy hit takes the damage. Returns true if an enemy was hit.</summary>
    private bool HitScan(PeerSession s, ServerInstance instance, PhysicsDirectSpaceState3D space,
        Vector3 origin, Vector3 dir, WeaponProfile profile, ItemDef weapon)
    {
        var query = PhysicsRayQueryParameters3D.Create(origin, origin + dir * profile.Range, collisionMask: 1 | 8);
        var hit = space.IntersectRay(query);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not EnemyCharacter enemy) return false;
        if (!instance.Enemies.ContainsKey(enemy.Name) || enemy.IsDead) return false;

        var killed = enemy.TakeDamage(profile.Damage, profile.Knockback ? dir : null);
        GD.Print($"[server] '{s.Character!.Name}' hit {enemy.Name} with {weapon.Name} for {profile.Damage} ({enemy.NetHealth} left){(killed ? ", killed" : "")}");
        return true;
    }

    /// <summary>
    /// Port of Magic.Attack + PlayerGhostAim.PlacePortal: the area goes where the aim ray meets the world
    /// within 10 m; it rolls Random.Range(0, max_damage) once and applies it to each enemy that enters.
    /// </summary>
    private void CastMagic(PeerSession s, ServerInstance instance, PhysicsDirectSpaceState3D space,
        Vector3 origin, Vector3 dir, ItemDef weapon)
    {
        if (Combat.MagicAim.FindPlacement(space, origin, dir) is not { } spot) return; // nowhere to place it

        var style = CombatRules.MagicStyleOf(weapon);
        var area = new MagicAreaEffect
        {
            Instance = instance,
            Center = spot,
            Style = style,
            Damage = CombatRules.RollMagicDamage(_combatRandom),
            OwnerPeer = s.Peer,
            ExpiresAt = Now + CombatRules.MagicAreaLifetime,
        };
        _magicAreas.Add(area);
        foreach (var peer in instance.Players.Keys)
            _session.SendMagicArea(peer, area.Center, (int)style, CombatRules.MagicAreaLifetime, CasterColor(s));
        GD.Print($"[server] '{s.Character!.Name}' cast {weapon.Name} ({style}, {area.Damage} damage) at {area.Center.Snapped(Vector3.One * 0.1f)}");
    }

    /// <summary>
    /// A player's magic ghost moved (or they stopped aiming). Only accepted while they hold a wand and the
    /// spot is within reach; it's then replicated to the others in the instance via the player node.
    /// </summary>
    private void OnAim(long peer, int instanceId, bool aiming, Vector3 position)
    {
        if (!_sessions.TryGetValue(peer, out var s) || _instances.FindPlayer(peer) is not { } found) return;
        if (found.Instance.Id != instanceId) return; // late packet from a previous instance
        var player = found.Player;
        var head = player.GlobalPosition + new Vector3(0, 1.6f, 0);
        var valid = aiming && s.Gear?.EquippedWeapon?.Class == ItemClass.Magic && position.IsFinite()
                    && head.DistanceTo(position) <= CombatRules.MagicPlaceRange + 4f; // + the drop to the floor
        player.AimActive = valid;
        if (valid) player.AimPoint = position;
    }

    /// <summary>The caster's chosen character color (their pentagram is drawn in it).</summary>
    private Color CasterColor(PeerSession s) =>
        _instances.FindPlayer(s.Peer)?.Player.PlayerColor ?? ColorFromHex(s.Character?.ColorHex ?? "ffffff");

    private void TickMagicAreas()
    {
        var now = Now;
        _magicAreas.RemoveAll(a => now >= a.ExpiresAt || _instances.Get(a.Instance.Id) == null);
        foreach (var area in _magicAreas)
        {
            foreach (var enemy in area.Instance.Enemies.Values.ToList())
            {
                if (enemy.IsDead || area.AlreadyHit.Contains(enemy)) continue;
                var offset = enemy.GlobalPosition - area.Center;
                if (new Vector2(offset.X, offset.Z).Length() > CombatRules.MagicAreaRadius + EnemyCharacter.Radius || Mathf.Abs(offset.Y) > 2f)
                    continue;

                // AreaOfEffect.Effect: damage once on entering; Frost freezes; Blood heals the caster.
                area.AlreadyHit.Add(enemy);
                var killed = enemy.TakeDamage(area.Damage);
                if (area.Style == MagicStyle.Frost && !killed) enemy.Freeze(CombatRules.FrostFreezeSeconds);
                if (area.Style == MagicStyle.Blood && _sessions.TryGetValue(area.OwnerPeer, out var owner))
                {
                    owner.Health.Heal(CombatRules.BloodHealPerHit);
                    SyncHealth(owner);
                }
                GD.Print($"[server] magic ({area.Style}) hit {enemy.Name} for {area.Damage} ({enemy.NetHealth} left){(killed ? ", killed" : "")}");
            }
        }
    }

    /// <summary>R: move rounds from the inventory into the equipped gun.</summary>
    private void OnReload(long peer)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Inventory == null || s.Gear?.EquippedWeapon is not { Class: ItemClass.Gun } gun) return;
        var full = GunRules.Loaded(s.Gear.WeaponSlot) >= (gun.MagazineSize ?? 0);
        if (GunRules.Reload(s.Gear.WeaponSlot, gun, s.Inventory) == 0)
        {
            _session.SendNotice(peer, full ? "Already loaded." : $"No {ItemCatalog.NameOf(gun.AmmoItemId ?? -1)} to reload with.");
            return;
        }
        SendInventory(s); // the rounds moved from the inventory into the gun (also sends the ammo state)
        SaveInventory(s);
    }

    /// <summary>The equipped gun's loaded rounds and the matching rounds carried; (-1, -1) if no gun is equipped.</summary>
    private void SendAmmoState(PeerSession s)
    {
        if (s.Gear?.EquippedWeapon is { Class: ItemClass.Gun } gun && s.Inventory != null)
            _session.SendAmmo(s.Peer, GunRules.Loaded(s.Gear.WeaponSlot), GunRules.Reserve(s.Inventory, gun));
        else
            _session.SendAmmo(s.Peer, -1, -1);
    }

    // ------------------------------------------------------------ taking damage

    private void OnPlayerHitByEnemy(ServerInstance instance, EnemyCharacter enemy, PlayerCharacter player, int damage)
    {
        if (!_sessions.TryGetValue(player.OwnerPeerId, out var s) || s.Character == null || s.Health.IsDead) return;
        if (_instances.FindPlayer(s.Peer)?.Instance != instance) return; // left meanwhile

        var died = s.Health.Damage(damage);
        SyncHealth(s);
        GD.Print($"[server] {enemy.Name} hit '{s.Character.Name}' for {damage} ({s.Health.Current} left)");
        if (died) HandlePlayerDeath(s, instance);
    }

    /// <summary>Copies the session's health onto the player node (replicated to clients).</summary>
    private void SyncHealth(PeerSession s)
    {
        if (_instances.FindPlayer(s.Peer) is { } found) found.Player.NetHealth = s.Health.Current;
    }

    private void HandlePlayerDeath(PeerSession s, ServerInstance instance)
    {
        var c = s.Character!;
        NotifyInstance(instance, c.Hardcore ? $"{c.Name} died. (Hardcore: gone for good.)" : $"{c.Name} died!", except: s.Peer);

        if (!c.Hardcore)
        {
            // Back to the hub with everything they carry; this run doesn't count them as escaped.
            GD.Print($"[server] '{c.Name}' died in instance {instance.Id}; returning to the hub");
            _session.SendNotice(s.Peer, "You died! You wake up back in the hub.");
            SendToInstance(s, _instances.Hub);
            return;
        }

        // Hardcore (original Manager: DeleteCharacterData + clear inventory and gear).
        GD.Print($"[server] hardcore character '{c.Name}' died in instance {instance.Id}; deleting it");
        OnLeavingDungeon(s, instance, connected: true); // the Dungeon Pass still drops for the team
        _instances.RemovePeer(s.Peer);
        OnLeftDungeon(instance);
        _db.DeleteCharacter(c.Id);
        s.Character = null;
        s.Inventory = null;
        s.Gear = null;
        s.PendingInstanceId = null;
        _session.SendCharacterLost(s.Peer, $"{c.Name} died and was a hardcore character. They are gone for good.");
        SendCharacterList(s);
    }
}
