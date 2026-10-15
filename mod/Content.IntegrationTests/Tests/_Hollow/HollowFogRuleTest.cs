using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._Hollow;
using Content.Server.GameTicking;
using Content.Shared._Hollow;
using Content.Shared.GameTicking;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Hollow;

[TestFixture]
[TestOf(typeof(HollowFogRuleSystem))]
public sealed class HollowFogRuleTest : GameTest
{
    private const string Preset = "HollowFog";
    private const string LightFixture = "Poweredlight";

    public override PoolSettings PoolSettings => new()
    {
        Dirty = true,
        DummyTicker = false,
        Connected = true,
        InLobby = true,
        // A real (small) station with air, so monsters have somewhere to spawn.
        Map = "Dev",
    };

    /// <summary>
    /// Runs a round of Hollow Fog through a full fog -> siren -> Otherworld -> fog cycle.
    /// </summary>
    [Test]
    public async Task FullCycleTest()
    {
        var pair = Pair;
        var server = pair.Server;
        var client = pair.Client;
        var entMan = server.EntMan;
        var ticker = server.System<ServerGameTicker>();
        var hollow = server.System<HollowFogRuleSystem>();
        var lights = server.System<SharedPoweredLightSystem>();

        Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.PreRoundLobby));

        await server.WaitPost(() =>
        {
            ticker.SetGamePreset(Preset);
            ticker.ToggleReadyAll(true);
            ticker.StartRound();
        });
        await pair.RunTicksSync(10);

        Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
        var player = pair.Player!.AttachedEntity!.Value;

        EntityUid map = default;
        HollowFogRuleComponent rule = null;
        EntityUid bulb = default;
        Color originalColor = default;

        await server.WaitAssertion(() =>
        {
            var rules = entMan.AllEntities<HollowFogRuleComponent>().ToList();
            Assert.That(rules, Has.Count.EqualTo(1));
            rule = rules[0].Comp;

            // The station's map is in the fog.
            map = entMan.GetComponent<TransformComponent>(player).MapUid!.Value;
            Assert.That(entMan.TryGetComponent(map, out HollowWorldComponent world));
            Assert.That(world!.Phase, Is.EqualTo(HollowPhase.Fog));
            Assert.That(rule.Phase, Is.EqualTo(HollowPhase.Fog));
            Assert.That(rule.NextPhaseTime, Is.GreaterThan(server.Timing.CurTime));

            // The player was handed a pocket radio somewhere on their person.
            var radios = entMan.AllEntities<StaticRadioComponent>()
                .Where(r => IsHeldBy(entMan, r.Owner, player))
                .ToList();
            Assert.That(radios, Has.Count.EqualTo(1), "Player should spawn with exactly one pocket radio");

            // Put a light next to the player so we can watch it change.
            var light = entMan.SpawnAtPosition(LightFixture, entMan.GetComponent<TransformComponent>(player).Coordinates);
            bulb = lights.GetBulb(light)!.Value;
            originalColor = entMan.GetComponent<LightBulbComponent>(bulb).Color;
            Assert.That(originalColor, Is.Not.EqualTo(rule.OtherworldLightColor));
        });

        // The client sees the fog too.
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            var clientMap = client.EntMan.GetEntity(server.EntMan.GetNetEntity(map));
            Assert.That(client.EntMan.TryGetComponent(clientMap, out HollowWorldComponent world));
            Assert.That(world!.Phase, Is.EqualTo(HollowPhase.Fog));
        });

        // Siren: lights start blinking.
        await server.WaitAssertion(() =>
        {
            hollow.AdvancePhase(rule);
            Assert.That(rule.Phase, Is.EqualTo(HollowPhase.Siren));
            Assert.That(entMan.GetComponent<HollowWorldComponent>(map).Phase, Is.EqualTo(HollowPhase.Siren));
            Assert.That(entMan.Count<BlinkingPoweredLightComponent>(), Is.GreaterThan(0));
        });

        // Otherworld: lights turn red and monsters appear.
        await server.WaitAssertion(() =>
        {
            hollow.AdvancePhase(rule);
            Assert.That(rule.Phase, Is.EqualTo(HollowPhase.Otherworld));
            Assert.That(entMan.GetComponent<LightBulbComponent>(bulb).Color, Is.EqualTo(rule.OtherworldLightColor));
            Assert.That(rule.SpawnedMonsters, Is.Not.Empty);
            Assert.That(rule.SpawnedMonsters.All(entMan.HasComponent<HollowMonsterComponent>));
        });

        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            var clientMap = client.EntMan.GetEntity(server.EntMan.GetNetEntity(map));
            Assert.That(client.EntMan.GetComponent<HollowWorldComponent>(clientMap).Phase, Is.EqualTo(HollowPhase.Otherworld));
        });

        // Back to the fog: lights restored, monsters gone.
        await server.WaitAssertion(() =>
        {
            hollow.AdvancePhase(rule);
            Assert.That(rule.Phase, Is.EqualTo(HollowPhase.Fog));
            Assert.That(rule.ShiftsCompleted, Is.EqualTo(1));
            Assert.That(entMan.GetComponent<LightBulbComponent>(bulb).Color, Is.EqualTo(originalColor));
            Assert.That(rule.SpawnedMonsters, Is.Empty);
            Assert.That(rule.OriginalBulbColors, Is.Empty);
        });

        await pair.RunTicksSync(5);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Count<HollowMonsterComponent>(), Is.Zero, "Monsters should dissolve when the fog returns");
        });

        // Ending the round lifts the fog.
        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Count<HollowWorldComponent>(), Is.Zero);
        });
    }

    /// <summary>
    /// The pocket radio speeds up its static bursts when a monster is close, and goes quiet when switched off.
    /// </summary>
    [Test]
    public async Task RadioDetectsMonsterTest()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var ticker = server.System<ServerGameTicker>();
        var toggle = server.System<ItemToggleSystem>();

        await server.WaitPost(() =>
        {
            ticker.SetGamePreset(Preset);
            ticker.ToggleReadyAll(true);
            ticker.StartRound();
        });
        await pair.RunTicksSync(10);

        var player = pair.Player!.AttachedEntity!.Value;
        Entity<StaticRadioComponent> radio = default;
        await server.WaitAssertion(() =>
        {
            radio = entMan.AllEntities<StaticRadioComponent>().Single(r => IsHeldBy(entMan, r.Owner, player));
            Assert.That(toggle.IsActivated(radio.Owner), "Radio should start switched on");
        });

        // Nothing nearby: the radio just keeps polling.
        await pair.RunSeconds(1);
        await server.WaitAssertion(() =>
        {
            var wait = radio.Comp.NextBurst - server.Timing.CurTime;
            Assert.That(wait, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(0.5)));
        });

        // A monster right next to the player: the radio schedules its next burst of static.
        await server.WaitPost(() =>
        {
            entMan.SpawnAtPosition("MobHollowHusk", entMan.GetComponent<TransformComponent>(player).Coordinates);
        });
        // Watch tick by tick, since the radio re-arms itself after every burst.
        var longestWait = TimeSpan.Zero;
        for (var i = 0; i < server.Timing.TickRate * 2; i++)
        {
            await pair.RunTicksSync(1);
            await server.WaitPost(() =>
            {
                var wait = radio.Comp.NextBurst - server.Timing.CurTime;
                if (wait > longestWait)
                    longestWait = wait;
            });
        }

        Assert.That(longestWait, Is.GreaterThan(TimeSpan.FromSeconds(0.5)), "Radio should have played static and backed off");
        Assert.That(longestWait, Is.LessThan(radio.Comp.MaxInterval), "Static should come faster than the edge-of-range rate");
    }

    private static bool IsHeldBy(IEntityManager entMan, EntityUid item, EntityUid holder)
    {
        var parent = entMan.GetComponent<TransformComponent>(item).ParentUid;
        while (parent.IsValid())
        {
            if (parent == holder)
                return true;

            parent = entMan.GetComponent<TransformComponent>(parent).ParentUid;
        }

        return false;
    }
}
