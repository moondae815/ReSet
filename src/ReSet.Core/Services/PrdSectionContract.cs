using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>Prd.md 한 섹션의 계약 - 헤딩, 요구 ID 접두사, 근거로 인용해도 되는 Spec 헤딩.</summary>
    public sealed record PrdSectionRule(
        string Heading,
        string IdPrefix,
        IReadOnlyList<string> AllowedSources);

    /// <summary>
    /// Prd.md 섹션이 Spec.md 섹션에서 파생되는 고정 관계.
    ///
    /// 이 표가 문서에만 있고 검사에 없으면 「파생 고정형」이라는 말이 지켜지지 않는다.
    /// 생성 프롬프트와 귀속 검사가 같은 표를 읽어야 둘이 갈라지지 않는다.
    /// </summary>
    public static class PrdSectionContract
    {
        public static readonly IReadOnlyList<PrdSectionRule> Sections = new[]
        {
            new PrdSectionRule("## 배경 및 목적", "REQ-BG", new[] { "## 개요" }),
            new PrdSectionRule("## 수행 조건 및 입력 계약", "REQ-IN", new[] { "## 파라미터 목록" }),
            new PrdSectionRule("## 데이터 요구사항", "REQ-DATA", new[] { "## CRUD 분석" }),
            new PrdSectionRule("## 기능 요구사항", "REQ-FUNC", new[] { "## 로직 흐름 요약" }),
            new PrdSectionRule(
                "## 예외 및 비기능 요구사항",
                "REQ-NFR",
                new[] { "## CRUD 분석", "## 로직 흐름 요약" }),
        };
    }
}
