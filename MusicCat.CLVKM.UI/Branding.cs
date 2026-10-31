using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MusicCat.CLVKM.UI;

/// <summary>Assembly-relative resource URIs, so renaming the assembly cannot break icons.</summary>
public static class Branding
{
    private static readonly string AssemblyName = typeof(Branding).Assembly.GetName().Name ?? "KGMaster";

    private static ImageSource? _iconImage;

    public static Uri IconUri { get; } =
        new($"pack://application:,,,/{AssemblyName};component/icon.png", UriKind.Absolute);

    /// <summary>Lazily loaded, frozen window icon (null when the resource is missing).</summary>
    public static ImageSource? IconImage
    {
        get
        {
            if (_iconImage is null)
            {
                try
                {
                    var frame = BitmapFrame.Create(IconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    frame.Freeze();
                    _iconImage = frame;
                }
                catch
                {
                    return null;
                }
            }

            return _iconImage;
        }
    }
}
