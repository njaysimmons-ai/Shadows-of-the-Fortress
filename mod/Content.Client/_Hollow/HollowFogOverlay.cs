using System.Numerics;
using Content.Shared._Hollow;
using Content.Shared.Ghost.Components;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._Hollow;

/// <summary>
/// Draws the fog (and the Otherworld's darkness) over the world when the local player is on a
/// map with <see cref="HollowWorldComponent"/>.
/// </summary>
public sealed partial class HollowFogOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> Shader = "HollowFog";

    [Dependency] private IEntityManager _entity = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IPrototypeManager _proto = default!;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    private readonly ShaderInstance _shader;

    /// <summary>
    /// How fast the overlay blends toward a new phase's look, per second.
    /// </summary>
    private const float BlendRate = 0.35f;

    private FogLook _current = FogLook.None;

    public HollowFogOverlay()
    {
        IoCManager.InjectDependencies(this);
        _shader = _proto.Index(Shader).InstanceUnique();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        var target = GetTargetLook();
        var t = Math.Clamp(args.DeltaSeconds * BlendRate, 0f, 1f);
        _current = FogLook.Lerp(_current, target, t);
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        if (!_entity.TryGetComponent(_player.LocalEntity, out EyeComponent? eye) || args.Viewport.Eye != eye.Eye)
            return false;

        return _current.Density > 0.005f;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        _shader.SetParameter("fogColor", new Vector3(_current.Color.R, _current.Color.G, _current.Color.B));
        _shader.SetParameter("density", _current.Density);
        _shader.SetParameter("innerRadius", _current.InnerRadius);
        _shader.SetParameter("outerRadius", _current.OuterRadius);
        _shader.SetParameter("grain", _current.Grain);
        _shader.SetParameter("veil", _current.Veil);

        var handle = args.WorldHandle;
        handle.UseShader(_shader);
        handle.DrawRect(args.WorldBounds, Color.White);
        handle.UseShader(null);
    }

    private FogLook GetTargetLook()
    {
        if (_player.LocalEntity is not { } player || _entity.HasComponent<GhostComponent>(player))
            return FogLook.None;

        var map = _entity.GetComponent<TransformComponent>(player).MapUid;
        if (!_entity.TryGetComponent(map, out HollowWorldComponent? world))
            return FogLook.None;

        return world.Phase switch
        {
            HollowPhase.Fog => FogLook.Fog,
            HollowPhase.Siren => FogLook.Siren,
            HollowPhase.Otherworld => FogLook.Otherworld,
            _ => FogLook.None,
        };
    }

    private readonly record struct FogLook(Color Color, float Density, float InnerRadius, float OuterRadius, float Grain, float Veil)
    {
        public static readonly FogLook Fog = new(Color.FromHex("#c4c8cc"), 0.92f, 0.16f, 0.55f, 0.12f, 0.1f);
        public static readonly FogLook Siren = new(Color.FromHex("#4a4a50"), 0.95f, 0.15f, 0.5f, 0.4f, 0.08f);
        public static readonly FogLook Otherworld = new(Color.FromHex("#120303"), 0.97f, 0.17f, 0.5f, 0.5f, 0.03f);

        // Same shape as the fog with zero density, so fading in from nothing only fades the opacity.
        public static readonly FogLook None = Fog with { Density = 0f };

        public static FogLook Lerp(FogLook a, FogLook b, float t)
        {
            return new FogLook(
                Color.InterpolateBetween(a.Color, b.Color, t),
                MathHelper.Lerp(a.Density, b.Density, t),
                MathHelper.Lerp(a.InnerRadius, b.InnerRadius, t),
                MathHelper.Lerp(a.OuterRadius, b.OuterRadius, t),
                MathHelper.Lerp(a.Grain, b.Grain, t),
                MathHelper.Lerp(a.Veil, b.Veil, t));
        }
    }
}
