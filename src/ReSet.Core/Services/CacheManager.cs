using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public class CacheManager : ICacheManager
    {
        private static readonly object FileLock = new object();
        private const string CacheIndexFileName = ".sp_cache_index.json";
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
        private static readonly Regex ReferenceSectionRegex = new(
            @"(?ms)^## 참조 코드 객체(?:[ \t]*\r?\n|\z).*?(?=^##\s|\z)",
            RegexOptions.Compiled);

        public string ComputeCompositeHash(SpDefinition spDef, int maxDepth)
        {
            if (spDef == null) return string.Empty;

            // 1. SP 본문 소스 DDL 해시
            var sourceHash = ComputeSha256(spDef.DdlText);

            // 2. 의존성 개체들의 해시 수집 및 정렬 (일관된 해시 결합을 위해 SortedDictionary 사용)
            var depHashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (spDef.Dependencies != null)
            {
                foreach (var dep in spDef.Dependencies)
                {
                    var key = BuildDependencyKey(dep);
                    var ddl = dep.ReferencedDdlText ?? string.Empty;
                    depHashes[key] = ComputeSha256(ddl);
                }
            }

            // 3. 결합 문자열 구성
            var sb = new StringBuilder();
            sb.AppendLine($"Source:{sourceHash}");
            sb.AppendLine($"MaxDepth:{maxDepth}");
            foreach (var kvp in depHashes)
            {
                sb.AppendLine($"Dep:{kvp.Key}:{kvp.Value}");
            }

            return ComputeSha256(sb.ToString());
        }

        public bool IsCacheValid(
            CodeObjectKey objectKey,
            string compositeHash,
            OutputPathResolver outputPaths)
        {
            if (objectKey == null ||
                string.IsNullOrWhiteSpace(compositeHash) ||
                outputPaths == null)
            {
                return false;
            }

            var cacheKey = objectKey.CanonicalName;
            Log.Information("캐시 유효성 검사 - 코드 객체: {ObjectKey}", cacheKey);

            try
            {
                // 1. 실제 출력 파일이 존재하는지 검증 (docs/Spec.md)
                var specFilePath = outputPaths.ResolveSpecPath(objectKey);
                if (!File.Exists(specFilePath))
                {
                    Log.Debug("캐시 미스 (설계 명세서 파일이 존재하지 않음): {SpecFilePath}", specFilePath);
                    return false;
                }

                // 2. 캐시 인덱스 파일 로드 및 해시 대조
                var cacheIndex = LoadCacheIndex(outputPaths.OutputRoot);
                if (cacheIndex != null &&
                    TryGetEntry(cacheIndex, objectKey, outputPaths, out var entry))
                {
                    var specFileContent = NormalizeSpecificationForCache(
                        File.ReadAllText(specFilePath));
                    var currentSpecContentHash =
                        entry.SpecContentLength > 0 &&
                        specFileContent.Length >= entry.SpecContentLength
                            ? ComputeSha256(
                                specFileContent[
                                    (specFileContent.Length - entry.SpecContentLength)..])
                            : string.Empty;
                    var isValid =
                        entry.ObjectKey == objectKey &&
                        !string.IsNullOrWhiteSpace(entry.SpecContentHash) &&
                        string.Equals(
                            entry.SpecContentHash,
                            currentSpecContentHash,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            entry.CompositeHash,
                            compositeHash,
                            StringComparison.OrdinalIgnoreCase);
                    if (isValid)
                    {
                        Log.Information(
                            "캐시 히트 - 코드 객체: {ObjectKey} (분석 생략 가능)",
                            cacheKey);
                    }
                    else
                    {
                        Log.Debug(
                            "캐시 미스 (객체 키 또는 복합 해시 불일치) - 코드 객체: {ObjectKey}, EntryHash: {EntryHash}, CurrentHash: {CurrentHash}",
                            cacheKey,
                            entry.CompositeHash,
                            compositeHash);
                    }
                    return isValid;
                }
            }
            catch (Exception ex)
            {
                // 캐시 로드 실패 시 안전하게 Soft Fail (false 반환하여 재분석 진행)
                Log.Warning(
                    ex,
                    "캐시 인덱스 파일 로드 중 오류 발생 - 코드 객체: {ObjectKey}",
                    cacheKey);
                return false;
            }

            Log.Debug(
                "캐시 미스 (캐시 인덱스 내 항목 없음) - 코드 객체: {ObjectKey}",
                cacheKey);
            return false;
        }

        public void UpdateCache(
            CodeObjectKey objectKey,
            SpDefinition spDef,
            string compositeHash,
            OutputPathResolver outputPaths,
            string specificationMarkdown)
        {
            if (objectKey == null ||
                spDef == null ||
                string.IsNullOrWhiteSpace(compositeHash) ||
                outputPaths == null ||
                string.IsNullOrEmpty(specificationMarkdown))
            {
                return;
            }

            var cacheKey = objectKey.CanonicalName;
            try
            {
                lock (FileLock)
                {
                    var cacheIndex =
                        LoadCacheIndex(outputPaths.OutputRoot) ??
                        new CacheIndex();

                    // 의존성 개별 해시 구성
                    var depHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (spDef.Dependencies != null)
                    {
                        foreach (var dep in spDef.Dependencies)
                        {
                            var key = BuildDependencyKey(dep);
                            var ddl = dep.ReferencedDdlText ?? string.Empty;
                            depHashes[key] = ComputeSha256(ddl);
                        }
                    }

                    var cacheableSpecification = NormalizeSpecificationForCache(
                        specificationMarkdown);
                    var entry = new CacheEntry
                    {
                        ProcedureName = $"{objectKey.Schema}.{objectKey.Name}",
                        ObjectKey = objectKey,
                        LastAnalyzed = DateTime.UtcNow,
                        SourceHash = ComputeSha256(spDef.DdlText),
                        DependencyHashes = depHashes,
                        CompositeHash = compositeHash,
                        SpecContentHash = ComputeSha256(cacheableSpecification),
                        SpecContentLength = cacheableSpecification.Length
                    };

                    cacheIndex.Entries[cacheKey] = entry;

                    SaveCacheIndex(outputPaths.OutputRoot, cacheIndex);
                    Log.Information(
                        "캐시 인덱스 갱신 성공 - 코드 객체: {ObjectKey}",
                        cacheKey);
                }
            }
            catch (Exception ex)
            {
                // 캐시 쓰기 실패 시 예외 격리 (분석은 통과했으므로 로깅 외 무시)
                Log.Warning(
                    ex,
                    "캐시 인덱스 갱신 실패 (예외 격리) - 코드 객체: {ObjectKey}",
                    cacheKey);
            }
        }

        private CacheIndex? LoadCacheIndex(string outputDirectory)
        {
            var cacheIndexPath = Path.Combine(outputDirectory, CacheIndexFileName);
            if (!File.Exists(cacheIndexPath))
            {
                return null;
            }

            lock (FileLock)
            {
                var json = File.ReadAllText(cacheIndexPath);
                var cacheIndex = JsonSerializer.Deserialize<CacheIndex>(
                    json,
                    JsonOptions);
                if (cacheIndex == null)
                {
                    return null;
                }

                cacheIndex.Entries = new Dictionary<string, CacheEntry>(
                    cacheIndex.Entries,
                    StringComparer.OrdinalIgnoreCase);
                return cacheIndex;
            }
        }

        private void SaveCacheIndex(string outputDirectory, CacheIndex cacheIndex)
        {
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var cacheIndexPath = Path.Combine(outputDirectory, CacheIndexFileName);
            var json = JsonSerializer.Serialize(cacheIndex, JsonOptions);

            lock (FileLock)
            {
                File.WriteAllText(cacheIndexPath, json);
            }
        }

        private static bool TryGetEntry(
            CacheIndex cacheIndex,
            CodeObjectKey objectKey,
            OutputPathResolver outputPaths,
            out CacheEntry entry)
        {
            if (cacheIndex.Entries.TryGetValue(
                    objectKey.CanonicalName,
                    out entry!))
            {
                return true;
            }

            if (cacheIndex.Entries.TryGetValue(
                    objectKey.LegacyCanonicalName,
                    out entry!))
            {
                return true;
            }

            var legacyKey = $"{objectKey.Schema}.{objectKey.Name}";
            return objectKey.Type == CodeObjectType.Procedure &&
                outputPaths.IsCurrentDatabase(objectKey.Database) &&
                cacheIndex.Entries.TryGetValue(legacyKey, out entry!);
        }

        private static string BuildDependencyKey(DependencyInfo dependency) =>
            string.Join(
                    ".",
                    CodeObjectKey.EncodeCanonicalSegment(dependency.Database ?? string.Empty),
                    CodeObjectKey.EncodeCanonicalSegment(dependency.Schema),
                    CodeObjectKey.EncodeCanonicalSegment(dependency.Name),
                    CodeObjectKey.EncodeCanonicalSegment(dependency.Type))
                .ToUpperInvariant();

        private static string NormalizeSpecificationForCache(string specificationMarkdown) =>
            ReferenceSectionRegex.Replace(
                    specificationMarkdown ?? string.Empty,
                    string.Empty)
                .TrimEnd();

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private static string ComputeSha256(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                var hashBytes = sha.ComputeHash(bytes);
                
                var sb = new StringBuilder();
                foreach (var b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

    }
}
