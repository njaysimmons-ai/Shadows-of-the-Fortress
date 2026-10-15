using Robust.Shared.Audio;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._Hollow;

/// <summary>
/// A pocket radio that hisses with static when a creature of the Otherworld is close.
/// Only works while toggled on via ItemToggle. The closer the creature, the louder and faster the static.
/// </summary>
[RegisterComponent, Access(typeof(StaticRadioSystem))]
public sealed partial class StaticRadioComponent : Component
{
    /// <summary>
    /// How far away a monster can be before the radio picks it up.
    /// </summary>
    [DataField]
    public float Range = 12f;

    /// <summary>
    /// Time between bursts of static when a monster is right next to the radio.
    /// </summary>
    [DataField]
    public TimeSpan MinInterval = TimeSpan.FromSeconds(0.6);

    /// <summary>
    /// Time between bursts of static when a monster is at the edge of the range.
    /// </summary>
    [DataField]
    public TimeSpan MaxInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Volume adjustment at the edge of the range. Static plays at full volume when a monster is adjacent.
    /// </summary>
    [DataField]
    public float FarVolume = -12f;

    [DataField]
    public SoundSpecifier StaticSound = new SoundCollectionSpecifier("HollowRadioStatic");

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan NextBurst;
}
