using Content.Shared._Hollow;
using Content.Shared.Item.ItemToggle;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Timing;

namespace Content.Server._Hollow;

/// <summary>
/// Makes <see cref="StaticRadioComponent"/> radios crackle when a <see cref="HollowMonsterComponent"/> is nearby.
/// </summary>
public sealed partial class StaticRadioSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ItemToggleSystem _toggle = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly HashSet<Entity<HollowMonsterComponent>> _monsters = new();

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<StaticRadioComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var radio, out var xform))
        {
            if (now < radio.NextBurst)
                continue;

            // Check a few times a second even when nothing is around.
            radio.NextBurst = now + TimeSpan.FromSeconds(0.5);

            if (!_toggle.IsActivated(uid))
                continue;

            var radioPos = _transform.GetMapCoordinates(uid, xform);
            _monsters.Clear();
            _lookup.GetEntitiesInRange(radioPos, radio.Range, _monsters);

            var closest = float.MaxValue;
            foreach (var monster in _monsters)
            {
                var monsterPos = _transform.GetMapCoordinates(monster);
                var distance = (monsterPos.Position - radioPos.Position).Length();
                closest = MathF.Min(closest, distance);
            }

            if (closest > radio.Range)
                continue;

            // 0 when adjacent, 1 at the edge of the range.
            var t = Math.Clamp(closest / radio.Range, 0f, 1f);
            var volume = radio.FarVolume * t;
            _audio.PlayPvs(radio.StaticSound, uid, radio.StaticSound.Params.WithVolume(volume));

            var interval = radio.MinInterval + (radio.MaxInterval - radio.MinInterval) * t;
            radio.NextBurst = now + interval;
        }
    }
}
