using Content.Shared._Hollow;
using Content.Shared.Storage;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._Hollow;

/// <summary>
/// Game rule that drowns the station in fog and periodically drags it into the Otherworld.
/// </summary>
[RegisterComponent, Access(typeof(HollowFogRuleSystem))]
public sealed partial class HollowFogRuleComponent : Component
{
    /// <summary>
    /// Minimum time spent in the fog between Otherworld shifts.
    /// </summary>
    [DataField]
    public TimeSpan MinFogDuration = TimeSpan.FromMinutes(8);

    /// <summary>
    /// Maximum time spent in the fog between Otherworld shifts.
    /// </summary>
    [DataField]
    public TimeSpan MaxFogDuration = TimeSpan.FromMinutes(14);

    /// <summary>
    /// How long the siren wails before the Otherworld arrives.
    /// </summary>
    [DataField]
    public TimeSpan SirenDuration = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How long each Otherworld shift lasts.
    /// </summary>
    [DataField]
    public TimeSpan OtherworldDuration = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Monsters spawned when the Otherworld arrives. Rolled once per spawn.
    /// </summary>
    [DataField(required: true)]
    public List<EntitySpawnEntry> Monsters = new();

    /// <summary>
    /// Monster spawns per living player on the station, per Otherworld shift.
    /// </summary>
    [DataField]
    public float MonstersPerPlayer = 0.75f;

    [DataField]
    public int MinMonsters = 3;

    [DataField]
    public int MaxMonsters = 30;

    /// <summary>
    /// Extra spawns per previous Otherworld shift, so each one is worse than the last.
    /// </summary>
    [DataField]
    public int EscalationPerShift = 2;

    /// <summary>
    /// The color every station light bulb turns during the Otherworld.
    /// </summary>
    [DataField]
    public Color OtherworldLightColor = Color.FromHex("#d0402a");

    /// <summary>
    /// Text color of the announcement when the Otherworld arrives.
    /// </summary>
    [DataField]
    public Color OtherworldAnnouncementColor = Color.FromHex("#e0503a");

    /// <summary>
    /// Item handed to every crew member so they can hear what's coming.
    /// </summary>
    [DataField]
    public EntProtoId? StarterItem = "HollowPocketRadio";

    [DataField]
    public SoundSpecifier SirenSound = new SoundPathSpecifier("/Audio/_Hollow/siren.ogg");

    [DataField]
    public SoundSpecifier ReturnSound = new SoundPathSpecifier("/Audio/Misc/woosh.ogg");

    [DataField]
    public EntProtoId DissolveEffect = "HollowDissolveEffect";

    [DataField]
    public HollowPhase Phase = HollowPhase.Fog;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan NextPhaseTime;

    [DataField]
    public int ShiftsCompleted;

    /// <summary>
    /// Monsters spawned this shift, deleted when the fog returns.
    /// </summary>
    [DataField]
    public List<EntityUid> SpawnedMonsters = new();

    /// <summary>
    /// Original colors of every bulb we recolored, restored when the fog returns.
    /// </summary>
    [DataField]
    public Dictionary<EntityUid, Color> OriginalBulbColors = new();

    /// <summary>
    /// Map entities we put a <see cref="HollowWorldComponent"/> on.
    /// </summary>
    [DataField]
    public HashSet<EntityUid> AffectedMaps = new();
}
