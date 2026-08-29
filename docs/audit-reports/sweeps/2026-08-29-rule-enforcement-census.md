# 통합 배치 계획 규칙의 강제 수단 전수 조사 (2026-08-29)

`ConsolidatedPlanRules`의 규칙 하나하나가 **무엇으로 강제되는가**를 전수로 셌다.
물음은 「규칙이 있는가」가 아니라 **「모델이 바뀌어도 지켜지는가」**다.

계기: 4단계 2차 통제군(`POQSettleBatch3`)에서 L1이 **아무것도 발화하지 않았는데**,
그 문서에는 우리가 이미 아는 위반이 둘 있었다 — 규칙 3-1이 금지한 API 지정 11건,
`S05`의 T-SQL 잔존. **침묵이 깨끗함이 아니라 실명(失明)이었다.**

선행: `docs/superpowers/specs/2026-08-27-stage3-rule-rewrite-design.md` §9·§10

## 0. 측정 조건

- 소스를 직접 읽어 셌다. 산출물 표본이 아니라 **코드가 부르는 검사 목록**이다.
- 대상: `src/ReSet.Core/Services/MechanicalValidator.cs`(검사 정의 49개),
  `AiService.cs`의 `ConsolidatedPlanRules`(규칙 번호 21개 · 하위 조항을 갈라 24행).
- 잰 시각의 HEAD는 `dd6041e`. 코퍼스는 이 조사에 쓰지 않았다.
- 「기계 강제」는 **L1만** 센다. 프롬프트와 Critic은 둘 다 모델 재량이므로 이 조사의
  분모가 아니라 분자를 만드는 쪽이다.

## 1. 강제 수단은 세 경로, 그중 둘이 이 축이다

| 진입점 | 검사 수 | 받는 것 | 이 조사의 대상 |
|---|---:|---|:--|
| `Validate(markdown, expectations)` | 26 | SP 명세서 | ✘ 축 B의 것 |
| `ValidateConsolidated(markdown)` | **4** | 계획서 본문**만** | ✔ |
| `ValidateBatchStep(...)` | **16** | 단계 + `SpecExpectations` | ✔ |

`ValidateConsolidated`가 받는 것이 `string` 하나뿐이라는 사실이 §4의 설계 제약을 낳는다.

**L1 실패는 진짜 강제다.** `VerificationPipelineOrchestrator.cs:2006`이 `!IsValid`면
`ComposeAfterL1Failure`로 자가 수정을 되돌린다(:2013). 보고가 아니라 되돌림이고,
정규식 판정이라 **모델과 무관**하다.

## 2. 규칙 21개 × 기계 강제

✔ 온전 · ◐ 부분 · ✘ 없음

| 규칙 | 요구 | 강제 | 검사 |
|---|---|:--:|---|
| 1 | Korean Markdown | ✘ | |
| 2 | H2 넷 | ✔ | `ValidateMarkdownStructure` |
| 2-a | 검증 SQL의 `CROSS JOIN` 금지 | ✔ | `CheckVerificationCartesianComparison` |
| 2-b | 의사코드는 앱 코드다 | ✘ | |
| 3 | 순서 보존·병렬 금지 | ✘ | |
| **3-1** | **SQL 거처** — 신규 SP·제어 흐름·API 금지 | **✘** | **없음** |
| 4 | SNAPSHOT 격리 의무 | ✘ | |
| 4 | Shadow 역학 (a)~(e) | ◐ | `CheckShadowBackupContract` |
| 4-1 | `batch`·`batch_shadow` 스키마 | ✔ | `CheckNonCanonicalBatchSchema` |
| 5 | 재시작 파라미터 추가 금지 | ✔ | `CheckStepInterface` |
| 6 | 청크 DELETE에 청크 키 | ✘ | |
| 6-1 | 실패 지점 충실도 | ◐ **위태** | `CheckLegacyReturnCodeBinding` + §3의 둘 |
| 6-2 | 제어 단계 예약 코드 대역 | ✔ | `CheckControlStepErrorCodeBand` |
| 7 | 비즈니스 로직 축약 금지 | ✔ | `CheckMissingConditionColumns`·`CheckStatementCountAgainstSpec`·`CheckAnchoredStatementFacts`·`...Extras` |
| 7-1 | UNION 분기 컬럼 정렬 | ✘ | |
| 8 | 청크 필터 보존 | ◐ | `CheckMissingConditionColumns` 부분 |
| 8-1 | 청크마다 자기 트랜잭션 | ✘ | |
| 9 | 원본 오류 코드 재사용 | ◐ | 레거시 **없는** 단계만(`CheckControlStepErrorCodeBand`) |
| 10 | `NOLOCK` 금지 | **✘** | 검사 0건 |
| 11 | INSERT 전용 롤백 | ◐ | `CheckShadowBackupContract` 부분 |
| 12 | 청크 키가 실재하는 컬럼인가 | ✘ | 테이블은 보나 **컬럼은 안 본다** |
| 13 | 출력 파라미터 매핑 | ◐ | `CheckStepInterface` 부분 |
| 14 | 전체를 코드블록으로 감싸지 말 것 · mermaid 블록 사용 | ◐ | 직접 검사 없음. `PostProcessMarkdown`은 **mermaid 안쪽만 정화**하고(`:5776`), 전체를 감싸면 H2 검사가 간접적으로 걸린다 |
| 15 | 잡담 금지 | ✘ | |

**온전 6 · 부분 7 · 전무 11** (하위 조항을 갈라 24행).
규칙 번호로는 21개이고, 규칙 2와 4가 성격이 다른 조항을 품고 있어 표에서 갈랐다.

> ⚠️ **부분(◐)을 「있다」로 읽지 말 것.** 규칙 9가 대표적이다 — 강제되는 것은
> 「레거시 출신이 **없는** 단계가 예약 대역을 지키는가」뿐이고, 규칙 9의 본문인
> 「레거시 출신 단계가 **원본 코드를 그대로 쓰는가**」는 아무도 안 본다.
> 2차 통제군에서 Critic이 `S11`·`S13`의 오류 코드 발명을 잡아냈는데, 그것은
> **모델이 잡은 것**이지 기계가 잡은 것이 아니다.

## 3. 이미 켜져 있으나 꺼질 수 있는 검사 셋

**「좋은 일이 방어를 끈다」의 형태다.** 축 B 로드맵이 내내 경계한 것이고, 한 번은
실현됐다.

| 검사 | 무엇에 묶여 있나 | 상태 |
|---|---|---|
| `CheckStepIdInitialValue` | `DECLARE @v_currentStepId INT = 0` — T-SQL 철자 | **침묵 예고**(설계서 §8-4) |
| `CheckCatchDiscardsReturnCode` | `CATCH` 블록의 존재 | **침묵 예고** |
| `CheckSpecLocalVariablesDeclared` | 명세서 「지역 변수 표」 | **이미 18 → 0으로 꺼졌던 전례**(`known-defects.md` 5-3-7) |
| `CheckUnknownTableReferences` | 스키마 카탈로그가 비면 **조용히 건너뛴다**(`:1891`) | 조건부 침묵 |

앞의 둘은 3단계·이번 회차가 프롬프트에서 그 철자를 걷어냈으므로 **지금 침묵하고 있을
가능성이 높다.** 2차 통제군에서 L1 발화가 0이었으나, 그것이 「깨끗」인지 「침묵」인지
**가르지 않았다.** 가르는 것이 이 조사의 후속 과제다.

> **판정법**: 발화 0을 통과로 읽지 말고, 그 검사가 **재료를 얻었는지**를 따로 확인한다.
> 검사 D가 그렇게 꺼졌다 — 좌표 차분도 침묵 분모도 못 봤고, 검사별 총 발화량을 전후로
> 나란히 놓아서야 보였다.

## 4. 설계 제약 — 신규 SP 검사는 시그니처를 넓혀야 한다

규칙 3-1은 `CREATE PROCEDURE`를 **원본 인용일 때만** 허용한다. 그러므로 검사는
「이 이름이 이 Job의 레거시 프로시저인가」를 물어야 하는데,
`ValidateConsolidated(string markdown)`은 **본문만 받는다.**

선택지 둘:

1. **시그니처를 넓힌다** — `ValidateConsolidated(string, IReadOnlyCollection<string>? legacyProcedureNames = null)`.
   호출부는 `VerificationPipelineOrchestrator.cs:2004`와 `:2476` 둘뿐이고, 그 자리에
   Job의 원본 프로시저 목록이 이미 있다. 기본값을 두면 기존 테스트가 안 깨진다.
2. **문맥으로 가른다** — 인용은 보통 원본 DDL 블록 안에 있다. 재료가 필요 없지만
   판정이 약하고, 「인용처럼 보이게 쓰면 통과」라는 우회를 남긴다.

**1을 권한다.** 2는 이 규칙이 겨누는 위반(모델이 새 프로시저를 지어내는 것)을
정확히 놓치는 쪽으로 틀린다.

## 5. 기계화 순위

### A급 — 정규식 하나, 재료 불필요, 실측된 위반 있음

| # | 규칙 | 판정식 | 실측 근거 |
|---|---|---|---|
| 1 | **10 NOLOCK** | `WITH\s*\(\s*NOLOCK` | 규칙이 "explicitly remove ALL"이라 예외가 없다. 1차 통제군 코드 안 2건 |
| 2 | **3-1 API 지정** | `SqlConnection`·`SqlCommand`·`SqlParameter`·`IsolationLevel\.`·`TransactionScope`·`DbContext`·`PreparedStatement`·`EntityManager` | **2차 통제군 11건. Critic이 보고도 통과시켰다** |
| 3 | **3-1 제어 흐름** | 새 SQL의 `GOTO`·`IF @@ERROR`·`BEGIN TRY` | 1차 통제군 `GOTO` 20 · `@@ERROR` 18 |

3번의 유일한 설계점은 **원본 인용을 어떻게 가르는가**다. §4와 같은 물음이므로 4번과
함께 다루면 한 번에 풀린다.

### B급 — 구조를 넓혀야 함

| # | 규칙 | 필요한 것 |
|---|---|---|
| 4 | **3-1 신규 저장 프로시저** | 원본 프로시저 목록 (§4) |
| 5 | 12 청크 키 실재 | 대상 테이블 DDL의 컬럼 목록 — `raw/ddl`에 이미 있다 |
| 6 | 8-1 청크 트랜잭션 경계 | **보류.** 새 Few-Shot의 두 층 표기에 의존하므로 그 표기가 정착한 뒤 |

### C급 — 판정 기준부터 설계해야 함

7-1 UNION 분기 정렬(컬럼 목록 파싱 필요) · 3 순서·병렬(판정 모호) ·
4 격리 의무(「어디서 거는지 정하지 말라」는 규칙이라 부재를 벌할 수 없다) ·
6 청크 DELETE 필터 · 2-b 의사코드 언어.

규칙 1·15는 기계화 가치가 낮다.

## 6. 왜 이 순위인가 — 근거와 반례

**(1) 재량 절에 기댄 축은 모델 교체로 조용히 꺼진다.** 이것이 순위의 제1 기준이다.
실측 전례: 명세서 Actor를 `gpt-5.6-terra` → `deepseek-v4-pro-0813`으로 바꾸자 프롬프트가
요구한 「지역 변수 표」가 사라져 `CheckSpecLocalVariablesDeclared`가 **18 → 0**으로 꺼졌고,
**잃은 18건은 진짜 결함이었다**(스냅샷 복원으로 확인). 관측 변화는 없었다.

**(2) 「모델이 지키더라」는 강제가 아니다.** 2차 통제군의 신규 저장 프로시저 0은 규칙의
효력이 아니라 **`claude-sonnet-5`가 원래 그것을 쓰지 않는다**는 사실이었다 — 같은 모델의
1차 통제군이 **옛 규칙으로도 0**이었다. 기준선에서 유일한 Claude 표본 `Proc4`도 옛 규칙에
0이다. 이 축은 지금도 **한 번도 강제된 적이 없다.**

**(3) 반례 — Critic은 무능하지 않다.** 1차 통제군에서 명시 조항이 없는데도 Critic이
"SQL 의사코드 안에 TRY/CATCH·트랜잭션 제어를 두어 정책과 충돌한다"를 규칙 3-1에서
파생해 잡아냈다. **그러므로 이 조사는 「Critic을 L1로 대체하자」가 아니다.** Critic은
파생할 줄 알지만 그 능력이 모델마다 다르고, 2차 통제군의 Critic(`glm-5.3`)은 API 지정을
보고도 통과시켰다. **기계는 모델이 바뀌어도 같은 것을 본다** — 그 점 하나가 다르다.

**(4) 강제는 셋이 겹쳐야 한다.** L1은 산출물을 사후에 보고 재생성으로 되돌리므로 시도를
소모한다. 프롬프트가 먼저 옳게 쓰게 하고, Critic이 파생으로 넓게 잡고, L1이 놓칠 수 없는
축을 못박는다. A급 셋은 **L1이 없어서 두 층뿐이던 축**들이다.

## 7. 재실행 레시피

```bash
# 진입점별 검사 목록
grep -n "public .*Validate[A-Za-z]*(" src/ReSet.Core/Services/MechanicalValidator.cs
sed -n '214,262p' src/ReSet.Core/Services/MechanicalValidator.cs        # ValidateConsolidated
sed -n '287,570p' src/ReSet.Core/Services/MechanicalValidator.cs \
  | grep -o "Check[A-Za-z]*(" | sort -u                                 # ValidateBatchStep

# 규칙 목록
python3 - <<'PY'
import io,re
s=io.open('src/ReSet.Core/Services/AiService.cs',encoding='utf-8').read()
i=s.index('private const string ConsolidatedPlanRules'); j=s.index('[Few-Shot Examples',i)
for m in re.finditer(r'^(\d+(?:-\d+)?)\. (\[[^\]]+\]|[A-Z][^\n]{0,60})', s[i:j], re.M):
    print(m.group(1), m.group(2))
PY

# 특정 규칙에 검사가 있는지 (예: NOLOCK)
grep -n -i "NOLOCK" src/ReSet.Core/Services/MechanicalValidator.cs
```

**주의**: `grep -c "Check"`로 세지 말 것. 정의는 49개인데 계획서·단계 경로가 부르는 것은
20개뿐이고 나머지는 명세서 경로(축 B)의 것이다. **정의 수가 아니라 호출 목록을 세라.**
