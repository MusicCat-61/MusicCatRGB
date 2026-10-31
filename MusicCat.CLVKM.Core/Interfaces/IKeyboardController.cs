using System.Threading.Tasks;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Interfaces;

public interface IKeyboardController
{
    Task InitializeAsync();
    Task SetColorAsync(Zone zone, ColorRGB color);
    Task SetModeAsync(int mode) => Task.CompletedTask;
    Task SetBrightnessAsync(byte brightness) => Task.CompletedTask; // Default to 100%
    Task ShutdownAsync();
}
