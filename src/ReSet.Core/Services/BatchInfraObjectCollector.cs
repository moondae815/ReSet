using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 계획서가 참조하는 신규 인프라 스키마 객체의 목록.
    /// </summary>
    /// <param name="Names">정규화·중복 제거·정렬된 객체명.</param>
    /// <param name="CollapsedRunIdVariants">자리표시자가 리터럴로 굳어 접힌 원문.
    /// 사람이 규칙 위반을 볼 수 있게 함께 낸다 - 접기만 하고 숨기면 계획서가
    /// 규칙을 어겼다는 사실이 어디에도 남지 않는다.</param>
    public sealed record BatchInfraObjects(
        IReadOnlyList<string> Names,
        IReadOnlyList<string> CollapsedRunIdVariants)
    {
        public static BatchInfraObjects Empty { get; } =
            new(Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// 계획서에서 batch·batch_shadow 스키마 객체를 수집한다.
    ///
    /// 이 클래스가 접두사 정의를 <b>단독 소유</b>한다. 회차 0 지시서(TaskFileComposer)와
    /// 미지 테이블 검사(MechanicalValidator)가 같은 판단을 해야 하기 때문이다 - 두 곳이
    /// 각자 접두사를 알면 한쪽이 신규 접두사를 놓쳤을 때 다른 쪽이 그 객체를 전부
    /// "존재하지 않는 테이블"로 오탐한다.
    /// </summary>
    public static class BatchInfraObjectCollector
    {
        /// <summary>Shadow 이름 규칙의 실행 식별자 자리표시자.</summary>
        public const string RunIdPlaceholder = "_<RunId>_";

        /// <summary>
        /// 접두사 정의의 단일 소스. ObjectRegex는 이 목록에서 패턴을 만들어 낸다 -
        /// 정규식 리터럴에 접두사를 따로 적으면 여기 목록과 갈라질 수 있고, 그러면
        /// 이 클래스가 주장하는 "단독 소유"가 이름만 남는다. 미지 테이블 검사
        /// (MechanicalValidator)도 이 목록을 그대로 재사용해 같은 함정을 피한다.
        /// </summary>
        public static readonly IReadOnlyList<string> Schemas = new[] { "batch", "batch_shadow" };

        // 패턴은 Schemas에서 파생한다. 길이 내림차순으로 정렬해 "batch_shadow.X"가
        // "batch"에서 먼저 걸려 '.'을 못 찾고 백트래킹하는 일을 막는다.
        //
        // 객체명은 `<RunId>` 조각을 품을 수 있다. 이것을 못 읽으면 정규식이 '<'에서
        // 멈춰 `batch_shadow.TSettleByTX_<RunId>_S11`이 `batch_shadow.TSettleByTX_`라는
        // 잘린 이름으로 목록에 오른다 - 접기 결과로 우리가 내보내는 표기
        // (RunIdPlaceholder)를 정작 우리가 다시 읽지 못하던 결함이다. 허용은 이 한
        // 조각으로 좁힌다. '<'를 통째로 이름 문자에 넣으면 테이블 셀의 `<br/>`가
        // 객체명에 붙어 들어온다.
        private static readonly Regex ObjectRegex = new(
            $@"\b({string.Join("|", Schemas.OrderByDescending(s => s.Length).Select(Regex.Escape))})\.([A-Za-z_][A-Za-z_0-9]*(?:<RunId>[A-Za-z_0-9]*)*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RunIdLiteralRegex = new(
            @"_(?:RunId|Run)_",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static BatchInfraObjects Collect(string? planMarkdown)
        {
            if (string.IsNullOrWhiteSpace(planMarkdown))
            {
                return BatchInfraObjects.Empty;
            }

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var collapsed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in ObjectRegex.Matches(planMarkdown))
            {
                var schema = match.Groups[1].Value.ToLowerInvariant();
                var rawName = match.Groups[2].Value;
                var normalized = RunIdLiteralRegex.Replace(rawName, RunIdPlaceholder);

                if (!string.Equals(normalized, rawName, StringComparison.Ordinal))
                {
                    collapsed.Add($"{schema}.{rawName}");
                }

                names.Add($"{schema}.{normalized}");
            }

            return new BatchInfraObjects(names.ToList(), collapsed.ToList());
        }

        /// <summary>
        /// 이 이름이 배치 전용 스키마를 쓰되 그 이름이 <see cref="Schemas"/>가 아닌가.
        ///
        /// 판정을 여기 두는 이유는 IsInfraObject와 같다 - 배치 스키마 이름의 정의를
        /// 이 클래스가 단독으로 소유한다. 검증기가 자기 목록을 따로 들면 두 곳이
        /// 갈라진다.
        /// </summary>
        public static bool IsNonCanonicalBatchObject(string? qualifiedName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedName))
            {
                return false;
            }

            var parts = qualifiedName.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            // 마지막 조각은 객체명이므로 보지 않는다 - dbo.TBatchLog는 이름에 batch가
            // 들어갈 뿐 업무 테이블이다. 한정자 조각만 본다.
            for (var i = 0; i < parts.Length - 1; i++)
            {
                // 이름이 batch로 끝나는지를 본다. 포함으로 넓히면 C# 타입의 멤버 접근이
                // 걸린다 - 실측: BatchStepResult.LegacyRetVal. 실제로 갈라진 이름들
                // (poqbatch·poqsettlebatch·POQBatch)은 하나같이 batch로 끝난다.
                var qualifier = parts[i].Trim('[', ']').Trim();
                if (qualifier.EndsWith("batch", StringComparison.OrdinalIgnoreCase) &&
                    !Schemas.Contains(qualifier, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 이 이름이 계획서가 새로 만드는 인프라 객체인가. 카탈로그에 없는 것이
        /// 정상이므로 미지 테이블 검사에서 제외해야 한다.
        /// </summary>
        public static bool IsInfraObject(string? qualifiedName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedName))
            {
                return false;
            }

            var parts = qualifiedName.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            // 마지막 조각은 객체명이다. 그 앞의 어느 조각이든 batch 계열이면 인프라다
            // (3부 식별자 SETTLE_POQ_DB.batch.X도 인정한다).
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (Schemas.Contains(parts[i], StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
