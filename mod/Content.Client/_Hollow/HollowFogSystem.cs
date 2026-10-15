using Robust.Client.Graphics;

namespace Content.Client._Hollow;

/// <summary>
/// Registers the <see cref="HollowFogOverlay"/>. The overlay decides each frame whether there's anything to draw.
/// </summary>
public sealed partial class HollowFogSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlay = default!;

    private HollowFogOverlay _fog = default!;

    public override void Initialize()
    {
        base.Initialize();

        _fog = new HollowFogOverlay();
        _overlay.AddOverlay(_fog);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _overlay.RemoveOverlay(_fog);
    }
}
