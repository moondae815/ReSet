using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// L1 위반이 문서의 어느 구역에서 일어났는지 찾는다 - 단계 섹션인가, 골격인가.
    ///
    /// 이 클래스가 존재하는 이유: 실측(POQSettleBatch4 2026-08-29)의 3차 L1 실패는
    /// 규칙 3-1 위반 `END TRY` 하나였고 4차는 `batch.BatchRun` INSERT 부재였다.
    /// 지점이 특정되는 결함인데도 문서 전체를 다시 만들었고, 그렇게 두 회차를 태웠다.
    ///
    /// [골격 구역이 왜 필요한가 - 반복 실행 3판 실측(2026-09-04)]
    /// 단계만 귀속 대상으로 두면 골격에 있는 위반은 영영 안 고쳐진다. 판3의 L1 발화
    /// 7회는 <b>전부</b> 골격 mermaid였고 14건이 모두 같은 화살표 오류였는데, 골격
    /// 생성 호출 2회가 둘 다 첫 실패 이전이라 깨진 다이어그램이 글자 하나 안 바뀐 채
    /// 6회차를 전부 태웠다. 판2는 공통 규약 절의 `IF @@ROWCOUNT = 0`이 같은 일을 했다.
    /// 그 둘은 귀속 실패 → 전량 재생성으로 떨어졌고, 전량 재생성조차 골격은 다시 만들지
    /// 않았다(호출부가 <c>lastSkeleton</c>을 남겨 둔다). 채점 못한 회차 판2 3 · 판3 5다.
    ///
    /// 귀속하지 못하면 아무것도 주장하지 않는다(<see cref="L1Attribution.None"/>).
    /// 억지로 아무 단계에나 붙이면 멀쩡한 단계를 다시 쓰게 되어, 회귀 롤백이 막으려는
    /// 회귀를 다시 들인다. 호출부는 빈 귀속을 "전량 재생성"으로 읽는다.
    ///
    /// 한 어휘가 문서 안 여러 단계 섹션에 나타나면 그 단계 전부를 담는다 - 처음
    /// 발견한 단계 하나로 멈추지 않는다(최종 whole-branch 리뷰, Important 5 참고).
    /// </summary>
    public static class L1ViolationAttribution
    {
        /// <summary>
        /// 위반 하나가 여는 자리. 단계와 골격은 서로 배타적이지 않다 - 같은 어휘가
        /// 공통 규약 절과 단계 본문에 함께 있으면 둘 다 열어야 한다. 한쪽만 고치면
        /// 다음 회차에 같은 위반으로 다시 실패하면서 예산만 태운다.
        /// </summary>
        public sealed record L1Attribution(IReadOnlyList<string> StepCodes, bool Skeleton)
        {
            public static readonly L1Attribution None = new(Array.Empty<string>(), false);

            /// <summary>어느 자리도 지목하지 못했다 - 호출부의 전량 재생성 조건이다.</summary>
            public bool IsEmpty => StepCodes.Count == 0 && !Skeleton;
        }

        /// <summary>
        /// 어휘가 나타나는 모든 자리를 구역으로 갈라 돌려준다.
        ///
        /// 하나만 돌려주면 안 되는 이유(최종 whole-branch 리뷰, Important 5): 규칙
        /// 3-1(`BEGIN TRY`/`END TRY`)·규칙 10류 위반은 체계적이다 - 모델이 한 단계에서
        /// 그렇게 쓰면 보통 여러 단계에서 그렇게 쓴다. <see cref="MechanicalValidator"/>는
        /// 검사당 <c>DetailedError</c> 하나만 내므로(발생당이 아니다) 이 메서드가 첫
        /// 발견에서 멈추면 나머지 위반 자리는 <c>StepFreezeState</c>가 영영 열지 않는다 -
        /// L1이 다음 회차에도 같은 위반으로 다시 실패하면서 Job 전체 예산인
        /// <c>l1RepairAttempt</c>만 태우다가 소진된다.
        ///
        /// 코드 펜스 안을 건너뛰지 않는 이유: 위반 어휘 자체가 대개 SQL 코드 블록
        /// 안에 있다(`END TRY`가 그렇다). 헤딩 탐지만 펜스를 존중한다 -
        /// MarkdownSectionLocator.ComputeFenceFlags로 줄마다 펜스 여부를 미리 계산해
        /// 헤딩 후보 판정에서만 펜스 안 줄을 걸러낸다. 펜스 안에 `###`로 시작하는 줄이
        /// 있어도(예: SQL 주석) 그것은 진짜 단계 헤딩이 아니다.
        ///
        /// <paramref name="steps"/>가 없으면 아무것도 주장하지 않는다 - 단계 목록이
        /// 없는 회차는 단일 호출 폴백 경로이고, 그 경로에는 재사용할 골격도 섹션
        /// 캐시도 없어 부분 재생성이라는 개념 자체가 없다.
        /// </summary>
        public static L1Attribution Attribute(
            string? documentMarkdown, string lexeme, IReadOnlyList<BatchStepPlan>? steps)
        {
            if (string.IsNullOrEmpty(documentMarkdown) ||
                string.IsNullOrWhiteSpace(lexeme) ||
                steps == null || steps.Count == 0)
            {
                return L1Attribution.None;
            }

            var lines = MarkdownSectionLocator.SplitLines(documentMarkdown);
            var owners = MapRegions(lines, steps);
            var attributedSteps = new List<string>();
            var skeleton = false;

            for (var i = 0; i < lines.Count; i++)
            {
                // 헤딩 줄 자체는 훑지 않는다 - 헤딩은 구역의 경계이지 본문이 아니다.
                if (owners[i].IsHeading) continue;
                if (lines[i].IndexOf(lexeme, StringComparison.OrdinalIgnoreCase) < 0) continue;

                switch (owners[i].Region)
                {
                    case Region.Step:
                        var code = owners[i].StepCode!;
                        if (!attributedSteps.Contains(code, StringComparer.OrdinalIgnoreCase))
                        {
                            attributedSteps.Add(code);
                        }
                        break;

                    case Region.Skeleton:
                        skeleton = true;
                        break;

                    // Region.Unknown: 판정 불가한 헤딩 아래로 들어온 자리다. 단계에도
                    // 골격에도 붙이지 않는다 - 이 발생만 건너뛰고 스캔은 계속한다
                    // (멈추지 않는다). 뒤에 나오는 판정 가능한 자리까지 이 발생 하나
                    // 때문에 포기하면 안 된다.
                }
            }

            return new L1Attribution(attributedSteps, skeleton);
        }

        /// <summary>
        /// 단계 코드만 필요한 호출부를 위한 얇은 창구. 판정 자체는
        /// <see cref="Attribute"/> 하나가 소유한다 - 두 벌로 두면 구역 판정이 갈라진다.
        /// </summary>
        public static IReadOnlyList<string> AttributeByLexeme(
            string? documentMarkdown, string lexeme, IReadOnlyList<BatchStepPlan>? steps) =>
            Attribute(documentMarkdown, lexeme, steps).StepCodes;

        /// <summary>
        /// 블록 본문(<see cref="DetailedError.RawContext"/>)을 문서에서 되찾아 그 구역을
        /// 돌려준다. <c>MermaidCliError</c>가 이것을 쓴다.
        ///
        /// [왜 어휘 검색으로는 안 되는가] mermaid 컴파일 로그에는 백틱이 하나도 없어
        /// <see cref="MechanicalValidator.ViolationLexemes"/>가 항상 빈 어휘를 낸다 -
        /// 귀속이 구조적으로 불가능했다(반복 실행 판3의 7회 발화가 전부 이 부류다).
        /// 블록의 줄들을 어휘로 쓰는 것도 안 된다 - mermaid의 `end` 한 줄이 SQL 본문의
        /// `END`에 걸려 무관한 단계를 연다.
        ///
        /// [왜 유형만 보고 골격으로 단정하지 않는가] 코퍼스 22편의 mermaid 블록 50개는
        /// 전부 골격에 있었다. 그래도 유형 기반 하드 귀속을 쓰면 단계 안 mermaid가
        /// 깨졌을 때 그 단계가 영영 얼어붙고, 골격만 헛되이 다시 만들면서 예산을 태운다.
        ///
        /// 빈 줄은 양쪽에서 걷어내고 남은 줄을 순서대로 대조한다 - 조립이 빈 줄을
        /// 넣거나 빼도 블록을 되찾을 수 있어야 한다. 문서에서 못 찾으면
        /// <see cref="L1Attribution.None"/>이다(억지로 붙이지 않는다).
        /// </summary>
        public static L1Attribution AttributeBlock(
            string? documentMarkdown, string? blockText, IReadOnlyList<BatchStepPlan>? steps)
        {
            if (string.IsNullOrEmpty(documentMarkdown) ||
                string.IsNullOrWhiteSpace(blockText) ||
                steps == null || steps.Count == 0)
            {
                return L1Attribution.None;
            }

            var needle = MarkdownSectionLocator.SplitLines(blockText)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
            if (needle.Count == 0) return L1Attribution.None;

            var lines = MarkdownSectionLocator.SplitLines(documentMarkdown);
            var owners = MapRegions(lines, steps);

            // 빈 줄을 걷어낸 문서 줄과 그 원래 인덱스.
            var dense = new List<(int Index, string Text)>();
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length > 0) dense.Add((i, trimmed));
            }

            var attributedSteps = new List<string>();
            var skeleton = false;

            for (var start = 0; start + needle.Count <= dense.Count; start++)
            {
                var matched = true;
                for (var offset = 0; offset < needle.Count && matched; offset++)
                {
                    matched = string.Equals(
                        dense[start + offset].Text, needle[offset], StringComparison.OrdinalIgnoreCase);
                }

                if (!matched) continue;

                var owner = owners[dense[start].Index];
                if (owner.Region == Region.Step &&
                    !attributedSteps.Contains(owner.StepCode!, StringComparer.OrdinalIgnoreCase))
                {
                    attributedSteps.Add(owner.StepCode!);
                }
                else if (owner.Region == Region.Skeleton)
                {
                    skeleton = true;
                }
            }

            return new L1Attribution(attributedSteps, skeleton);
        }

        /// <summary>
        /// 줄이 속한 구역. <see cref="Unknown"/>이 따로 있는 이유는 골격과 구분하기
        /// 위해서다 - "판정 불가한 헤딩 아래"를 골격으로 읽으면 골격을 다시 만들어도
        /// 안 고쳐질 위반으로 골격 예산을 태운다.
        /// </summary>
        private enum Region
        {
            Unknown,
            Skeleton,
            Step
        }

        private readonly record struct LineOwner(Region Region, string? StepCode, bool IsHeading);

        /// <summary>
        /// 줄마다 구역을 매긴다.
        ///
        /// 골격의 정의는 조립 구조가 정한다 - <see cref="BatchPlanAssembler"/>는 골격의
        /// `## 단계별 이행 상세 및 의사코드` 블록 <b>끝</b>에 단계 본문을 끼워 넣는다.
        /// 그러므로 골격이 쓴 자리는 (a) 그 H2 밖의 모든 것(개요·흐름도·마지막 검증 SQL
        /// 세트)과 (b) 그 H2 안에서 첫 단계 섹션이 시작되기 전까지(공통 규약 소절들)다.
        ///
        /// 첫 단계 섹션이 시작된 뒤에 나오는 판정 불가 헤딩(`### P20~P23.` 같은 묶음
        /// 헤딩, `#### Phase 1.` 같은 하위 헤딩)은 <see cref="Region.Unknown"/>이다 -
        /// 그 자리는 단계 본문일 수도, 모델이 끼워 넣은 것일 수도 있어 어느 쪽으로도
        /// 단정할 수 없다. H2를 만나면 언제나 골격으로 되돌아간다.
        /// </summary>
        private static LineOwner[] MapRegions(IReadOnlyList<string> lines, IReadOnlyList<BatchStepPlan> steps)
        {
            var fenceFlags = MarkdownSectionLocator.ComputeFenceFlags(lines);
            var owners = new LineOwner[lines.Count];

            // 문서 첫머리(첫 H2 앞의 제목·서두)도 골격이 쓴 자리다.
            var region = Region.Skeleton;
            string? stepCode = null;
            var inStepDetailBlock = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                if (!fenceFlags[i] && trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    var level = trimmed.Length - trimmed.TrimStart('#').Length;
                    if (level <= 2)
                    {
                        inStepDetailBlock = line.Contains(StepDetailBlockTitle, StringComparison.OrdinalIgnoreCase);
                        region = Region.Skeleton;
                        stepCode = null;
                    }
                    else
                    {
                        var (_, code) = ReadStepHeading(line, steps);
                        if (code != null)
                        {
                            region = Region.Step;
                            stepCode = code;
                        }
                        else if (!inStepDetailBlock || region == Region.Skeleton)
                        {
                            // 단계 상세 H2 밖의 하위 헤딩은 골격의 것이고, 그 H2 안이라도
                            // 아직 단계가 시작되기 전이면 공통 규약 소절이다.
                            region = Region.Skeleton;
                            stepCode = null;
                        }
                        else
                        {
                            region = Region.Unknown;
                            stepCode = null;
                        }
                    }

                    owners[i] = new LineOwner(region, stepCode, IsHeading: true);
                    continue;
                }

                owners[i] = new LineOwner(region, stepCode, IsHeading: false);
            }

            return owners;
        }

        /// <summary>
        /// `## 단계별 이행 상세 및 의사코드`의 제목부. 모델이 꼬리표를 붙여 쓰는 일이
        /// 잦아(BatchPlanAssembler.LocateStepDetailBlock 참고) 마커를 뗀 제목으로 본다.
        /// </summary>
        private static readonly string StepDetailBlockTitle =
            BatchPlanAssembler.StepDetailHeader.TrimStart('#').Trim();

        /// <summary>
        /// 한 줄이 단계 헤딩인지, 헤딩이라면 어느 단계 코드를 선언하는지를 함께 돌려준다.
        ///
        /// 반환을 <c>(IsHeading, Code)</c> 둘로 나누는 이유: 호출부가 세 상태를 구분해야
        /// 한다 - 헤딩이 아니면 구역을 그대로 두고, 헤딩이면서 코드가 확정되면 그 단계로
        /// 옮기고, 헤딩인데 코드를 확정할 수 없으면(<c>IsHeading=true, Code=null</c>)
        /// 그 자리를 단계에 붙이지 말아야 한다. 코드 하나만 돌려주면(이전 버전처럼)
        /// 뒤의 두 경우가 똑같이 "null"이 되어 구분이 사라진다 - 실측(BatchStepPlan.cs
        /// 주석)의 "### P20~P23." 같은 여러 단계를 묶은 헤딩이 그 구분 없이는 직전의
        /// 무관한 단계(P19)로 잘못 귀속됐다.
        ///
        /// `### S02. 이름` 또는 `#### S02. 이름` 꼴에서 목차가 아는 단계 코드를 읽는다.
        ///
        /// 헤딩 레벨을 `###`로 고정하지 않는 이유: 실측 산출물이 이미 갈린다
        /// (BatchStepPlan 참고) - 한쪽은 단계를 H3에, 다른 쪽은 H4에 두면서 같은 H4에
        /// 단계가 아닌 헤딩(`#### Phase 1.`)을 섞는다. 레벨로 가르는 대신 "헤딩이 선언하는
        /// 선행 코드가 정확히 무엇인가"로 가른다 - `#### Phase 1.`은 선행 토큰이
        /// "Phase"라 어떤 단계 코드와도 같지 않으므로 자연히 걸러진다(Code=null이지만
        /// IsHeading=true이므로 구역이 옮겨진다).
        ///
        /// 선행 토큰만 보는 이유(부분 문자열 포함 판정을 쓰지 않는 이유):
        /// PlanBoundaryResolver.TryLocateByCode가 이미 겪은 함정이다 - "### S02 (S01 이후)"
        /// 같은 헤딩에서 본문에 언급된 다른 단계 코드(S01)가 먼저 걸리면 그 단계로 잘못
        /// 귀속되고, 억지 귀속은 멀쩡한 단계를 다시 쓰게 만든다. 헤딩이 스스로 선언하는
        /// 선행 코드만 인정하면 이 함정을 원천에서 피한다.
        ///
        /// 선행 토큰이 어느 코드와도 정확히 같지 않으면(하나로 판정할 수 없으면) Code는
        /// null이다 - "### P20~P23."처럼 여러 단계를 묶은 헤딩, 목차에 없는 코드, 코드와
        /// 무관한 하위 헤딩이 모두 이 경우다.
        /// </summary>
        private static (bool IsHeading, string? Code) ReadStepHeading(
            string line, IReadOnlyList<BatchStepPlan> steps)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#", StringComparison.Ordinal)) return (false, null);

            var afterMarker = trimmed.TrimStart('#').TrimStart();
            var leadingToken = afterMarker
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(leadingToken)) return (true, null);

            var normalizedToken = leadingToken.Trim('.', ':', ')', ',', ';');

            var code = steps
                .Select(step => step.Code)
                .FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(c) &&
                    string.Equals(c, normalizedToken, StringComparison.OrdinalIgnoreCase));

            return (true, code);
        }
    }
}
