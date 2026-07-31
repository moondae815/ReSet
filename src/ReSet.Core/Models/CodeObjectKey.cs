namespace ReSet.Core.Models;

public sealed record CodeObjectKey(string Database, string Schema, string Name, CodeObjectType Type)
{
    public string CanonicalName =>
        $"{EncodeCanonicalSegment(Database)}.{EncodeCanonicalSegment(Schema)}.{EncodeCanonicalSegment(Name)}.{Type}";

    public string LegacyCanonicalName => $"{Database}.{Schema}.{Name}.{Type}";

    public static CodeObjectKey Create(string database, string schema, string name, CodeObjectType type) =>
        new(database.Trim(), schema.Trim(), name.Trim(), type);

    public bool Equals(CodeObjectKey? other) =>
        other is not null &&
        string.Equals(Database, other.Database, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
        Type == other.Type;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Database, StringComparer.OrdinalIgnoreCase);
        hash.Add(Schema, StringComparer.OrdinalIgnoreCase);
        hash.Add(Name, StringComparer.OrdinalIgnoreCase);
        hash.Add(Type);
        return hash.ToHashCode();
    }

    internal static string EncodeCanonicalSegment(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal)
            .Replace(".", "%2E", StringComparison.Ordinal);
}
