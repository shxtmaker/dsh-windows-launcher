using System.Text.RegularExpressions;

namespace DshLauncher.Compatibility;

internal sealed partial class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(
        string major,
        string minor,
        string patch,
        string? prerelease,
        string? build,
        string text)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
        Text = text;
    }

    public string Major { get; }

    public string Minor { get; }

    public string Patch { get; }

    public string? Prerelease { get; }

    public string? Build { get; }

    public string Text { get; }

    public static bool TryParseExact(string? value, out SemanticVersion version)
    {
        version = null!;
        if (value is null || value.Length is 0 or > 128 ||
            !string.Equals(value, value.Normalize(), StringComparison.Ordinal))
        {
            return false;
        }

        var match = SemVerPattern().Match(value);
        if (!match.Success)
        {
            return false;
        }

        var major = match.Groups[1].Value;
        var minor = match.Groups[2].Value;
        var patch = match.Groups[3].Value;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (prerelease is not null && prerelease.Split('.').Any(identifier =>
                identifier.All(char.IsAsciiDigit) &&
                identifier.Length > 1 && identifier[0] == '0'))
        {
            return false;
        }

        var build = match.Groups[5].Success ? match.Groups[5].Value : null;
        version = new SemanticVersion(
            major,
            minor,
            patch,
            prerelease,
            build,
            value);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = CompareNumericIdentifier(Major, other.Major);
        if (result != 0)
        {
            return result;
        }

        result = CompareNumericIdentifier(Minor, other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = CompareNumericIdentifier(Patch, other.Patch);
        if (result != 0)
        {
            return result;
        }

        if (Prerelease is null)
        {
            return other.Prerelease is null ? 0 : 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            result = ComparePrereleaseIdentifier(left[index], right[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            return CompareNumericIdentifier(left, right);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0
            ? lengthComparison
            : string.Compare(left, right, StringComparison.Ordinal);
    }

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemVerPattern();
}
