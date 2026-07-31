using System;
using System.IO;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services;

public sealed class OutputPathResolver
{
    private const string SpecFileName = "Spec.md";
    private const string ManifestFileName = "dependency-manifest.json";
    private const string CanonicalDdlFileName = "object_definition.sql";
    private readonly string _currentDatabase;

    public OutputPathResolver(string currentDatabase, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(currentDatabase))
        {
            throw new ArgumentException("Current database is required.", nameof(currentDatabase));
        }

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("Output root is required.", nameof(outputRoot));
        }

        _currentDatabase = currentDatabase.Trim();
        OutputRoot = Path.TrimEndingDirectorySeparator(outputRoot);
    }

    internal string OutputRoot { get; }

    public string ResolveSpecPath(CodeObjectKey objectKey) =>
        Path.Combine(ResolveDocsDirectory(objectKey), SpecFileName);

    public string ResolveDocsDirectory(CodeObjectKey objectKey) =>
        Path.Combine(ResolveObjectDirectory(objectKey), "docs");

    public string ResolveCanonicalDdlPath(CodeObjectKey objectKey)
    {
        ArgumentNullException.ThrowIfNull(objectKey);

        var objectDirectoryName = ResolveObjectDirectoryName(objectKey);
        return IsCurrentDatabase(objectKey.Database)
            ? Path.Combine(
                OutputRoot,
                "Objects",
                objectDirectoryName,
                "raw",
                CanonicalDdlFileName)
            : Path.Combine(
                OutputRoot,
                "External",
                SanitizeSegment(objectKey.Database),
                "Objects",
                objectDirectoryName,
                "raw",
                CanonicalDdlFileName);
    }

    public string ResolveManifestPath(CodeObjectKey objectKey) =>
        Path.Combine(ResolveObjectDirectory(objectKey), "raw", ManifestFileName);

    private string ResolveObjectDirectory(CodeObjectKey objectKey)
    {
        ArgumentNullException.ThrowIfNull(objectKey);

        var objectTypeDirectory = objectKey.Type switch
        {
            CodeObjectType.Procedure => "Procedures",
            CodeObjectType.Function => "Functions",
            _ => throw new ArgumentOutOfRangeException(
                nameof(objectKey),
                objectKey.Type,
                "Unsupported code object type.")
        };
        var objectDirectoryName = string.Join(
            ".",
            SanitizeSegment(objectKey.Schema),
            SanitizeSegment(objectKey.Name));

        return IsCurrentDatabase(objectKey.Database)
            ? Path.Combine(OutputRoot, objectTypeDirectory, objectDirectoryName)
            : Path.Combine(
                OutputRoot,
                "External",
                SanitizeSegment(objectKey.Database),
                objectTypeDirectory,
                objectDirectoryName);
    }

    private string ResolveObjectDirectoryName(CodeObjectKey objectKey) =>
        string.Join(
            ".",
            SanitizeSegment(objectKey.Schema),
            SanitizeSegment(objectKey.Name),
            SanitizeSegment(objectKey.Type.ToString()));

    internal bool IsCurrentDatabase(string database) =>
        string.Equals(
            _currentDatabase,
            database?.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Path segment is required.", nameof(value));
        }

        var trimmedValue = value.Trim();
        if (trimmedValue is "." or "..")
        {
            return new string('_', trimmedValue.Length);
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(trimmedValue
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
    }
}
