using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Animation that can use the colours picked in the UI (primary and secondary).
/// The engine reads these values on every frame, so live slider changes are picked up.
/// </summary>
public interface IColorAwareAnimation
{
    ColorRGB Primary { get; set; }

    ColorRGB Secondary { get; set; }
}
