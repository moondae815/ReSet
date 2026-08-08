using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코딩 에이전트가 남길 산출물의 이름 규약을 단독으로 소유한다.
    ///
    /// 이 클래스가 존재하는 이유는 <b>규약을 말하는 쪽과 규약을 판정하는 쪽이 갈라졌기
    /// 때문이다.</b> 조립 회차의 Job 전체 검증은 계획서(BatchMigrationPlan.md)의 조부모
    /// 디렉터리 이름(= Job 이름)으로 소스를 자동 탐색하는데, 그 이름 규약이 도구가 쓰는
    /// 어떤 지시서에도 적혀 있지 않았다. 그래서 모든 회차가 통과한 성공 실행이
    /// "매핑 0건 → Unrecoverable → 조립 실패"로 끝났다 - 가장 초록이어야 할 실행이
    /// 빨강으로 보고됐다.
    ///
    /// 판정부(FileMappingService의 자동 탐색)와 지시부(TaskFileComposer)가 여기 하나를
    /// 같이 읽는다. 한쪽만 고치면 컴파일이 되더라도 테스트가 두 쪽을 교차 검증한다.
    /// </summary>
    public static class CodegenArtifactNaming
    {
        /// <summary>
        /// Job 전체 검증의 자동 탐색이 프로젝트 디렉터리로 인정하는 이름들.
        /// 순서는 탐색 순서와 같다(먼저 존재하는 것이 채택된다).
        /// </summary>
        public static IReadOnlyList<string> JobProjectDirectoryNames(string jobName)
        {
            var noUnderscore = (jobName ?? string.Empty).Replace("_", string.Empty);

            return new[]
                {
                    $"{noUnderscore}.Batch",
                    $"{jobName}.Batch",
                    noUnderscore,
                    jobName ?? string.Empty,
                }
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// 자동 탐색이 <b>정확 일치</b>로 인정하는 진입점 파일의 확장자 없는 이름.
        /// 단계 회차의 접두사 규칙(StartsWith)과 달리 여기는 완전 일치여야 한다.
        /// </summary>
        public static string JobEntryPointFileBaseName(string jobName) => jobName ?? string.Empty;

        /// <summary>
        /// 조립 회차 지시서에 실을 이름 규약 문구. 판정부가 실제로 인정하는 이름을
        /// 그대로 나열한다 - 예시를 손으로 적으면 판정부와 조용히 갈라진다.
        /// </summary>
        public static string DescribeJobArtifactNaming(string jobName, string targetLanguage)
        {
            var extension = targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase) ? ".java" : ".cs";
            var directories = string.Join(", ", JobProjectDirectoryNames(jobName).Select(name => $"`{name}/`"));

            return
                $"- **산출물 이름 규약(필수)**: Job 전체 검증은 계획서와 소스 트리를 `{jobName}`이라는 이름으로 짝짓습니다. " +
                $"작업 디렉터리 바로 아래에 {directories} 중 하나의 디렉터리를 두거나, " +
                $"진입점 파일을 `{JobEntryPointFileBaseName(jobName)}{extension}`(확장자 앞이 정확히 이 이름)로 두십시오. " +
                "이 규약을 벗어나면 구현이 완전해도 조립 회차가 실패로 기록됩니다.";
        }

        /// <summary>
        /// 단계 회차 지시서에 실을 이름 규약 문구.
        ///
        /// 조립 회차와 규칙 모양이 다르다(이쪽은 접두사, 저쪽은 완전 일치/디렉터리).
        /// 그래서 두 문구 모두 "어느 게이트의 규칙인지"를 명시한다 - 한쪽 문구를 일반화해
        /// 다른 회차에 적용하면 게이트를 통과하지 못한다.
        /// </summary>
        public static string DescribeStepArtifactNaming(string stepCodePrefix, string targetLanguage)
        {
            var extension = targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase) ? ".java" : ".cs";

            return
                $"- **산출물 이름 규약(필수)**: 이 회차의 Tasklet 파일 이름은 정확히 `{stepCodePrefix}`로 시작해야 합니다 " +
                $"(예: `{stepCodePrefix}Tasklet{extension}`). 접두사 바로 뒤에 숫자를 붙이지 마십시오 - " +
                "다른 회차의 코드로 오인됩니다. 이 규약을 벗어나면 이 회차는 검증되지 못한 채 재시도됩니다.";
        }
    }
}
