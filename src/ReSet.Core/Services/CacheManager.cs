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
        // 4: 집합 술어 수집 범위가 넓어졌다(리터럴 우변 등호·부등호, ISNULL 래핑 좌변,
        //    파생 테이블 내부 술어). 표에 연산·범위 칸이 생겨 프롬프트 입력이 달라졌고,
        //    옛 산출물은 그 칸이 없어 L1을 통과할 수 없으므로 전부 재분석해야 한다.
        //    2026-08-19 축 A 감사에서 이 재료가 없어 새어 나간 대상 행 집합 결함이 4건이었다.
        // 5: 참조 함수 표가 조립기 산출물로 바뀌었고 함수 동작 서술이 금지되었다.
        //    프롬프트 입력과 출력 계약이 둘 다 달라졌으므로 옛 산출물은 재분석해야 한다.
        //    2026-08-20 축 A 교차 대조에서 이 표의 10행 중 8행이 결함이었고 🔴이 5건이었다.
        // 6: UPDATE 절 제목의 문장 번호가 "갱신 0"에서 실제 번호로 고쳐졌고(정규화가
        //    GlobalStatementOrdinal을 유실하고 있었다), 오류 반환 코드 앵커의 줄 번호가
        //    빈 줄만큼 밀리던 것을 바로잡았다. 둘 다 프롬프트 입력이 달라진 것이므로
        //    옛 엔트리를 재사용하면 산출물이 옛 재료 그대로 남는다.
        // 7: 추출기 결함 셋을 닫았다(2026-08-20 축 A 감사). 자기참조 판정이 갱신 대상
        //    별칭을 FROM 절에서 풀고, 집합 술어가 LEFT/RIGHT 같은 전용 노드로 감싼
        //    좌변도 담고, 의존성 이름이 카탈로그 표기로 정규화된다. 셋 다 프롬프트에
        //    실리는 기계 확정 재료라 옛 엔트리를 재사용하면 틀린 재료가 그대로 남는다.
        // 8: 잠금 힌트·객체 선언 표가 새로 실리고 DML 범위 표에 ORDER BY 칸이 붙었다
        //    (2026-08-21 축 A 감사의 🟡 다섯). 프롬프트 입력이 달라졌으므로 옛 엔트리를
        //    재사용하면 산출물이 옛 재료 그대로 남는다.
        // 9: 실행 의미 표(DB 배치·집계 대입·@@ROWCOUNT·커서 수명·식 타입 경로 다섯 종류)와
        //    CASE 분기 표가, 이 버전을 올릴 당시 존재하던 프롬프트 호출부 네 갈래(SP
        //    전체·함수·CrudAnalysis·LogicAndVisualization) 전부에 새로 실렸다 - 이후
        //    Task 17이 다섯 번째 호출부(OverviewAndParameters)를 추가했다(AiService.cs
        //    참고). 요약이 곧 결함이었다(2026-08-22 축 A 감사, UIF_SettleYMD 🟠 3건).
        //    DML 범위 표에는 GROUP BY 칸이 붙었다. 스키마 표 과소 포함도 고쳐져 주석에만
        //    등장하는 컬럼과 별칭 한정 표기(예: X.PRODUCTNAME)가 다시 실린다. 과소
        //    포함이 "그 컬럼은 없다"는 잘못된 서술을 14개 명세서에 남긴 결함이었다
        //    (UP_UTIL_SETTLE_PROC_ETC 실측).
        //    전부 프롬프트 입력이 달라진 것이므로 옛 엔트리를 재사용하면 새 표가 없는
        //    옛 산출물이 그대로 남고, 이 계획이 세운 L1 검사도 캐시 히트에서는 영영
        //    발동하지 않는다.
        // 10: 프롬프트 입력이 둘 바뀌었다 - 스키마 표 컬럼 필터가 INSERT·UPDATE 대상
        //     컬럼(입력원 ⑤)도 보게 됐고(오직 대상으로만 등장하는 컬럼이 잘려 모델이
        //     "스키마에 없다"고 단정하던 결함), 실행 의미 표의 `DB 배치` 문장이 3부
        //     식별자를 소속 DB 접두사로 안과 밖으로 가른다(홈 DB 참조가 크로스 DB로
        //     읽히던 결함). 이 회차가 세운 L1 검사도 셋 늘었다 - 기계 확정 표의 헤더·
        //     구분·데이터 행 셀 수, INSERT 매핑 표 테이블명의 파서 표기 대조(Ordinal),
        //     널 허용 주장과 `Dependencies.IsNullable`의 테이블 앵커 대조. 프롬프트
        //     입력이 달라진 것이므로 옛 엔트리를 재사용하면 틀린 재료로 만든 산출물이
        //     그대로 남고, 새 L1 셋도 캐시 히트에서는 영영 발동하지 않는다.
        //     2026-08-22 축 A 재감사 실측 6결함이 근거다.
        private const int CurrentCacheFormatVersion = 10;
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
