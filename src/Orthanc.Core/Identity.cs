namespace Orthanc.Core;

public static class Identity
{
    // Restrict logical identifiers to portable ASCII, independent of MySQL collation or OS casing.
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A name is required");

        var result = value.Trim().ToLowerInvariant();
        if (result.Length is < 1 or > 200 || result.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or ':' or '/' or ' ')))
            throw new ArgumentException("Names must be 1–200 ASCII letters, digits, spaces, or . _ - : /");

        return result;
    }
}