using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._Hollow;

/// <summary>
/// Placed on a map entity while the Hollow Fog game mode is running on it.
/// Clients read this to draw the fog / Otherworld screen overlay.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class HollowWorldComponent : Component
{
    [DataField, AutoNetworkedField]
    public HollowPhase Phase = HollowPhase.Fog;
}

[Serializable, NetSerializable]
public enum HollowPhase : byte
{
    /// <summary>
    /// Thick white fog, normal lighting.
    /// </summary>
    Fog,

    /// <summary>
    /// The siren is wailing and the lights are failing. The Otherworld is coming.
    /// </summary>
    Siren,

    /// <summary>
    /// Rust, darkness and monsters.
    /// </summary>
    Otherworld,
}
