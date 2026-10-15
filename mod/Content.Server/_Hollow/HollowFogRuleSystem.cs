using Content.Server.Chat.Systems;
using Content.Shared.GameTicking.Rules;
using Content.Server.Light.EntitySystems;
using Content.Shared.Station.Systems;
using Content.Shared._Hollow;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Light.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Station.Components;
using Content.Shared.Storage;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Random;

namespace Content.Server._Hollow;

/// <summary>
/// Runs the Hollow Fog game mode: the station sits in thick fog, and every so often a siren
/// sounds, the lights bleed red and the Otherworld's creatures come out until the fog returns.
/// </summary>
public sealed partial class HollowFogRuleSystem : GameRuleSystem<HollowFogRuleComponent>
{
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private PoweredLightSystem _poweredLight = default!;
    [Dependency] private LightBulbSystem _bulb = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;

    private static readonly string[] StarterItemSlots = ["pocket1", "pocket2"];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    protected override void Started(Entity<HollowFogRuleComponent, GameRuleComponent> rule, ref GameRuleStartedEvent args)
    {
        base.Started(rule, ref args);

        var comp = rule.Comp1;

        foreach (var station in _station.GetStations())
        {
            foreach (var grid in station.Comp.Grids)
            {
                if (Transform(grid).MapUid is not { } map)
                    continue;

                comp.AffectedMaps.Add(map);
                EnsureComp<HollowWorldComponent>(map);
            }
        }

        SetPhase(comp, HollowPhase.Fog);
        comp.NextPhaseTime = Timing.CurTime + RollFogDuration(comp);

        // Rule was added mid-round by an admin: hand radios to everyone already playing.
        if (GameTicker.RunLevel == GameRunLevel.InRound)
        {
            var query = EntityQueryEnumerator<ActorComponent, InventoryComponent>();
            while (query.MoveNext(out var uid, out _, out _))
            {
                if (_mobState.IsAlive(uid) && IsOnAffectedMap(comp, uid))
                    GiveStarterItem(comp, uid);
            }
        }

        _chat.DispatchGlobalAnnouncement(Loc.GetString("hollow-announcement-start"),
            Loc.GetString("hollow-announcement-sender"),
            colorOverride: Color.LightGray);
    }

    protected override void Ended(Entity<HollowFogRuleComponent> rule, ref GameRuleEndedEvent args)
    {
        base.Ended(rule, ref args);

        var comp = rule.Comp;
        ReturnToFog(comp, false);

        foreach (var map in comp.AffectedMaps)
        {
            RemComp<HollowWorldComponent>(map);
        }

        comp.AffectedMaps.Clear();
    }

    protected override void ActiveTick(EntityUid uid, HollowFogRuleComponent component, GameRuleComponent gameRule, float frameTime)
    {
        base.ActiveTick(uid, component, gameRule, frameTime);

        if (Timing.CurTime < component.NextPhaseTime)
            return;

        AdvancePhase(component);
    }

    /// <summary>
    /// Moves the rule on to its next phase: fog, then siren, then Otherworld, then back to fog.
    /// </summary>
    public void AdvancePhase(HollowFogRuleComponent comp)
    {
        switch (comp.Phase)
        {
            case HollowPhase.Fog:
                StartSiren(comp);
                comp.NextPhaseTime = Timing.CurTime + comp.SirenDuration;
                break;
            case HollowPhase.Siren:
                StartOtherworld(comp);
                comp.NextPhaseTime = Timing.CurTime + comp.OtherworldDuration;
                break;
            case HollowPhase.Otherworld:
                ReturnToFog(comp, true);
                comp.NextPhaseTime = Timing.CurTime + RollFogDuration(comp);
                break;
        }
    }

    private void StartSiren(HollowFogRuleComponent comp)
    {
        SetPhase(comp, HollowPhase.Siren);
        _audio.PlayGlobal(comp.SirenSound, Filter.Broadcast(), true);

        // Every light on the station starts to stutter while the siren wails.
        var stopBlinking = Timing.CurTime + comp.SirenDuration;
        var query = EntityQueryEnumerator<PoweredLightComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapUid is not { } map || !comp.AffectedMaps.Contains(map))
                continue;

            var blink = EnsureComp<BlinkingPoweredLightComponent>(uid);
            blink.StopBlinkingTime = stopBlinking;
            Dirty(uid, blink);
        }
    }

    private void StartOtherworld(HollowFogRuleComponent comp)
    {
        SetPhase(comp, HollowPhase.Otherworld);
        RecolorLights(comp);
        SpawnMonsters(comp);

        _chat.DispatchGlobalAnnouncement(Loc.GetString("hollow-announcement-otherworld"),
            Loc.GetString("hollow-announcement-sender"),
            playSound: false,
            colorOverride: comp.OtherworldAnnouncementColor);
    }

    private void ReturnToFog(HollowFogRuleComponent comp, bool announce)
    {
        var wasOtherworld = comp.Phase != HollowPhase.Fog;
        SetPhase(comp, HollowPhase.Fog);
        RestoreLights(comp);

        foreach (var monster in comp.SpawnedMonsters)
        {
            if (TerminatingOrDeleted(monster))
                continue;

            SpawnAtPosition(comp.DissolveEffect, Transform(monster).Coordinates);
            QueueDel(monster);
        }

        comp.SpawnedMonsters.Clear();

        if (!wasOtherworld || !announce)
            return;

        comp.ShiftsCompleted++;
        _audio.PlayGlobal(comp.ReturnSound, Filter.Broadcast(), true);
        _chat.DispatchGlobalAnnouncement(Loc.GetString("hollow-announcement-fog-returns"),
            Loc.GetString("hollow-announcement-sender"),
            playSound: false,
            colorOverride: Color.LightGray);
    }

    private void SetPhase(HollowFogRuleComponent comp, HollowPhase phase)
    {
        comp.Phase = phase;

        foreach (var map in comp.AffectedMaps)
        {
            if (!TryComp<HollowWorldComponent>(map, out var world))
                continue;

            world.Phase = phase;
            Dirty(map, world);
        }
    }

    private void RecolorLights(HollowFogRuleComponent comp)
    {
        var query = EntityQueryEnumerator<PoweredLightComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var light, out var xform))
        {
            if (xform.MapUid is not { } map || !comp.AffectedMaps.Contains(map))
                continue;

            if (_poweredLight.GetBulb(uid, light) is not { } bulbUid
                || !TryComp<LightBulbComponent>(bulbUid, out var bulb))
            {
                continue;
            }

            comp.OriginalBulbColors.TryAdd(bulbUid, bulb.Color);
            _bulb.SetColor(bulbUid, comp.OtherworldLightColor, bulb);
            // Re-applies the bulb to the fixture's point light without touching its switch.
            _poweredLight.SetState(uid, light.On, light);
        }
    }

    private void RestoreLights(HollowFogRuleComponent comp)
    {
        foreach (var (bulbUid, color) in comp.OriginalBulbColors)
        {
            if (TerminatingOrDeleted(bulbUid) || !TryComp<LightBulbComponent>(bulbUid, out var bulb))
                continue;

            _bulb.SetColor(bulbUid, color, bulb);

            // Refresh the fixture the bulb is in, if it's still in one.
            var fixture = Transform(bulbUid).ParentUid;
            if (TryComp<PoweredLightComponent>(fixture, out var light))
                _poweredLight.SetState(fixture, light.On, light);
        }

        comp.OriginalBulbColors.Clear();
    }

    private void SpawnMonsters(HollowFogRuleComponent comp)
    {
        var players = 0;
        var query = EntityQueryEnumerator<ActorComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (_mobState.IsAlive(uid) && IsOnAffectedMap(comp, uid))
                players++;
        }

        var count = (int) MathF.Round(players * comp.MonstersPerPlayer)
                    + comp.ShiftsCompleted * comp.EscalationPerShift;
        count = Math.Clamp(count, comp.MinMonsters, comp.MaxMonsters);

        var stations = _station.GetStations();
        if (stations.Count == 0)
            return;

        for (var i = 0; i < count; i++)
        {
            var station = RobustRandom.Pick(stations);
            if (!_station.TryFindRandomTileOnStation(station, out _, out _, out var coords))
                continue;

            foreach (var proto in EntitySpawnCollection.GetSpawns(comp.Monsters, RobustRandom))
            {
                comp.SpawnedMonsters.Add(SpawnAtPosition(proto, coords));
            }
        }
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        var query = QueryActiveRules();
        while (query.MoveNext(out _, out var comp, out _, out _))
        {
            GiveStarterItem(comp, ev.Mob);
        }
    }

    private void GiveStarterItem(HollowFogRuleComponent comp, EntityUid mob)
    {
        if (comp.StarterItem is not { } item)
            return;

        foreach (var slot in StarterItemSlots)
        {
            if (_inventory.SpawnItemInSlot(mob, slot, item, silent: true))
                return;
        }

        _hands.PickupOrDrop(mob, SpawnAtPosition(item, Transform(mob).Coordinates), checkActionBlocker: false);
    }

    private bool IsOnAffectedMap(HollowFogRuleComponent comp, EntityUid uid)
    {
        return Transform(uid).MapUid is { } map && comp.AffectedMaps.Contains(map);
    }

    private TimeSpan RollFogDuration(HollowFogRuleComponent comp)
    {
        return RobustRandom.Next(comp.MinFogDuration, comp.MaxFogDuration);
    }
}
