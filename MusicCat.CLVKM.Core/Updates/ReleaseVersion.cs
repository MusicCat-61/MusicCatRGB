using System;
using System.Collections.Generic;

namespace MusicCat.CLVKM.Core.Updates;

/// <summary>
/// A release version that can be compared no matter how many components the tag has.
///
/// The project has shipped both three-part tags (<c>1.7.0</c>) and four-part ones
/// (<c>1.0.6.1</c>), and <see cref="System.Version"/> cannot order them against each
/// other reliably — <c>1.0.6</c> is "less than" <c>1.0.6.0</c> for it, and a missing
/// component throws. Here every version is normalised to four components, so
/// <c>1.7.0</c> == <c>1.7.0.0</c> and <c>1.0.6.1</c> &gt; <c>1.0.6</c>.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private const int ComponentCount = 4;

    private readonly int[] _components;

    private ReleaseVersion(int[] components, string? prerelease, string original)
    {
        _components = components;
        Prerelease = prerelease;
        Original = original;
    }

    /// <summary>The version exactly as it appeared in the tag, without the leading "v".</summary>
    public string Original { get; }

    /// <summary>Dotted numeric part, always four components: <c>1.0.6.1</c>.</summary>
    public string Normalized => string.Join('.', _components);

    /// <summary>
    /// The version as the user should see it. Both three- and four-part tags are in use
    /// (<c>1.7.0</c> and <c>1.0.6.1</c>), so three components are always shown and the
    /// fourth only when it is not zero: <c>1.0.6.0</c> reads <c>1.0.6</c>, while
    /// <c>1.0.6.1</c> keeps its fourth digit instead of collapsing to <c>1.0.6</c>.
    /// </summary>
    public string Display
    {
        get
        {
            int count = _components[3] == 0 ? 3 : 4;
            var parts = new string[count];

            for (int i = 0; i < count; i++)
            {
                parts[i] = _components[i].ToString();
            }

            var text = string.Join('.', parts);
            return Prerelease is null ? text : $"{text}-{Prerelease}";
        }
    }

    /// <summary>Suffix after the dash ("beta.1" in <c>1.7.0-beta.1</c>), if any.</summary>
    public string? Prerelease { get; }

    public bool IsPrerelease => Prerelease is not null;

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();

        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        // Drop build metadata ("+abc123"), then split off a prerelease suffix.
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        string? prerelease = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
        }

        var pieces = value.Split('.', StringSplitOptions.TrimEntries);
        if (pieces.Length == 0 || pieces.Length > ComponentCount)
        {
            return false;
        }

        var components = new int[ComponentCount];
        for (int i = 0; i < pieces.Length; i++)
        {
            // "1.0.6.1" is fine; "1.0.x" is not a version we can order.
            if (!int.TryParse(pieces[i], out int component) || component < 0)
            {
                return false;
            }

            components[i] = component;
        }

        version = new ReleaseVersion(components, prerelease, value);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (int i = 0; i < ComponentCount; i++)
        {
            int diff = _components[i].CompareTo(other._components[i]);
            if (diff != 0)
            {
                return diff;
            }
        }

        // Same numbers: a plain release outranks its own prereleases.
        if (Prerelease is null && other.Prerelease is not null)
        {
            return 1;
        }

        if (Prerelease is not null && other.Prerelease is null)
        {
            return -1;
        }

        return string.CompareOrdinal(Prerelease, other.Prerelease);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

    public override int GetHashCode() => Normalized.GetHashCode() ^ (Prerelease?.GetHashCode() ?? 0);

    public override string ToString() => Display;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    // Without these, "==" would compare references and two equal versions parsed from
    // different strings would look different.
    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right) => !(left == right);
}
