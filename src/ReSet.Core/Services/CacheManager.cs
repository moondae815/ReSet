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
        private static volatile bool _hasMigrated = false;
        private static readonly object _migrationLock = new object();
        private const string CacheIndexFileName = ".sp_cache_index.json";
        // 2: 정적 분석 식별자 정규화. DDL이 안 바뀌어도 프롬프트에 들어가는 스키마 표와
        //    테이블 목록이 달라지므로, 이전 버전으로 만든 산출물은 전부 다시 만들어야 한다.
        // 3: SpStaticAnalysisResult에 AstUpdateMappings가 추가되어 프롬프트 입력이 달라졌다.
        //    DDL이 같아도 기존 산출물은 UPDATE 매핑표가 없으므로 재분석해야 한다.
        private const int CurrentCacheFormatVersion = 3;
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
            if (outputPaths != null)
            {
                EnsureMigrated(outputPaths.OutputRoot);
            }

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
                // 1. 실제 출력 파일 경로 확인 (존재하지 않아도 File Copy를 위해 진행)
                var specFilePath = outputPaths.ResolveSpecPath(objectKey);

                // 2. 캐시 인덱스 파일 로드 및 해시 대조
                var globalCacheDir = GetGlobalCacheDirectory(outputPaths.OutputRoot);
                var cacheIndex = LoadCacheIndex(globalCacheDir);
                if (cacheIndex != null &&
                    TryGetEntry(cacheIndex, objectKey, outputPaths, out var entry))
                {
                    // 파일 읽기와 해시 계산보다 먼저 판정한다. 해석할 수 없는 스키마의
                    // 엔트리는 내용이 일치하더라도 신뢰할 근거가 없다.
                    if (entry.FormatVersion != CurrentCacheFormatVersion)
                    {
                        Log.Information(
                            "캐시 미스(포맷 버전 {EntryVersion} != {CurrentVersion}) - 코드 객체: {ObjectKey}",
                            entry.FormatVersion,
                            CurrentCacheFormatVersion,
                            cacheKey);
                        return false;
                    }

                    string currentSpecContentHash = string.Empty;
                    if (File.Exists(specFilePath))
                    {
                        var specFileContent = NormalizeSpecificationForCache(
                            File.ReadAllText(specFilePath));
                        currentSpecContentHash =
                            entry.SpecContentLength > 0 &&
                            specFileContent.Length >= entry.SpecContentLength
                                ? ComputeSha256(
                                    specFileContent[
                                        (specFileContent.Length - entry.SpecContentLength)..])
                                : string.Empty;
                    }

                    var isValid =
                        entry.ObjectKey == objectKey &&
                        !string.IsNullOrWhiteSpace(entry.SpecContentHash) &&
                        (!File.Exists(specFilePath) || string.Equals(
                            entry.SpecContentHash,
                            currentSpecContentHash,
                            StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(
                            entry.CompositeHash,
                            compositeHash,
                            StringComparison.OrdinalIgnoreCase);

                    if (isValid)
                    {
                        // Copy the original file to the new destination if they differ
                        if (!string.IsNullOrEmpty(entry.OriginalSpecPath) && 
                            File.Exists(entry.OriginalSpecPath) &&
                            !string.Equals(entry.OriginalSpecPath, specFilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var destDir = Path.GetDirectoryName(specFilePath);
                                if (!string.IsNullOrEmpty(destDir)) 
                                    Directory.CreateDirectory(destDir);
                                File.Copy(entry.OriginalSpecPath, specFilePath, overwrite: true);
                                Log.Information("캐시 파일 복사 완료: {Src} -> {Dest}", entry.OriginalSpecPath, specFilePath);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "캐시 파일 복사 실패, Cache Miss로 간주합니다: {Dest}", specFilePath);
                                return false;
                            }
                        }
                        else if (!File.Exists(specFilePath))
                        {
                            // We hit the cache but the file doesn't exist AND we have no OriginalSpecPath to copy from
                            Log.Debug("캐시 히트이나 원본 파일이 존재하지 않아 Cache Miss 처리");
                            return false;
                        }

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
            if (outputPaths != null)
            {
                EnsureMigrated(outputPaths.OutputRoot);
            }

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
                    var globalCacheDir = GetGlobalCacheDirectory(outputPaths.OutputRoot);
                    var cacheIndex =
                        LoadCacheIndex(globalCacheDir) ??
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
                        FormatVersion = CurrentCacheFormatVersion,
                        ObjectKey = objectKey,
                        LastAnalyzed = DateTime.UtcNow,
                        SourceHash = ComputeSha256(spDef.DdlText),
                        DependencyHashes = depHashes,
                        CompositeHash = compositeHash,
                        SpecContentHash = ComputeSha256(cacheableSpecification),
                        SpecContentLength = cacheableSpecification.Length,
                        OriginalSpecPath = outputPaths.ResolveSpecPath(objectKey)
                    };

                    cacheIndex.Entries[cacheKey] = entry;

                    SaveCacheIndex(globalCacheDir, cacheIndex);
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

        private void EnsureMigrated(string outputRoot)
        {
            if (_hasMigrated) return;
            lock (_migrationLock)
            {
                if (_hasMigrated) return;
                MigrateLegacyCaches(outputRoot);
                _hasMigrated = true;
            }
        }

        public void MigrateLegacyCaches(string outputRoot)
        {
            try
            {
                var globalDir = GetGlobalCacheDirectory(outputRoot);
                if (!Directory.Exists(globalDir)) return;

                var globalIndexPath = Path.Combine(globalDir, CacheIndexFileName);
                var globalIndex = LoadCacheIndex(globalDir) ?? new CacheIndex();
                bool migratedAny = false;

                // Search for all .sp_cache_index.json files in subdirectories
                var legacyFiles = Directory.GetFiles(globalDir, CacheIndexFileName, SearchOption.AllDirectories);
                foreach (var file in legacyFiles)
                {
                    if (string.Equals(file, globalIndexPath, StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        var json = File.ReadAllText(file);
                        var legacyIndex = JsonSerializer.Deserialize<CacheIndex>(json, JsonOptions);
                        if (legacyIndex?.Entries != null)
                        {
                            var legacyDir = Path.GetDirectoryName(file);
                            var legacyResolver = new OutputPathResolver("legacy", legacyDir!); // Used just to resolve SpecPaths if needed

                            foreach (var kvp in legacyIndex.Entries)
                            {
                                // Update OriginalSpecPath if it was missing in legacy
                                if (string.IsNullOrEmpty(kvp.Value.OriginalSpecPath) && kvp.Value.ObjectKey != null)
                                {
                                    var expectedPath = legacyResolver.ResolveSpecPath(kvp.Value.ObjectKey);
                                    if (File.Exists(expectedPath))
                                    {
                                        kvp.Value.OriginalSpecPath = expectedPath;
                                    }
                                }

                                // Only merge if the file actually exists
                                if (!string.IsNullOrEmpty(kvp.Value.OriginalSpecPath) && File.Exists(kvp.Value.OriginalSpecPath))
                                {
                                    globalIndex.Entries[kvp.Key] = kvp.Value;
                                    migratedAny = true;
                                }
                            }
                        }
                        
                        // Optionally delete or rename the legacy file to prevent re-migration
                        File.Move(file, file + ".migrated", overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "레거시 캐시 마이그레이션 실패 (파일 수준): {File}", file);
                    }
                }

                if (migratedAny)
                {
                    SaveCacheIndex(globalDir, globalIndex);
                    Log.Information("레거시 캐시 마이그레이션 완료 (통합 캐시에 병합됨)");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "레거시 캐시 마이그레이션 중 오류가 발생하여 중단되었습니다.");
            }
        }

        private string GetGlobalCacheDirectory(string outputRoot)
        {
            var parent = Directory.GetParent(outputRoot);
            if (parent != null && parent.Name.Equals("output", StringComparison.OrdinalIgnoreCase))
            {
                return parent.FullName;
            }
            return outputRoot;
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
