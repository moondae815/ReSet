using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 프롬프트의 스키마 표에 어떤 컬럼이 실리는지 결정하는 단일 권위.
    ///
    /// 이 지식이 AiService.FormatTableSchemaToMarkdown 안에만 있으면 L1이 알 수 없다.
    /// 렌더링의 부수효과로 어딘가에 기록하는 방식은 택하지 않았다 - 렌더 경로가 둘이라
    /// (BuildSpMetadataTexts, RAG 경로) 어느 쪽이 마지막에 기록했는지에 결과가 달라진다.
    ///
    /// 이 필터는 토큰 절약용 최적화이지 정확성 장치가 아니다. 과다 포함은 표에 불필요한
    /// 행을 몇 개 더할 뿐이지만, 과소 포함은 모델이 그 컬럼을 "존재하지 않는다"고 잘못
    /// 기록한다 - 14개 명세서를 망가뜨린 바로 그 결함이다.
    /// </summary>
    public static class SchemaPromptColumnSelector
    {
        /// <summary>
        /// 이 의존성에 대해 프롬프트 스키마 표가 실제로 보여줄 컬럼 이름들.
        ///
        /// 반환값은 "keepCols"가 아니라 <b>실제로 렌더링되는 집합</b>이다. keepCols가
        /// 비면 필터를 걸지 않고 전체를 찍는 폴백이 있어 둘이 다르다. L1이 대조해야 하는
        /// 것은 AI가 실제로 본 것이므로 후자여야 한다.
        /// </summary>
        public static IReadOnlySet<string> Select(DependencyInfo dep, SpDefinition spDef)
        {
            var keepCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) AST에서 감지한 실제 참조 컬럼
            var analysis = spDef.StaticAnalysis;
            if (analysis?.ReferencedColumnsPerTable != null)
            {
                foreach (var kvp in analysis.ReferencedColumnsPerTable)
                {
                    if (!KeyMatchesDependency(kvp.Key, dep, spDef)) continue;
                    foreach (var c in kvp.Value) keepCols.Add(c);
                }
            }

            // 2) PK / FK 컬럼
            foreach (var col in dep.Columns)
            {
                if (col.IsPrimaryKey || col.IsForeignKey) keepCols.Add(col.ColumnName);
            }

            // 3) 인덱스 구성 컬럼
            if (dep.Indexes != null)
            {
                foreach (var idx in dep.Indexes)
                {
                    foreach (var c in idx.Columns) keepCols.Add(c);
                }
            }

            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in dep.Columns)
            {
                // keepCols가 비어 있으면 정적 분석 정보가 없는 것으로 보고 전체를 찍는다.
                if (keepCols.Count > 0 && !keepCols.Contains(col.ColumnName)) continue;
                shown.Add(col.ColumnName);
            }

            return shown;
        }

        /// <summary>
        /// 참조 컬럼 키가 통째로 유실됐는지 본다.
        ///
        /// 명제: 키 K가 임시 테이블이 아니고, <b>실제 매칭에서 어떤 의존성에도 병합되지
        /// 않았는데</b>, 베이스 이름으로는 컬럼을 가진 의존성과 맞는다면 - K의 컬럼들은
        /// 프롬프트 어디에도 실리지 않았다.
        ///
        /// 첫째 조건이 "정식 비교 실패"가 아니라 "실제 매칭 실패"인 것이 중요하다.
        /// KeyMatchesDependency는 DB 컨텍스트가 없을 때 이미 베이스 이름 비교로
        /// 내려간다. 조건을 정식 비교로 못 박으면 그 폴백 경로의 정상 동작이 전부
        /// 위반으로 보고된다.
        ///
        /// 이 위반은 재생성으로 고칠 수 없다 - 프롬프트가 거짓말을 한 코드 버그이지
        /// AI의 잘못이 아니다. 그래서 호출부는 이것을 L1 오류가 아니라 경고로 다룬다.
        /// </summary>
        public static IReadOnlyList<string> DetectOrphanedColumnKeys(SpDefinition spDef)
        {
            var defects = new List<string>();
            var analysis = spDef.StaticAnalysis;
            if (analysis?.ReferencedColumnsPerTable == null) return defects;

            foreach (var kvp in analysis.ReferencedColumnsPerTable)
            {
                var key = kvp.Key;
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (kvp.Value == null || kvp.Value.Count == 0) continue;

                var trimmed = key.TrimStart();
                if (trimmed.StartsWith("#", StringComparison.Ordinal)
                 || trimmed.StartsWith("@", StringComparison.Ordinal))
                {
                    continue;
                }

                if (spDef.Dependencies.Any(dep => KeyMatchesDependency(key, dep, spDef)))
                {
                    continue; // 어딘가에 병합됐다.
                }

                var baseName = ExtractBaseName(key);
                var lookalikes = spDef.Dependencies
                    .Where(dep => dep.Columns.Count > 0
                               && string.Equals(ExtractBaseName(dep.Name), baseName, StringComparison.OrdinalIgnoreCase))
                    .Select(dep => StaticAnalysisNormalizer.CanonicalizeParts(
                        dep.Database, dep.Schema, dep.Name, spDef.ObjectKey?.Database, spDef.Schema))
                    .ToList();

                if (lookalikes.Count == 0) continue;

                defects.Add(
                    $"[스키마 프롬프트] 참조 컬럼 키 `{key}`가 어떤 의존성에도 병합되지 않아 " +
                    $"컬럼 {kvp.Value.Count}개({string.Join(", ", kvp.Value)})가 프롬프트 스키마 표에서 누락되었습니다. " +
                    $"이름이 같은 의존성: {string.Join(", ", lookalikes)}. " +
                    "명세서가 해당 컬럼을 \"존재하지 않음\"으로 기술할 수 있습니다.");
            }

            return defects;
        }

        /// <summary>
        /// ReferencedColumnsPerTable의 키 하나가 이 의존성의 것인지 판정한다.
        ///
        /// 비교 양변의 한정 가능한 출처가 다르다. 의존성 쪽은 dep.Database가 있으면
        /// 그것으로 한정되지만, 키 쪽의 비한정 이름(예: "TSettleMst")이 암묵적으로
        /// 속하는 DB는 "분석 대상 객체 자신의 DB"이지 하필 지금 비교 중인 의존성의
        /// DB가 아니다. dep.Database로 키를 한정하면 존재하지 않는 테이블을 지어내는
        /// 것과 같다. 그래서 키 쪽 한정 가능 여부는 오직 spDef.ObjectKey?.Database
        /// 하나로만 결정된다.
        ///
        /// 컨텍스트가 없으면 베이스 이름 비교로 내려가 과다 포함 쪽으로 기운다. 이
        /// 폴백은 완전히 무해하지는 않다 - 스키마가 다른 진짜 다른 테이블의 컬럼이
        /// 섞일 수 있다. 그래도 거짓 "컬럼 없음"보다는 낫다.
        /// </summary>
        internal static bool KeyMatchesDependency(string key, DependencyInfo dep, SpDefinition spDef)
        {
            var depCanonicalName = StaticAnalysisNormalizer.CanonicalizeParts(
                dep.Database, dep.Schema, dep.Name, spDef.ObjectKey?.Database, spDef.Schema);

            var hasDbContext = !string.IsNullOrWhiteSpace(spDef.ObjectKey?.Database);

            var keyCanonicalName = StaticAnalysisNormalizer.Canonicalize(
                key, spDef.ObjectKey?.Database, spDef.Schema);

            return hasDbContext
                ? string.Equals(keyCanonicalName, depCanonicalName, StringComparison.OrdinalIgnoreCase)
                : string.Equals(
                    ExtractBaseName(keyCanonicalName), ExtractBaseName(dep.Name), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// canonical 이름(또는 원시 이름)에서 마지막 세그먼트만 뽑는다.
        /// DB 컨텍스트가 없어 3-part로 한정할 수 없을 때 폴백 비교 키로 쓴다.
        /// </summary>
        public static string ExtractBaseName(string? qualifiedOrRawName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedOrRawName)) return string.Empty;

            var trimmed = qualifiedOrRawName.Trim().Trim('[', ']');
            var lastDot = trimmed.LastIndexOf('.');
            return lastDot >= 0 ? trimmed[(lastDot + 1)..].Trim('[', ']') : trimmed;
        }
    }
}
