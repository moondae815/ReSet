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
        ///
        /// <b>디렉터리가 지시 형태이고 파일명은 허용 형태다.</b> 둘을 나란히 권하면 안 된다:
        /// 자동 탐색의 규칙 1(파일 정확 일치)이 규칙 2(디렉터리)보다 먼저 이기고 소스 루트를
        /// 재귀로 훑으므로, 에이전트가 파일 쪽을 고르면 Job 전체 계획서가 <b>진입점 파일 하나</b>와
        /// 대조된다. 매핑 게이트는 통과하지만 L2가 다단계 계획서를 파일 하나와 견주어 MISMATCH를
        /// 내고, 구현이 완전해도 조립 회차가 VerificationFailed로 끝난다 - C1의 증상이 C1의
        /// 수정이 연 문으로 되돌아오는 것이다. Java에서 main 클래스를 Job 이름으로 짓는 것이
        /// 관용이라 특히 밟기 쉽다.
        /// </summary>
        public static string DescribeJobArtifactNaming(string jobName, string targetLanguage)
        {
            var extension = targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase) ? ".java" : ".cs";
            var directories = string.Join(", ", JobProjectDirectoryNames(jobName).Select(name => $"`{name}/`"));

            return
                $"- **산출물 이름 규약(필수)**: Job 전체 검증은 계획서와 소스 트리를 `{jobName}`이라는 이름으로 짝짓습니다. " +
                $"작업 디렉터리 바로 아래에 {directories} 중 하나의 **디렉터리**를 만들고 그 안에 프로젝트를 두십시오. " +
                $"(이미 `{JobEntryPointFileBaseName(jobName)}{extension}`처럼 Job 이름과 정확히 같은 진입점 파일이 있어도 인정됩니다. " +
                "다만 그 경우 검증이 계획서 전체를 그 파일 하나와만 대조하므로 권장하지 않습니다 — 디렉터리 형태를 쓰십시오.) " +
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
