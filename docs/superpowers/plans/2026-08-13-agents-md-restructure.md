# AGENTS.md 재구조화 및 문서 예산 게이트 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** AGENTS.md를 108KB에서 25~35KB로 줄여 행동 규칙만 남기고, 다시 커지지 못하도록 래칫 게이트와 스킬 수정으로 잠근다.

**Architecture:** 문서 작업(Task 2~6)과 게이트 구축(Task 7~8)과 스킬 수정(Task 9)의 세 갈래다. 문서 작업은 삭제가 아니라 **이동**이며, 모든 삭제 줄은 근거 위치를 대장에 남겨 검증 가능하게 한다. 게이트는 `tests/ReSet.Core.Tests`의 기존 정책 테스트 관용구(순수 스캐너 + 기준선 파일)를 그대로 따른다.

**Tech Stack:** .NET / xUnit (기존 테스트 프로젝트), POSIX sh (`scripts/`), Markdown

**설계 문서:** `docs/superpowers/specs/2026-08-13-agents-md-restructure-design.md`

## Global Constraints

- **삭제의 기본값은 이동이다.** 근거 위치를 대지 못하는 줄은 지우지 말고, 판정에 따라 클래스 `<summary>`나 `architecture.md §4.x`에 새로 쓰거나 AGENTS.md에 남긴다. 어디에도 해당하지 않을 때만 "의도적 삭제"로 분류해 개별 승인을 받는다.
- **거처 판정 질문은 하나다:** "이 문장을 어긴 코드가 나왔을 때, 무엇이 그것을 잡는가?" → 테스트가 잡음 = 한 줄+링크 / 그 파일 여는 사람만 = `<summary>` / 여러 파일 함께 봐야 = `architecture.md §4.x` / **사람의 판단만 = AGENTS.md 본문에 원문 유지**
- **라인 예산 600바이트** (UTF-8 바이트, 문자 수 아님. 한글 약 200자)
- **게이트는 단방향이다.** 상한 초과만 실패하고 밑으로는 자유다. `cancellation-policy-baseline.txt`의 양방향 잠금을 흉내내지 말 것.
- **`<!-- synced-through: c8d6074 -->` 주석은 변경하지 않는다.** 이번 작업은 코드 반영 범위를 넓히지 않는다.
- **빌드 경고는 정확히 8건**이어야 한다(모두 `DbMetadataServiceTests.cs`의 기존 CS8600/CS8602). 증분 빌드는 경고를 다시 보고하지 않으므로 반드시 `dotnet clean && dotnet build`로 센다.
- **`dotnet test`는 실패 0, 건너뜀 0**이어야 한다.
- **`git worktree`에서 작업한다** (AGENTS.md 범주 8 요구. Task 7에서 소스 트리에 파일을 추가한다).
- 문서 어조를 보존한다: 한국어, 기존 이모지 헤딩 패턴, 존댓말 규칙 문체.

---

### Task 1: 워크트리와 문서 감사 스크립트

삭제 대장을 손으로 쓰면 72개 항목에서 반드시 틀린다. 근거 위치를 기계가 찾게 한다.

**Files:**
- Create: `scripts/doc-audit.sh`
- Create: `docs/superpowers/plans/2026-08-13-deletion-ledger.md` (스크립트 출력을 담을 대장)

**Interfaces:**
- Produces: `./scripts/doc-audit.sh <시작행> <끝행>` — AGENTS.md의 해당 구간 각 줄에 대해 `근거위치` 또는 `근거없음`을 표준출력으로 낸다. Task 2와 Task 3~6이 모두 이것을 쓴다.

- [ ] **Step 1: 워크트리 생성**

```bash
cd /Users/payletter/git-root/ReSet
git worktree add .worktrees/agents-md-restructure -b agents-md-restructure
cd .worktrees/agents-md-restructure
./scripts/setup-worktree.sh
```

이후 모든 작업은 이 워크트리에서 한다.

- [ ] **Step 2: 감사 스크립트 작성**

`scripts/doc-audit.sh`:

```sh
#!/usr/bin/env sh
#
# AGENTS.md의 한 구간을 훑어, 각 줄이 서술하는 내용의 근거가 이미 어디에 있는지 찾는다.
#
# 삭제 대장을 손으로 쓰면 72개 항목에서 반드시 틀린다. "이 줄은 중복이니 지워도
# 된다"는 주장은 근거 위치를 댈 수 있을 때만 참이고, 그 확인은 기계가 해야 한다.
#
#   ./scripts/doc-audit.sh 20 133     §2 카탈로그 구간을 감사한다
#
# 출력: 줄번호 | 바이트 | 대상식별자 | architecture.md 분량 | 코드주석 분량 | 판정
set -eu

START=${1:?시작 행 번호가 필요합니다}
END=${2:?끝 행 번호가 필요합니다}
ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"

printf '%-6s %-7s %-34s %-9s %-9s %s\n' LINE BYTES SYMBOL ARCH_MD SUMMARY VERDICT

awk -v s="$START" -v e="$END" 'NR>=s && NR<=e {print NR"\t"$0}' AGENTS.md |
while IFS="$(printf '\t')" read -r ln body; do
  [ -z "$body" ] && continue

  bytes=$(printf '%s' "$body" | LC_ALL=C wc -c | tr -d ' ')

  # 그 줄이 다루는 대상: 첫 번째 [Xxx.cs] 링크의 클래스명
  sym=$(printf '%s' "$body" | grep -oE '\[[A-Za-z][A-Za-z0-9]+\.cs\]' | head -1 |
        tr -d '[].' | sed 's/cs$//')
  [ -z "$sym" ] && { printf '%-6s %-7s %-34s %-9s %-9s %s\n' "$ln" "$bytes" "-" "-" "-" "산문(수동판정)"; continue; }

  arch=$(LC_ALL=C grep -h "$sym" docs/architecture.md 2>/dev/null | LC_ALL=C wc -c | tr -d ' ')

  f=$(find src -name "$sym.cs" | head -1)
  if [ -n "$f" ]; then
    doc=$(awk '/\/\/\//{c+=length($0)} END{print c+0}' "$f")
  else
    doc=0
  fi

  if [ "$arch" -ge "$bytes" ]; then
    verdict="중복:architecture.md"
  elif [ "$doc" -ge "$bytes" ]; then
    verdict="중복:$sym.cs <summary>"
  else
    verdict="근거없음(이동필요)"
  fi

  printf '%-6s %-7s %-34s %-9s %-9s %s\n' "$ln" "$bytes" "$sym" "$arch" "$doc" "$verdict"
done
```

- [ ] **Step 3: 실행 권한 부여 후 §2 카탈로그 구간에 돌려 본다**

```bash
chmod +x scripts/doc-audit.sh
./scripts/doc-audit.sh 20 145 | tee /tmp/audit-catalog.txt
```

기대: 대부분 `중복:architecture.md` 또는 `중복:<클래스>.cs <summary>`. `근거없음`이 나오는 줄은 설계 문서가 예고한 소형 모델 클래스 8개(`DependencyInfo`, `ColumnInfo`, `TableIndexInfo`, `AiResult`, `IMultiProgressScope`, `NullProgressScope`, `SettlementPolicyService`, `ValidationUiProxy`) 언저리여야 한다. 그 밖의 클래스가 `근거없음`으로 나오면 설계의 전제가 틀린 것이므로 **멈추고 보고한다.**

- [ ] **Step 4: 대장 초안 생성**

```bash
{
  echo "# 삭제 대장 — AGENTS.md 재구조화"
  echo
  echo "각 줄의 삭제 근거. \`중복\`은 동등 이상의 서술이 이미 그곳에 있다는 뜻이다."
  echo "\`근거없음\`은 삭제하지 않고 이동한다."
  echo
  echo '## Phase 1 — §2 클래스 카탈로그 (AGENTS.md L16–L145)'
  echo
  echo '```'
  cat /tmp/audit-catalog.txt
  echo '```'
} > docs/superpowers/plans/2026-08-13-deletion-ledger.md
```

- [ ] **Step 5: 커밋**

```bash
git add scripts/doc-audit.sh docs/superpowers/plans/2026-08-13-deletion-ledger.md
git commit -m "docs: add the audit script that backs every deletion with a location"
```

---

### Task 2: Phase 1 — 카탈로그 삭제와 라우팅 표 신설

**Files:**
- Modify: `AGENTS.md:16-145` (삭제 후 라우팅 표로 교체)
- Modify: `docs/architecture.md` §2.2 표 (소형 클래스 8개 편입)
- Modify: `docs/superpowers/plans/2026-08-13-deletion-ledger.md`

**Interfaces:**
- Consumes: Task 1의 `/tmp/audit-catalog.txt`
- Produces: AGENTS.md의 `## 🗺 라우팅 표` 섹션. Task 3~6이 규칙에서 근거를 걷어낼 때 이 표를 가리킨다.

- [ ] **Step 1: 근거 없는 소형 클래스를 architecture.md §2.2 표에 편입**

`docs/architecture.md`의 `### 2.2. 핵심 모듈 및 클래스 목록` 표에서 각 클래스가 속한 프로젝트 그룹의 마지막 행 뒤에 추가한다. 기존 열 구성(`| 프로젝트 | 모듈 | 역할 |`)과 링크 형식(`[Name](../src/...)`)을 그대로 따른다. AGENTS.md의 원문 설명을 그대로 옮긴다:

```markdown
| | [DependencyInfo](../src/ReSet.Core/Models/DependencyInfo.cs) | 재귀적으로 수집된 DB 개체(테이블, 뷰, 다른 SP 등) 의존성을 표현하는 모델. |
| | [ColumnInfo](../src/ReSet.Core/Models/ColumnInfo.cs) | 컬럼명, 데이터타입, PK/FK 정보, 한글 설명, 설명 누락 유무(`IsDescriptionMissing`) 및 Identity/DefaultValue 정보를 수집하는 모델. |
| | [TableIndexInfo](../src/ReSet.Core/Models/TableIndexInfo.cs) | 테이블 인덱스 메타데이터(인덱스명, 타입, Unique, PK 여부, 구성 컬럼)를 관리하는 모델. |
| | [AiResult](../src/ReSet.Core/Models/AiResult.cs) | AI 응답 내용(Content) 및 추론 텍스트(ThinkingText), 요청된 시스템/사용자 프롬프트 콘텍스트를 모아 관리하는 데이터 모델. |
| | [IMultiProgressScope](../src/ReSet.Core/Services/IMultiProgressScope.cs) | 멀티태스크 진행률 상황 보고를 위한 추상 인터페이스. |
| | [NullProgressScope](../src/ReSet.Core/Services/NullProgressScope.cs) | 유닛 테스트 및 무인 모드 등에서 UI 미출력을 보장하고 NullReferenceException을 막는 방어적 널 객체 구현체. |
| | [SettlementPolicyService](../src/ReSet.Core/Services/SettlementPolicyService.cs) | DDL 상수 분석 및 DB 마스터 데이터 프로파일링을 활용한 통합 정산 정책서 생성 서비스. |
| | [ValidationUiProxy](../src/ReSet.Cli/ValidationUiProxy.cs) | 검증기(Validator)의 L1/L2/L3 요약 보고서를 Spectre.Console로 TUI에 렌더링하는 브릿지 구현체. |
```

Task 1 Step 3이 위 8개와 다른 목록을 냈다면 실제 결과를 따르고, 그 차이를 대장에 적는다.

- [ ] **Step 2: 편입이 실제로 됐는지 확인**

```bash
for c in DependencyInfo ColumnInfo TableIndexInfo AiResult IMultiProgressScope NullProgressScope SettlementPolicyService ValidationUiProxy; do
  printf '%-26s %s\n' "$c" "$(grep -c "$c" docs/architecture.md)"
done
```

기대: 모두 1 이상.

- [ ] **Step 3: AGENTS.md L16–L145를 라우팅 표로 교체**

`## 📂 프로젝트 구조 및 주요 파일 바로가기 (Key Code References)` 헤딩부터 `### 5. 단위 테스트 프로젝트` 마지막 항목(L145)까지를 통째로 지우고 아래로 대체한다. `---` 구분선(L147)은 남긴다.

```markdown
## 🗺 어디를 만지면 무엇을 먼저 읽는가 (Routing)

클래스 목록은 이 문서가 갖고 있지 않습니다. 클래스별 역할은
[docs/architecture.md §2.2](./docs/architecture.md), 설계 근거는 각 클래스의 `<summary>`
주석과 [§4 핵심 아키텍처 메커니즘](./docs/architecture.md)에 있습니다. 여기에는 **무엇을
먼저 읽어야 하는가**만 둡니다 — 클래스를 여기 다시 나열하면 카탈로그가 이름만 바꿔
부활하고, 그것이 이 문서를 108KB로 만들었습니다.

| 만지는 대상 | 먼저 읽을 것 |
| :--- | :--- |
| 검증 파이프라인 — 재시도·구제 채택·목차 재설계 | `architecture.md §4.4` + 범주 4 |
| 프롬프트 캐시·중단점·토큰 비용 | `architecture.md §4.13` + 범주 2 |
| 지시서 번들·회차 단위 코드 생성 | `architecture.md §4.11` + 범주 6 |
| 단계 하한 검사·목차 보강 재료 | `architecture.md §4.12` + 범주 4 |
| 정적 분석·SQL 객체 타입 판정 | `architecture.md §4.3` + `TypeClassificationPolicyTests` |
| 재귀 의존성 수집·Soft Fail | `architecture.md §4.1` + 범주 2 |
| AI 공급자 추가·CLI 제공자 | `architecture.md §4.5` + 범주 4 |
| 정합성 검증기(Validator) | `architecture.md §4.6` + 범주 5 |
| 취소 처리 | 범주 2 + `CancellationPolicyTests` |
| 프롬프트 문구·환각 차단 규칙 | `architecture.md §4.9` + 범주 7 |
```

- [ ] **Step 4: 링크 유효성 검증**

```bash
grep -ho '](\./[^)]*)' AGENTS.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "$p" ] || echo "BROKEN: $p"; done
grep -o '](\.\./src/[^)]*)' docs/architecture.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "docs/$p" ] || echo "BROKEN architecture.md: $p"; done
```

기대: 출력 없음.

- [ ] **Step 5: 크기 확인**

```bash
wc -c AGENTS.md docs/architecture.md
```

기대: AGENTS.md가 약 54KB 줄어 **50~57KB** 범위. 이 범위를 크게 벗어나면 삭제 범위가 잘못된 것이므로 멈추고 확인한다.

- [ ] **Step 6: 대장에 Phase 1 결과 기록 후 커밋**

대장 파일에 실제 삭제 범위(`AGENTS.md L16–L145`), 교체 내용, 소형 8개의 이동 위치를 적는다.

```bash
git add AGENTS.md docs/architecture.md docs/superpowers/plans/2026-08-13-deletion-ledger.md
git commit -m "docs: replace the AGENTS.md class catalog with a routing table

The catalog duplicated architecture.md (53 of 72 entries) and the code's own
<summary> comments, which carry the same rationale several times more thickly.
Eight small model classes had no other home and moved into architecture.md 2.2."
```

---

### Task 3: Phase 2a — 범주 2 (예외 처리 및 안정성, 10.8KB)

**Files:**
- Modify: `AGENTS.md` 범주 2 구간
- Modify: `docs/superpowers/plans/2026-08-13-deletion-ledger.md`

**Interfaces:**
- Consumes: Task 2가 만든 라우팅 표(근거를 걷어낸 자리에서 가리킬 대상)

- [ ] **Step 1: 구간 감사**

Task 2가 130줄을 지웠으므로 행 번호는 이미 이동했다. 헤딩에서 구간을 계산한다.

```bash
S=$(grep -n '^### .* 범주 2\.' AGENTS.md | cut -d: -f1)
E=$(grep -n '^### .* 범주 3\.' AGENTS.md | cut -d: -f1)
echo "범주 2 = L$S..L$((E-1))"
./scripts/doc-audit.sh "$S" "$((E-1))" | tee /tmp/audit-cat2.txt
```

- [ ] **Step 2: 항목별 판정 적용**

각 불릿에 Global Constraints의 판정 질문을 적용한다. 이 범주의 확정된 판정:

| 대상 | 판정 | 처리 |
|---|---|---|
| 취소 규칙 (`OperationCanceledException` 필터) | `CancellationPolicyTests`가 잡음 | 규칙 문장 + 테스트 링크만 남김 |
| SQL 타입 판정 (L168, 2,307B) | `TypeClassificationPolicyTests`가 잡음 | 규칙 + 사고 1줄 + 링크. 스캐너 한계 서술 전량 삭제 |
| 모델별 전송 규격 (L172, 1,744B) | `architecture.md §4.13`이 보유 | 규칙만 남기고 캐시 가격 논증 삭제 |
| Soft Fail 정책 (DB/Exporter/캐시/재귀) | 사람의 판단만이 잡음 | **원문 유지** |
| Ollama 온도·추론 제어 | 사람의 판단만이 잡음 | **원문 유지** |

L168의 구체적 변환 (2,307B → 약 280B):

```markdown
    *   **SQL 객체 타입 판정은 반드시 분류기를 거칠 것**: `Contains("TABLE")` 같은 부분 문자열 판정은 `SQL_TABLE_VALUED_FUNCTION`을 테이블로 오분류합니다. 실제로 정산일을 계산하는 `UIF_SettleYMD`의 DDL이 그렇게 누락됐습니다. [SqlObjectTypeClassifier](./src/ReSet.Core/Services/SqlObjectTypeClassifier.cs)를 쓰고 사본을 만들지 마십시오. (`TypeClassificationPolicyTests`가 자동 검사하며, 스캐너의 알려진 한계는 `TypeClassificationPolicyScanner.cs` 상단 주석에 있습니다)
```

삭제되는 것: 스캐너가 잡는 구문 형태의 열거, 놓치는 형태의 열거, `TryParseCodeObjectType`/`NormalizeCodeObjectDdlFolder`의 `switch` 테이블 서술. 마지막 항목은 근거가 어디에도 없으므로 **삭제하지 말고** `architecture.md §4.3`에 한 문단으로 옮긴다.

- [ ] **Step 3: 라인 예산 위반 확인**

```bash
LC_ALL=C awk 'length($0)>600 {print NR": "length($0)" "substr($0,1,50)}' AGENTS.md
```

문서 전체를 검사한다(구간을 잘라 재면 경계를 잘못 잡았을 때 위반이 숨는다). 범주 2에 속한 줄이 남아 있으면 그 줄은 아직 규칙이 아니라 서술을 담고 있다는 뜻이므로 다시 판정한다. 다른 범주의 위반은 뒤 Task에서 처리하므로 여기서는 무시한다.

- [ ] **Step 4: 대장 기록 후 커밋**

지운 줄마다 `(원문 위치 / 사유 / 근거 위치)`를 대장에 적는다. `근거없음`이었던 것은 옮긴 위치를 적는다.

```bash
git add AGENTS.md docs/architecture.md docs/superpowers/plans/2026-08-13-deletion-ledger.md
git commit -m "docs: keep only the judgment rules in the stability category"
```

---

### Task 4: Phase 2b — 범주 4 (검증 오케스트레이션, 15.8KB)

가장 큰 구간이고 가장 조심할 구간이다. 여기에는 기계가 잡을 수 없는 프롬프트 설계 제약이 섞여 있다.

**Files:**
- Modify: `AGENTS.md` 범주 4 구간
- Modify: `docs/superpowers/plans/2026-08-13-deletion-ledger.md`

- [ ] **Step 1: 구간 감사**

```bash
S=$(grep -n '^### .* 범주 4\.' AGENTS.md | cut -d: -f1)
E=$(grep -n '^### .* 범주 5\.' AGENTS.md | cut -d: -f1)
./scripts/doc-audit.sh "$S" "$((E-1))" | tee /tmp/audit-cat4.txt
```

- [ ] **Step 2: 판정 적용**

| 대상 | 판정 | 처리 |
|---|---|---|
| L213 "코드가 강제하는 제약은 프롬프트에도 실으십시오" | 기계가 잡을 수 없음 | **한 글자도 줄이지 않는다** |
| L217 Anti-Shortcut 프롬프트 제약 | 기계가 잡을 수 없음 | **원문 유지** |
| L216 하이브리드 영문 프롬프트 구조 | 기계가 잡을 수 없음 | **원문 유지** |
| L218 캐시 워밍 순서 | `RunConsolidatedPipeline_WarmsCacheBeforeFanningOut`가 잡음 | 한 줄로 축소 |
| L219 예외 재시도 지연 | `..._DelaysRetryWithJitter` / `..._RetriesWithoutDelay`가 잡음 | 한 줄로 축소 |
| L207 검증 종료 상태 정직성 | 사람의 판단 + `VerificationOutcome` 주석 | 규칙만 남김 |
| L212 CLI 제공자 원칙 | `architecture.md §4.5`가 더 두껍게 보유 | 규칙만 남김 |
| L202~205 L2 Actor-Critic 흐름 | `architecture.md §4.4`가 보유 | 규칙만 남김 |

L218의 구체적 변환 (약 1,000B → 약 230B):

```markdown
    *   **`GenerateBySplitAsync`의 첫 단계 단독 실행을 제거하지 마십시오**: 프롬프트 접두사 캐시를 채우는 워밍이며, 지웠을 때의 증상은 산출물은 그대로인데 입력 토큰 비용만 조용히 오르는 것이라 코드만 봐서는 알 수 없습니다. (`RunConsolidatedPipeline_WarmsCacheBeforeFanningOut`가 검사, 근거는 `architecture.md §4.13`)
```

L219의 구체적 변환:

```markdown
    *   **단계 생성의 예외 재시도 지연을 보존하십시오**: 지연은 예외 실패에만 붙고 하한 미달 재시도에는 붙지 않습니다 — 429 하나가 여러 단계를 같은 창에서 때리는 것을 흩트리기 위함입니다. (`RunConsolidatedPipeline_WhenStepGenerationThrows_DelaysRetryWithJitter`와 `..._WhenStepMissesFloor_RetriesWithoutDelay`가 검사)
```

- [ ] **Step 3: 원문 유지 항목이 실제로 살아 있는지 확인**

```bash
grep -c "코드가 강제하는 제약은 프롬프트에도" AGENTS.md   # 1이어야 함
grep -c "Anti-Shortcut" AGENTS.md                        # 1 이상이어야 함
grep -c "하이브리드 영문" AGENTS.md                        # 1이어야 함
```

0이 나오면 지우지 말아야 할 것을 지운 것이다. 되돌린다.

- [ ] **Step 4: 라인 예산 확인**

```bash
LC_ALL=C awk 'length($0)>600 {print NR": "length($0)" "substr($0,1,50)}' AGENTS.md
```

"코드가 강제하는 제약은 프롬프트에도"(구 L213)는 원문 유지 대상이라 600을 넘는다. 넘는 줄이 원문 유지 대상뿐인지 확인하고, 넘는다면 **규칙을 훼손하지 않는 선에서 여러 불릿으로 나눈다** — 내용 삭제가 아니라 줄바꿈이다. 세 사례(`ErrorCodes` 빈 배열, `MaxSteps`, `LegacyProcedures`)를 각각 한 불릿으로 쪼개면 규칙은 그대로 남고 각 줄은 예산 안에 든다.

- [ ] **Step 5: 대장 기록 후 커밋**

```bash
git add AGENTS.md docs/superpowers/plans/2026-08-13-deletion-ledger.md
git commit -m "docs: strip the verification category down to what only judgment catches"
```

---

### Task 5: Phase 2c — 범주 6·7 (외부 에이전트 7.7KB, 정화 7.0KB)

**Files:**
- Modify: `AGENTS.md` 범주 6·7 구간
- Modify: `docs/superpowers/plans/2026-08-13-deletion-ledger.md`

- [ ] **Step 1: 구간 감사**

```bash
S=$(grep -n '^### .* 범주 6\.' AGENTS.md | cut -d: -f1)
E=$(grep -n '^### .* 범주 8\.' AGENTS.md | cut -d: -f1)
./scripts/doc-audit.sh "$S" "$((E-1))" | tee /tmp/audit-cat67.txt
```

- [ ] **Step 2: 판정 적용**

| 대상 | 판정 | 처리 |
|---|---|---|
| L250 통합 배치 5대 제약 (NOLOCK/INSERT-only/Chunk Key/멱등성/XACT_ABORT) | 기계가 잡을 수 없는 프롬프트 설계 제약 | **원문 유지** |
| L246 컬럼 매핑 표 축약 금지 | 기계가 잡을 수 없음 | **원문 유지** |
| L248 DDL 기반 제약 조건 작성 | 기계가 잡을 수 없음 | **원문 유지** |
| L251 복합 필터의 정확한 해석 | 기계가 잡을 수 없음 | **원문 유지** |
| L247 UPDATE 매핑표 | `MechanicalValidator`가 대조 + `architecture.md`에 근거 | 규칙만 남김 |
| L249 의존 스키마 덤프 필터링 (1,337B) | `architecture.md §4.3`이 보유 | 규칙 + 사고 1줄 |
| L228~239 번들 분할·회차 실행 | `architecture.md §4.11`이 보유 | 규칙만 남김 |
| L252 Mermaid 생성 규칙 | `MechanicalValidator`가 정화 + 프롬프트 제약 | 규칙 유지, 정화기 동작 서술은 삭제 |

**주의:** 범주 7은 원문 유지 비율이 가장 높다. 여기 있는 것 대부분이 "AI에게 무엇을 시킬지"에 관한 규칙이고, 그건 어떤 테스트도 잡지 못한다. 이 범주에서 큰 감축이 나온다면 지우지 말아야 할 것을 지웠다는 신호다.

- [ ] **Step 3: 원문 유지 항목 확인**

```bash
grep -c "NOLOCK" AGENTS.md          # 1 이상
grep -c "외 다수" AGENTS.md          # 1 (컬럼 축약 금지 규칙)
grep -c "NOT IN" AGENTS.md          # 1 (복합 필터 해석)
```

- [ ] **Step 4: 대장 기록 후 커밋**

```bash
git add AGENTS.md docs/superpowers/plans/2026-08-13-deletion-ledger.md
git commit -m "docs: trim the codegen and cleansing categories to their rules"
```

---

### Task 6: Phase 2d — 범주 1·3·5·8 (합계 5.9KB)

작은 범주 넷을 한 번에 처리한다. 범주 1(보안)과 8(워크트리)은 이미 짧고 전부 판단 규칙이라 거의 손대지 않는다.

**Files:**
- Modify: `AGENTS.md` 범주 1·3·5·8 구간
- Modify: `docs/superpowers/plans/2026-08-13-deletion-ledger.md`

- [ ] **Step 1: 판정 적용**

| 범주 | 처리 |
|---|---|
| 1 보안 (939B) | **전량 유지.** `appsettings.local.json`의 provider 덮어쓰기 사고는 기계가 잡지 못한다 |
| 3 UI/UX (3,282B) | `Markup.Escape()`·`ShowChoices(false)` 등은 판단 규칙이라 유지. TUI 진행도 넘버링 형식 서술 등 구현 세부는 `architecture.md §5`로 |
| 5 런타임 격리 (1,049B) | **전량 유지.** Rollback·타임아웃은 체크리스트가 묻는 항목이다 |
| 8 워크트리 (704B) | **전량 유지** |

- [ ] **Step 2: 전체 라인 예산 확인**

```bash
LC_ALL=C awk 'length($0)>600 {print NR": "length($0)}' AGENTS.md
```

여기 남는 줄이 Task 8의 기준선을 정한다. 각 줄이 "원문 유지 대상"으로 대장에 근거가 적혀 있는지 확인한다. 근거 없이 600을 넘는 줄은 없어야 한다.

- [ ] **Step 3: 최종 크기 측정**

```bash
wc -c AGENTS.md
LC_ALL=C awk 'length($0)>600' AGENTS.md | wc -l
```

기대: 25~35KB. 범위를 벗어나면 판정이 한쪽으로 치우친 것이므로 대장을 다시 본다.

- [ ] **Step 4: 커밋**

```bash
git add AGENTS.md docs/architecture.md docs/superpowers/plans/2026-08-13-deletion-ledger.md
git commit -m "docs: finish the category pass over AGENTS.md"
```

---

### Task 7: 문서 예산 측정기와 단위 테스트

측정 로직은 실제 트리와 분리해 순수 함수로 만든다. `CancellationPolicyScanner`(순수) + `CancellationPolicyTests`(기준선) 분리와 같은 구조다.

**Files:**
- Create: `tests/ReSet.Core.Tests/DocumentationBudget.cs`
- Create: `tests/ReSet.Core.Tests/DocumentationBudgetTests.cs`

**Interfaces:**
- Produces: `DocumentationBudget.MeasureBytes(string) -> int`, `DocumentationBudget.FindOversizedLines(string text, int maxLineBytes) -> IReadOnlyList<OversizedLine>`, `record OversizedLine(int Line, int Bytes, string Excerpt)`. Task 8이 이것을 쓴다.

- [ ] **Step 1: 실패하는 테스트를 먼저 쓴다**

`tests/ReSet.Core.Tests/DocumentationBudgetTests.cs`:

```csharp
using System.Text;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class DocumentationBudgetTests
{
    [Fact]
    public void MeasureBytes_CountsUtf8BytesNotCharacters()
    {
        // 예산의 단위는 바이트다. 문자 수로 재면 한글 문서의 실제 컨텍스트
        // 비용을 3분의 1로 과소평가한다.
        Assert.Equal(2, DocumentationBudget.MeasureBytes("ab"));
        Assert.Equal(6, DocumentationBudget.MeasureBytes("한글"));
    }

    [Fact]
    public void FindOversizedLines_ReportsOnlyTheLinesOverBudget()
    {
        var text = "짧은 줄\n" + new string('x', 700) + "\n짧은 줄";

        var found = DocumentationBudget.FindOversizedLines(text, 600);

        var only = Assert.Single(found);
        Assert.Equal(2, only.Line);
        Assert.Equal(700, only.Bytes);
    }

    [Fact]
    public void FindOversizedLines_HandlesCrLfWithoutCountingTheCarriageReturn()
    {
        var text = "a\r\n" + new string('x', 601);

        var found = DocumentationBudget.FindOversizedLines(text, 600);

        var only = Assert.Single(found);
        Assert.Equal(2, only.Line);
        Assert.Equal(601, only.Bytes);
    }

    [Fact]
    public void FindOversizedLines_DoesNotSplitASurrogatePairInTheExcerpt()
    {
        // AGENTS.md의 헤딩에는 이모지가 흔하다. 발췌를 문자 수로 자르면 서러게이트
        // 쌍 가운데가 잘려, 실패 메시지에 깨진 문자가 실린다.
        var line = new string('a', 59) + "\U0001F6A8" + new string('b', 700);

        var found = DocumentationBudget.FindOversizedLines(line, 600);

        var excerpt = Assert.Single(found).Excerpt;
        Assert.Equal(excerpt, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(excerpt)));
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --filter DocumentationBudgetTests
```

기대: 컴파일 실패 — `DocumentationBudget`을 찾을 수 없음.

- [ ] **Step 3: 최소 구현**

`tests/ReSet.Core.Tests/DocumentationBudget.cs`:

```csharp
using System.Collections.Generic;
using System.Text;

namespace ReSet.Core.Tests;

/// <summary>라인 예산을 넘긴 줄 하나.</summary>
public sealed record OversizedLine(int Line, int Bytes, string Excerpt);

/// <summary>
/// 자동 로드되는 문서의 크기를 잰다.
///
/// 총량과 라인 길이를 따로 재는 이유: 실제 병리는 총량이 아니라 4,162바이트짜리
/// "목록 항목" 하나였다. 총량 상한은 여러 항목에 분산시켜 우회할 수 있지만, 라인
/// 상한은 그 병리 자체를 겨냥하고 문서가 정당하게 자라도 계속 참이다.
/// </summary>
public static class DocumentationBudget
{
    public static int MeasureBytes(string text) => Encoding.UTF8.GetByteCount(text);

    public static IReadOnlyList<OversizedLine> FindOversizedLines(string text, int maxLineBytes)
    {
        var result = new List<OversizedLine>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var bytes = Encoding.UTF8.GetByteCount(lines[index]);
            if (bytes <= maxLineBytes) continue;

            result.Add(new OversizedLine(index + 1, bytes, Excerpt(lines[index])));
        }

        return result;
    }

    private static string Excerpt(string line)
    {
        const int maxChars = 60;
        if (line.Length <= maxChars) return line;

        var cut = maxChars;
        // 서러게이트 쌍을 가르지 않는다. 문서 헤딩에 이모지가 흔하다.
        if (char.IsHighSurrogate(line[cut - 1])) cut--;

        return line[..cut] + "…";
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

```bash
dotnet test --filter DocumentationBudgetTests
```

기대: 4개 통과.

- [ ] **Step 5: 커밋**

```bash
git add tests/ReSet.Core.Tests/DocumentationBudget.cs tests/ReSet.Core.Tests/DocumentationBudgetTests.cs
git commit -m "test: measure documentation size in bytes and per line"
```

---

### Task 8: 기준선 고정과 실제 트리 게이트

**Files:**
- Create: `tests/ReSet.Core.Tests/documentation-budget-baseline.txt`
- Modify: `tests/ReSet.Core.Tests/DocumentationBudgetTests.cs`

**Interfaces:**
- Consumes: Task 7의 `DocumentationBudget`, 기존 `RepoPaths.FindRepoRoot()` (`CancellationPolicyScanner.cs:240`)

- [ ] **Step 1: 기준선 파일을 실측값에서 생성한다**

상한은 손으로 적지 않는다. Task 6이 끝난 실제 크기에 15%를 더해 계산한다.

```bash
BUDGET=$(( $(LC_ALL=C wc -c < AGENTS.md) * 115 / 100 ))
echo "실측 $(LC_ALL=C wc -c < AGENTS.md) → 상한 $BUDGET"
```

- [ ] **Step 2: 기준선 파일 작성**

```bash
cat > tests/ReSet.Core.Tests/documentation-budget-baseline.txt <<EOF
# 매 세션 컨텍스트에 자동 로드되는 문서의 크기 상한(UTF-8 바이트).
#
# 이 게이트는 단방향이다. 상한 초과만 실패하고 밑으로는 자유다.
# cancellation-policy-baseline.txt의 양방향 잠금을 흉내내지 말 것 — 오타 한 글자만
# 고쳐도 실패하는 검사가 되고, 그러면 다음 사람이 이 파일을 무시하도록 길들여진다.
#
# 상한을 올리려면 이 줄을 사람이 고쳐야 한다. 그것이 이 파일의 요점이다.
# 올리기 전에 물어야 할 것: 그 내용을 잡는 것이 정말 사람의 판단뿐인가?
# 테스트가 잡으면 한 줄로 줄이고, 한 클래스 안에서 닫히면 그 클래스 <summary>로,
# 여러 파일에 걸치면 docs/architecture.md로 보낸다.
#
# architecture.md와 README.md는 자동 로드되지 않으므로 예산이 없다.

AGENTS.md = $BUDGET
EOF
cat tests/ReSet.Core.Tests/documentation-budget-baseline.txt
```

- [ ] **Step 3: 실제 트리 게이트 테스트 추가**

`DocumentationBudgetTests.cs`에 아래 멤버를 추가한다(기존 4개 테스트는 그대로 둔다):

```csharp
    // 라인 예산. 실제 병리는 4,162바이트짜리 "목록 항목"이었다.
    private const int MaxLineBytes = 600;

    private const string Routing =
        "이 문장을 어긴 코드가 나왔을 때 무엇이 그것을 잡습니까?\n" +
        "  테스트가 잡는다        → 규칙 한 줄 + 테스트 이름만 남기십시오\n" +
        "  그 파일 여는 사람만    → 해당 클래스의 <summary>로 옮기십시오\n" +
        "  여러 파일을 함께 봐야  → docs/architecture.md §4.x로 옮기십시오\n" +
        "  사람의 판단만이 잡는다 → AGENTS.md에 남을 자격이 있습니다\n";

    [Fact]
    public void NoAutoLoadedDocumentExceedsItsByteBudget()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var failures = new StringBuilder();

        foreach (var (relativePath, budget) in ReadBaseline(repoRoot))
        {
            var actual = DocumentationBudget.MeasureBytes(
                File.ReadAllText(Path.Combine(repoRoot, relativePath)));

            if (actual <= budget) continue;

            failures.AppendLine($"{relativePath}: 상한 {budget:N0} 바이트, 실제 {actual:N0} 바이트 ({actual - budget:N0} 초과)");
        }

        Assert.True(
            failures.Length == 0,
            "자동 로드되는 문서가 크기 예산을 넘었습니다.\n\n" + failures + "\n" + Routing);
    }

    [Fact]
    public void NoAutoLoadedDocumentHasAnOversizedLine()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var failures = new StringBuilder();

        foreach (var (relativePath, _) in ReadBaseline(repoRoot))
        {
            var oversized = DocumentationBudget.FindOversizedLines(
                File.ReadAllText(Path.Combine(repoRoot, relativePath)), MaxLineBytes);

            foreach (var line in oversized)
            {
                failures.AppendLine($"{relativePath}:{line.Line} — {line.Bytes:N0} 바이트 (상한 {MaxLineBytes})");
                failures.AppendLine($"  {line.Excerpt}");
            }
        }

        Assert.True(
            failures.Length == 0,
            $"목록 항목 하나가 {MaxLineBytes} 바이트를 넘었습니다. 항목이 아니라 문단입니다.\n\n"
            + failures + "\n" + Routing);
    }

    private static IEnumerable<(string RelativePath, int Budget)> ReadBaseline(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "tests", "ReSet.Core.Tests", "documentation-budget-baseline.txt");

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.LastIndexOf('=');
            Assert.True(separator > 0, $"기준선 파일의 형식이 잘못되었습니다: {raw}");

            yield return (line[..separator].Trim(), int.Parse(line[(separator + 1)..].Trim()));
        }
    }
```

파일 상단 `using`에 다음을 추가한다:

```csharp
using System.Collections.Generic;
using System.IO;
```

- [ ] **Step 4: 통과 확인**

```bash
dotnet test --filter DocumentationBudget
```

기대: 6개 전부 통과. `NoAutoLoadedDocumentHasAnOversizedLine`이 실패하면 Task 3~6이 놓친 줄이 있다는 뜻이므로, 그 줄을 판정에 따라 처리한 뒤 다시 돌린다.

- [ ] **Step 5: 게이트가 실제로 무는지 확인한다**

```bash
awk 'BEGIN { s=""; for (i = 0; i < 700; i++) s = s "x"; print s }' >> AGENTS.md
dotnet test --filter NoAutoLoadedDocumentHasAnOversizedLine   # 실패해야 한다
git checkout AGENTS.md
dotnet test --filter DocumentationBudget                      # 다시 통과해야 한다
```

통과만 확인하고 끝내면 아무것도 검사하지 않는 테스트를 넣고도 모른다.

- [ ] **Step 6: 체크리스트에 항목 추가 후 커밋**

`AGENTS.md`의 `## ✅ 에이전트 작업 완료 체크리스트`에 추가:

```markdown
- [ ] AGENTS.md에 600바이트를 넘는 줄을 만들지 않았는가? 그런 줄은 규칙이 아니라 문단이다. (`DocumentationBudgetTests`가 자동 검사하며, 상한은 `tests/ReSet.Core.Tests/documentation-budget-baseline.txt`에 있다)
```

```bash
git add tests/ReSet.Core.Tests/ AGENTS.md
git commit -m "test: cap the size of the documents that load into every session"
```

---

### Task 9: reset-doc-sync 스킬 수정

문서를 정리해도 이 스킬이 그대로면 다시 커진다. 108KB를 만든 것은 이 스킬의 append 전용 지시문이다.

**Files:**
- Modify: `.claude/skills/reset-doc-sync/SKILL.md`

- [ ] **Step 1: 대상 문서 표의 AGENTS.md 행 교체 (L21)**

기존:
```markdown
| `AGENTS.md` | AI 에이전트용 개발 규칙, 파일 바로가기 링크, 작업 완료 체크리스트 | 새 규칙 추가, 새 클래스 등장, 검증 항목 변경 |
```

교체:
```markdown
| `AGENTS.md` | AI 에이전트용 **행동 규칙**, 라우팅 표, 작업 완료 체크리스트 | 새 **규칙** 추가, 검증 항목 변경. **새 클래스가 생겼다는 사실만으로는 트리거가 아니다** |
```

- [ ] **Step 2: 낡은 크기 서술 수정 (L29)**

기존:
```markdown
세 문서는 합계 1,200줄이 넘는다. **전문을 읽지 말고 목차부터 확보한 뒤 필요한 섹션만 부분 읽기**를 한다.
```

교체:
```markdown
세 문서는 합계 200KB가 넘는다(README 57KB, AGENTS 약 30KB, architecture 137KB).
**전문을 읽지 말고 목차부터 확보한 뒤 필요한 섹션만 부분 읽기**를 한다.
```

- [ ] **Step 3: 3단계에 라우팅 판정을 선행 단계로 삽입**

`### 3단계: 업데이트 안(案) 작성`의 첫 문단(`변경이 필요한 문서에 대해서만 초안을 작성한다...`) 바로 뒤에 삽입:

```markdown
**3-0. 거처 판정 — 쓰기 전에 먼저 답한다.**

새 정보를 어느 문서에 쓸지는 취향이 아니다. 질문 하나로 결정된다:
**"이 문장을 어긴 코드가 나왔을 때, 무엇이 그것을 잡는가?"**

| 답 | 거처 | AGENTS.md에 남는 것 |
|---|---|---|
| 테스트가 잡는다 | 테스트 게이트 | 규칙 한 줄 + 테스트 이름 |
| 그 파일을 여는 사람만 잡는다 | 해당 클래스 `<summary>` | 없음 |
| 여러 파일을 함께 봐야 안다 | `docs/architecture.md §4.x` | 라우팅 표 한 줄 |
| **사람/에이전트의 판단만이 잡는다** | **`AGENTS.md` 본문** | **규칙 전문** |

마지막 칸일 때만 AGENTS.md에 쓴다. AGENTS.md는 매 세션 컨텍스트에 통째로 로드되므로,
여기 쓰는 모든 바이트는 그 세션의 모든 작업에서 값을 치른다.

이 판정을 건너뛴 결과가 108KB(약 51,000 토큰)였고, 그 중 94%는 특정 클래스를 건드릴
때만 필요한 설계 근거였다. 실측에서 §2 카탈로그 72개 항목 중 53개는 architecture.md가
이미 더 두껍게 다루고 있었고, 나머지도 대부분 코드의 `<summary>`에 있었다.
```

- [ ] **Step 4: AGENTS.md 작성 원칙 교체 (L150~154 블록)**

기존:
```markdown
**AGENTS.md 작성 원칙**
- 새 클래스가 생기면 `## 📂 프로젝트 구조 및 주요 파일 바로가기` 해당 프로젝트 하위에 링크 추가 (링크는 레포 루트 기준 `./src/...`)
- 새 개발 규칙은 기존 범주(보안/안정성/UI/파이프라인/생명주기/외부 에이전트/정화/버전관리)에 맞는 곳에 배치
- 기존 규칙의 의미를 훼손하지 않도록 추가·수정에만 집중
- `## ✅ 에이전트 작업 완료 체크리스트`에 새 검증 항목이 필요하면 추가
```

교체:
```markdown
**AGENTS.md 작성 원칙**
- **새 클래스는 `docs/architecture.md` §2.2 표에만 추가한다. AGENTS.md는 손대지 않는다.**
  AGENTS.md에는 클래스 목록이 없다 — 있었고, 그것이 이 문서를 108KB로 만들었다.
- AGENTS.md를 여는 것은 **새 규칙**이 생겼을 때뿐이며, 3-0 판정에서 마지막 칸이 나온
  경우로 한정한다
- 새 규칙은 기존 범주(보안/안정성/UI/파이프라인/생명주기/외부 에이전트/정화/버전관리)에 배치
- **한 항목은 600바이트를 넘지 않는다.** 넘으면 그것은 규칙이 아니라 설명이므로 3-0으로
  돌아가 거처를 다시 정한다. `DocumentationBudgetTests`가 자동 검사한다
- 기존 규칙의 의미를 훼손하지 않도록 추가·수정에만 집중
- `## ✅ 에이전트 작업 완료 체크리스트`에 새 검증 항목이 필요하면 추가
- 근거·사고 경위·실측 수치를 AGENTS.md에 쓰지 않는다. 규칙 문장과 "어겼을 때의 증상"
  한 줄이면 족하고, 나머지는 3-0의 거처로 보낸다
```

- [ ] **Step 5: 4-3 검증에서 죽은 검사를 교체**

기존:
```bash
# 단위 테스트 개수 대조 — AGENTS.md 체크리스트 숫자와 일치해야 함
dotnet test 2>&1 | tail -1
grep -n "개의 단위 테스트" AGENTS.md
```

교체:
```bash
# 테스트 — 체크리스트가 요구하는 것은 개수가 아니라 "실패 0, 건너뜀 0"이다.
# (기대 개수를 문서에 적는 방식은 하루 만에 네 번 낡아서 폐기됐다)
dotnet test 2>&1 | tail -3

# 문서 예산 — 자동 로드되는 문서의 총량과 라인 길이
dotnet test --filter DocumentationBudget 2>&1 | tail -3
```

- [ ] **Step 6: 죽은 검사가 사라졌는지 확인**

```bash
grep -c "개의 단위 테스트" .claude/skills/reset-doc-sync/SKILL.md   # 0이어야 함
grep -c "3-0" .claude/skills/reset-doc-sync/SKILL.md                 # 2 이상
grep -c "600바이트" .claude/skills/reset-doc-sync/SKILL.md            # 1 이상
```

- [ ] **Step 7: 커밋**

```bash
git add .claude/skills/reset-doc-sync/SKILL.md
git commit -m "docs: make the sync skill decide where a fact belongs before writing it

The skill already said to avoid excessive detail and to keep the documents
balanced, and AGENTS.md reached 108KB anyway. The instruction that mattered
was the first line of its AGENTS.md rules: append a link for every new class,
with nothing that ever removes one."
```

---

### Task 10: 최종 검증

**Files:** 없음 (검증만)

- [ ] **Step 1: 링크 유효성**

```bash
grep -ho '](\./[^)]*)' AGENTS.md README.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "$p" ] || echo "BROKEN: $p"; done
grep -o '](\.\./src/[^)]*)' docs/architecture.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "docs/$p" ] || echo "BROKEN architecture.md: $p"; done
```

기대: 출력 없음.

- [ ] **Step 2: 빌드 경고가 정확히 8건**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
```

기대: `8`.

- [ ] **Step 3: 테스트 실패 0, 건너뜀 0**

```bash
dotnet test 2>&1 | tail -3
```

- [ ] **Step 4: `synced-through`가 변하지 않았는지 확인**

```bash
git diff main -- AGENTS.md docs/architecture.md | grep "synced-through"
```

기대: 출력 없음. 출력이 있으면 되돌린다 — 이번 작업은 코드 반영 범위를 넓히지 않았다.

- [ ] **Step 5: 대장이 모든 삭제를 설명하는지 확인**

```bash
git diff main --numstat -- AGENTS.md
wc -l docs/superpowers/plans/2026-08-13-deletion-ledger.md
```

삭제된 줄 수와 대장의 항목 수를 대조한다. 대장에 없는 삭제가 있으면 그 줄을 찾아 근거를 적거나 되돌린다.

- [ ] **Step 6: 최종 수치 보고**

```bash
echo "AGENTS.md: $(wc -c < AGENTS.md) bytes (이전 108,485)"
echo "600바이트 초과 라인: $(LC_ALL=C awk 'length($0)>600' AGENTS.md | wc -l) (이전 54)"
echo "architecture.md: $(wc -c < docs/architecture.md) bytes (이전 137,251)"
```

- [ ] **Step 7: 병합과 워크트리 정리**

```bash
cd /Users/payletter/git-root/ReSet
git merge --no-ff agents-md-restructure
git worktree remove .worktrees/agents-md-restructure
git branch -d agents-md-restructure
```

---

## 자체 검토 결과

**스펙 커버리지:** 설계 1(소관) → Task 2·8 / 설계 2(판정 기준) → Global Constraints + Task 3~6 / 설계 3(새 형태) → Task 2 Step 3 / 설계 4(게이트) → Task 7·8 / 설계 5(스킬) → Task 9 / 이행 절차 → Task 1~10 / 안전장치 1(대장) → Task 1·10 Step 5, 2(링크) → Task 2 Step 4·10 Step 1, 3(빌드) → Task 10 Step 2·3, 4(synced-through) → Task 10 Step 4. 누락 없음.

**미리 아는 위험:** Task 3~6은 판단 작업이라 기계적으로 검증되지 않는다. 각 Task의 "원문 유지 확인" 단계(`grep -c`)가 최소한의 안전망이며, 그것이 잡는 것은 "지우지 말아야 할 것을 지웠다"뿐이고 "지웠어야 할 것을 남겼다"는 잡지 못한다. 후자는 Task 6 Step 3의 크기 범위(25~35KB)와 Task 8의 라인 예산이 간접적으로 잡는다.
