using System;
using System.IO;
using System.Linq;
using System.Text;
using ReSet.Core.Models;

namespace ReSet.Core.Services;

public sealed class OutputPathResolver
{
    private const string SpecFileName = "Spec.md";

    /// <summary>
    /// 산출물 파일명의 단일 출처. PRD 도출은 이미 발견한 docs 디렉터리 옆에 쓰므로
    /// CodeObjectKey를 만들지 않지만, 파일명만은 여기서 가져가 조립처가 갈라지지 않게 한다.
    /// </summary>
    public const string PrdFileName = "Prd.md";

    /// <summary>위 상수와 같은 이유로 공개한다 - 디렉터리 스캔이 이 이름을 찾는다.</summary>
    public const string SpecFileNamePublic = SpecFileName;

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

    /// <summary>산출물 레이아웃의 기준이 되는 분석 루트 DB.</summary>
    public string CurrentDatabase => _currentDatabase;

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
                EncodePathSegment(objectKey.Database),
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
            CodeObjectType.Unresolved => "Unresolved",
            _ => throw new ArgumentOutOfRangeException(
                nameof(objectKey),
                objectKey.Type,
                "Unsupported code object type.")
        };
        var objectDirectoryName = string.Join(
            ".",
            EncodePathSegment(objectKey.Schema),
            EncodePathSegment(objectKey.Name));

        return IsCurrentDatabase(objectKey.Database)
            ? Path.Combine(OutputRoot, objectTypeDirectory, objectDirectoryName)
            : Path.Combine(
                OutputRoot,
                "External",
                EncodePathSegment(objectKey.Database),
                objectTypeDirectory,
                objectDirectoryName);
    }

    private string ResolveObjectDirectoryName(CodeObjectKey objectKey) =>
        string.Join(
            ".",
            EncodePathSegment(objectKey.Schema),
            EncodePathSegment(objectKey.Name),
            EncodePathSegment(objectKey.Type.ToString()));

    internal bool IsCurrentDatabase(string database) =>
        string.Equals(
            _currentDatabase,
            database?.Trim(),
            StringComparison.OrdinalIgnoreCase);

    internal static string EncodePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Path segment is required.", nameof(value));
        }

        var trimmedValue = value.Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var encoded = new StringBuilder();
        foreach (var character in trimmedValue)
        {
            if (character == '.' ||
                character == '%' ||
                character == Path.DirectorySeparatorChar ||
                character == Path.AltDirectorySeparatorChar ||
                character == '/' ||
                character == '\\' ||
                invalidCharacters.Contains(character))
            {
                foreach (var valueByte in Encoding.UTF8.GetBytes(character.ToString()))
                {
                    encoded.Append('%');
                    encoded.Append(valueByte.ToString("X2"));
                }
            }
            else
            {
                encoded.Append(character);
            }
        }

        return encoded.ToString();
    }
}
