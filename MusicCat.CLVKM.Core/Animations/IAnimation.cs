using System.Collections.Generic;

namespace MusicCat.CLVKM.Core.Animations;

public interface IAnimation
{
    string Name { get; }
    IEnumerable<AnimationFrame> GenerateFrames();
}
