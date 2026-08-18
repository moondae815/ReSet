# 축 B 잔여 구멍 다섯 개 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 축 B가 세운 검사 여섯이 실물 산출물에서 **우회되거나 조용히 꺼지는** 다섯 자리를 막아, Job 재생성 1회를 돌렸을 때 재감사가 "닫혔다"와 "검사가 못 봤다"를 구별할 수 있게 만든다.

**Architecture:** 새 재료나 새 검사 축을 만들지 않는다. 기존 검사 넷의 **인식 범위**를 넓히고(별칭 UPDATE, 펜스 앵커, CATCH 앵커), 계약 하나에 빠진 사실을 채우고(`RunId` 발급·PK), 검사가 아예 없던 축 하나에 문서 수준 검사를 더한다(`BatchRun` 행 생성). 다섯 중 넷이 `MechanicalValidator.cs`를 건드리므로 **순차로 진행한다**.

**Tech Stack:** .NET 10 / C#, xUnit

**Spec:** 이 계획에는 별도 설계 문서가 없다. 근거는 `docs/superpowers/specs/2026-08-18-axis-b-batch-skeleton-design.md`(축 B 설계)와, 그 계획을 실행한 뒤 받은 최종 전체 브랜치 리뷰의 실측 지적이다. 각 태스크의 **근거** 절에 리뷰가 실행으로 재현한 입력과 결과를 그대로 옮겨 적었다.

## Global Constraints

- **지배 계약(축 B 설계 §0):** 재료 하나가 사실을 내고 프롬프트와 L1이 **같은 사실**을 소비한다. 규칙만 있고 물리는 기계 검사가 없으면 그 규칙은 없는 것과 같다.
- **프롬프트를 검사에 맞추지 말 것.** `AiService.cs`의 `ConsolidatedPlanRules`와 Few-Shot 예시는 이 계획에서 **읽기 전용**이다. 검사가 프롬프트의 정당한 산출물을 반려하면 검사를 고친다.
- **단계 검사 결과는 `StepValidationResult.Errors`(문자열)에 실는다.** `ErrorType`은 `ValidationResult.DetailedErrors` 쪽 어휘이고, 통합 문서 검사(`ValidateConsolidated`)만 그쪽을 쓴다.
- **소프트 스킵:** 재료가 없으면 검사를 실행하지 않는다. 없는 것을 결함으로 들지 않는다.
- **레드-그린 필수:** 모든 검사 변경은 되돌렸을 때 실제로 실패해야 한다. `DoesNotContain` 형태 테스트가 통과하는 것은 검사가 도는 증거가 **아니다** — 검사 호출부를 임시로 꺼서 대응 `Contains` 테스트가 실패하는지 확인한다.
- **오탐과 미탐을 함께 검증한다.** 이 파일은 축 B 실행에서 태스크 넷이 총 열 번의 수정 라운드를 돌았고, 매번 한쪽을 닫으면 반대쪽이 열렸다. 좁히거나 넓힐 때마다 **양쪽 시나리오를 각각 테스트로 고정**한다.
- **경고 기준선 9개:** `dotnet build --no-incremental`의 **요약 줄**이 `경고 9개`를 넘으면 안 된다. (`grep -c "warning"`은 18을 낸다 — 이 저장소는 경고를 두 줄씩 출력한다. 요약 줄을 보라.) `Assert.Single(x.Where(...))`를 쓰지 말 것 — `xUnit2031` 경고가 난다. `Assert.Single(x, predicate)`를 쓴다.
- **증분 빌드 주의:** 같은 초 안에 파일을 연속 수정하면 `dotnet test`가 스테일 DLL로 도는 현상이 이 저장소에서 실측됐다. 레드-그린 확인마다 `dotnet build --no-incremental`을 선행한다.
- **한국어 주석:** 이 저장소의 주석·오류 메시지는 한국어다. **왜 그렇게 했는지**를 적는다.
- **기존 자산을 재사용하되 동작을 바꾸지 말 것:** `SkipCommentToken`, `SplitTopLevelSegments`, `ExtractTopLevelClause`, `ExtractBalancedParenGroup`, `StripBracketQuoting`, `BlankCommentsAndStrings`, `BlankSubqueryParenGroups`, `CatchBlockPattern`. 축 B가 열 번의 라운드로 안정화한 코드다.
- **골든 케이스는 계약이다.** `tests/ReSet.Core.Tests/AxisBGoldenCaseTests.cs`의 7종은 감사가 실측한 결함이다. 어떤 태스크도 이 파일을 수정하지 않는다.

## 현재 기준선

브랜치 `axis-b-followup-gaps`(= `main` `e5c1bc6`)에서:

- `dotnet build --no-incremental` → 경고 9개 / 오류 0개
- `dotnet test` → **1878 passed, 0 failed**

## File Structure

| 파일 | 책임 | 변경 | 태스크 |
|---|---|---|---|
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 별칭 UPDATE를 제어 어휘·행 출처 검사가 인식 | 수정 | 1 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | `CheckStepInterface`의 파라미터 구간 앵커 | 수정 | 2 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 그림자 복원 검사(b)를 CATCH 문맥에 앵커 | 수정 | 3 |
| `src/ReSet.Core/Services/BatchControlContract.cs` | `RunId` 발급 수단과 기본 키를 계약에 담는다 | 수정 | 4 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | `BatchRun` 행 생성 검사(문서 수준) | 수정 | 5 |

**태스크 순서의 근거:** 1·2·3·5가 같은 파일을 건드리므로 순차로 진행한다. 4는 다른 파일이지만 5가 4의 결과를 소비하지 않으므로 어디에 놓아도 되며, 앞의 셋이 검사 인식 범위를 정리한 뒤 계약을 건드리는 편이 충돌이 적다. 5를 마지막에 두는 이유는 그것만 `ValidateConsolidated` 쪽이라 앞의 넷과 성격이 다르기 때문이다.

---

### Task 1: 별칭 UPDATE를 제어 어휘·행 출처 검사가 인식한다

**근거(최종 리뷰 실행 재현):**

```sql
UPDATE bsj SET bsj.ExecutionStatus = N'Completed', bsj.CompletedAt = GETUTCDATE()
FROM batch.BatchStepJournal bsj WHERE ...;
```

→ 계약 밖 컬럼 2개(`ExecutionStatus`, `CompletedAt`)와 계약 밖 상태값 `Completed`가 **하나도 보고되지 않는다**. `UPDATE cp SET cp.CheckpointStatus = ... FROM batch.BatchCheckpoint cp`만 있고 INSERT가 없어도 행 출처 검사가 통과한다.

원인 둘:
1. `CheckUpdateSetTargets`의 헤더 정규식이 `UPDATE\s+(?:\w+\.)?<bare>\b\s+SET`이라 `UPDATE bsj SET`을 못 본다. `CheckBatchControlRowOrigin`도 같은 형태다.
2. 설령 봤더라도 대입 대상이 `bsj.ExecutionStatus`인데 `^[A-Za-z_]\w*$` 검사에 걸려 건너뛴다.

`docs/architecture.md:433-434`가 `UPDATE A SET ... FROM T A`를 이 저장소가 다루는 표준 T-SQL 관용으로 명시한다. 이 형태가 재생성 산출물에 나오면 **B2 9건 + B3 6건이 초록 게이트 아래 그대로 남는다.**

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`

**Interfaces:**
- Consumes: `ExtractTopLevelClause(string text, int startIndex)`, `SplitTopLevelSegments(string)`, `StripBracketQuoting(string)`, `BlankCommentsAndStrings(string)` — 전부 이 파일의 기존 `private static`. 동작을 바꾸지 않는다.
- Produces: `private static HashSet<string> ResolveControlTableAliases(string cleaned, string bare)` — Task 3·5는 소비하지 않는다. 이 태스크 안에서만 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`의 클래스 안에 더한다.

```csharp
        // 최종 리뷰 실측: UPDATE <별칭> SET ... FROM <제어테이블> <별칭> 형태를
        // 어휘 검사가 아예 인식하지 못해 B2 9건이 초록 게이트 아래 남는다.
        // docs/architecture.md:433-434가 이 형태를 표준 관용으로 명시한다.
        [Fact]
        public void ValidateBatchStep_RejectsAnOutOfContractColumnInAnAliasedUpdate()
        {
            var markdown = Section(@"
UPDATE bsj SET bsj.ExecutionStatus = N'Succeeded'
FROM batch.BatchStepJournal bsj
WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        [Fact]
        public void ValidateBatchStep_RejectsADisallowedStatusValueInAnAliasedUpdate()
        {
            var markdown = Section(@"
UPDATE bsj SET bsj.StepStatus = N'Completed'
FROM batch.BatchStepJournal bsj
WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
        }

        // 별칭 형태로 UPDATE만 하고 INSERT가 없으면 B3도 우회된다.
        [Fact]
        public void ValidateBatchStep_RejectsAnAliasedUpdateOfAJournalRowItNeverInserts()
        {
            var markdown = Section(@"
UPDATE bsj SET bsj.StepStatus = N'Succeeded'
FROM batch.BatchStepJournal bsj
WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("INSERT") && e.Contains("BatchStepJournal"));
        }

        // 별칭 형태의 정상 어휘는 잡히면 안 된다. 넓히면서 오탐을 들이지 않았는지 잠근다.
        [Fact]
        public void ValidateBatchStep_AcceptsTheCanonicalVocabularyInAnAliasedUpdate()
        {
            var markdown = Section(@"
INSERT INTO batch.BatchStepJournal (RunId, StepCode, StepStatus, StartedAtUtc)
VALUES (@RunId, N'S17', N'Running', SYSUTCDATETIME());
UPDATE bsj SET bsj.StepStatus = N'Succeeded', bsj.CompletedAtUtc = SYSUTCDATETIME()
FROM batch.BatchStepJournal bsj
WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("제어 테이블"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("UPDATE만"));
        }

        // 별칭이 제어 테이블에 묶이지 않았으면 그 UPDATE는 대상이 아니다.
        // 업무 테이블을 별칭으로 갱신하는 것은 정상이다.
        [Fact]
        public void ValidateBatchStep_IgnoresAnAliasedUpdateBoundToABusinessTable()
        {
            var markdown = Section(@"
UPDATE m SET m.SettleState = 9, m.ExecutionStatus = N'Completed'
FROM dbo.TSettleMst m
WHERE m.YMD = @pi_strYMD;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("제어 테이블"));
        }

        // 한정자가 제어 테이블 이름 자체인 형태도 벗겨야 한다.
        [Fact]
        public void ValidateBatchStep_StripsAQualifierThatIsTheControlTableNameItself()
        {
            var markdown = Section(@"
UPDATE batch.BatchStepJournal SET batch.BatchStepJournal.ExecutionStatus = N'Succeeded'
WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests" --no-build`

Expected: 신규 6개 중 **4개 실패**(`RejectsAnOutOfContractColumnInAnAliasedUpdate`, `RejectsADisallowedStatusValueInAnAliasedUpdate`, `RejectsAnAliasedUpdateOfAJournalRowItNeverInserts`, `StripsAQualifierThatIsTheControlTableNameItself`). 나머지 2개(`AcceptsTheCanonicalVocabularyInAnAliasedUpdate`, `IgnoresAnAliasedUpdateBoundToABusinessTable`)는 검사가 아예 안 도는 상태라 우연히 통과한다 — 이것이 정상이며, 구현 후 오탐 방어로 남는다.

- [ ] **Step 3: 별칭 해석 헬퍼를 쓴다**

`MechanicalValidator.cs`의 `CheckUpdateSetTargets` 바로 위에 더한다.

```csharp
        /// <summary>
        /// 이 구문에서 제어 테이블에 묶인 별칭을 모은다.
        ///
        /// [왜 필요한가]
        /// 최종 리뷰 실측: `UPDATE bsj SET bsj.ExecutionStatus = N'Completed'
        /// FROM batch.BatchStepJournal bsj`가 어휘 검사와 행 출처 검사를 **둘 다**
        /// 우회했다. 두 검사 모두 "UPDATE 바로 뒤가 테이블명"만 보기 때문이다.
        /// docs/architecture.md:433-434가 이 형태를 이 저장소의 표준 T-SQL 관용으로
        /// 명시하므로 가공의 위험이 아니다 - 재생성 산출물이 이 형태를 쓰면
        /// B2·B3가 초록 게이트 아래 그대로 남는다.
        ///
        /// [왜 FROM/JOIN만 보는가]
        /// 별칭을 테이블에 묶는 자리는 FROM 절과 JOIN 절뿐이다. `AS`는 있어도 되고
        /// 없어도 된다. 다른 테이블에 묶인 별칭은 담지 않는다 - 담으면 업무 테이블을
        /// 별칭으로 갱신하는 정상 구문이 제어 테이블 검사에 걸린다.
        /// </summary>
        private static HashSet<string> ResolveControlTableAliases(string cleaned, string bare)
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match binding in Regex.Matches(
                cleaned,
                $@"\b(?:FROM|JOIN)\s+(?:\w+\.)?{Regex.Escape(bare)}\b\s+(?:AS\s+)?(?<alias>[A-Za-z_]\w*)",
                RegexOptions.IgnoreCase))
            {
                var alias = binding.Groups["alias"].Value;

                // FROM 뒤 첫 토큰이 별칭이 아니라 다음 절 키워드일 수 있다.
                if (Regex.IsMatch(
                        alias,
                        @"^(?:WHERE|SET|INNER|LEFT|RIGHT|FULL|CROSS|OUTER|JOIN|ON|GROUP|ORDER|HAVING|UNION|OPTION|WITH)$",
                        RegexOptions.IgnoreCase))
                {
                    continue;
                }

                aliases.Add(alias);
            }

            return aliases;
        }

        /// <summary>
        /// 대입 대상에서 이 제어 테이블을 가리키는 한정자만 벗긴다.
        ///
        /// `bsj.ExecutionStatus`(bsj가 이 테이블의 별칭)나
        /// `batch.BatchStepJournal.ExecutionStatus`(이름 자체)는 벗겨서 컬럼명을 낸다.
        /// 다른 테이블을 가리키는 한정자는 벗기지 않고 null을 낸다 - 그것은 이
        /// 테이블의 컬럼이 아니므로 대조 대상이 아니다.
        /// </summary>
        private static string? UnqualifyControlColumn(
            string target, string bare, HashSet<string> aliases)
        {
            var name = StripBracketQuoting(target.Trim());

            var lastDot = name.LastIndexOf('.');
            if (lastDot < 0) return name;

            var qualifier = StripBracketQuoting(name[..lastDot].Trim());
            var column = StripBracketQuoting(name[(lastDot + 1)..].Trim());

            var qualifierBare = qualifier[(qualifier.LastIndexOf('.') + 1)..];
            if (aliases.Contains(qualifier) ||
                string.Equals(qualifierBare, bare, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }

            return null;
        }
```

- [ ] **Step 4: `CheckUpdateSetTargets`가 별칭 헤더를 보게 한다**

`CheckUpdateSetTargets`의 본문을 아래로 바꾼다. 시그니처는 그대로다.

```csharp
        private static void CheckUpdateSetTargets(
            string stepMarkdown,
            ControlTable table,
            string bare,
            HashSet<string> known,
            IReadOnlyList<string>? allowed,
            BatchStepPlan step,
            StepValidationResult result)
        {
            // 별칭 묶임은 주석·문자열을 지운 사본에서 본다 - 주석 안의 FROM에
            // 속으면 엉뚱한 별칭이 제어 테이블에 묶인다.
            var aliases = ResolveControlTableAliases(BlankCommentsAndStrings(stepMarkdown), bare);

            // 테이블 이름을 직접 쓴 헤더와, 이 테이블에 묶인 별칭을 쓴 헤더를 함께 본다.
            var headerAlternatives = new List<string> { $@"(?:\w+\.)?{Regex.Escape(bare)}" };
            headerAlternatives.AddRange(aliases.Select(Regex.Escape));

            foreach (Match header in Regex.Matches(
                stepMarkdown,
                $@"UPDATE\s+(?:{string.Join("|", headerAlternatives)})\b\s+SET\s+",
                RegexOptions.IgnoreCase))
            {
                var setClause = ExtractTopLevelClause(stepMarkdown, header.Index + header.Length);

                foreach (var assignment in SplitTopLevelSegments(setClause))
                {
                    var eq = assignment.IndexOf('=');
                    if (eq <= 0) continue;

                    // 한정자가 이 제어 테이블을 가리킬 때만 벗긴다. 다른 것을
                    // 가리키면 null이 와서 대조 대상이 아니다.
                    var name = UnqualifyControlColumn(assignment[..eq], bare, aliases);
                    if (name == null) continue;
                    if (!Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) continue;

                    if (!known.Contains(name))
                    {
                        result.Errors.Add(
                            $"{step.Code} 섹션이 제어 테이블 `{table.Name}`에 계약 밖의 컬럼 " +
                            $"'{name}'을 씁니다. 이 테이블의 컬럼은 " +
                            $"{string.Join(", ", table.Columns.Select(c => c.Name))}가 전부입니다.");
                        continue;
                    }

                    if (allowed == null ||
                        !string.Equals(name, table.StatusColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ReportIfDisallowedStatusValue(assignment[(eq + 1)..], table, allowed, step, result);
                }
            }
        }
```

- [ ] **Step 5: `CheckBatchControlRowOrigin`이 별칭 UPDATE를 UPDATE로 세게 한다**

`CheckBatchControlRowOrigin`의 `updates` 판정을 바꾼다. 나머지는 그대로다.

```csharp
                var bare = table.Name[(table.Name.LastIndexOf('.') + 1)..];

                // 별칭 형태(UPDATE bsj SET ... FROM batch.BatchStepJournal bsj)도
                // 이 테이블의 UPDATE다. 이름 형태만 세면 별칭 형태가 행 출처 검사를
                // 통째로 우회한다(최종 리뷰 실측).
                var aliases = ResolveControlTableAliases(BlankCommentsAndStrings(stepMarkdown), bare);
                var updateAlternatives = new List<string> { $@"(?:\w+\.)?{Regex.Escape(bare)}" };
                updateAlternatives.AddRange(aliases.Select(Regex.Escape));

                var updates = Regex.IsMatch(
                    stepMarkdown,
                    $@"UPDATE\s+(?:{string.Join("|", updateAlternatives)})\b",
                    RegexOptions.IgnoreCase);
                if (!updates) continue;
```

- [ ] **Step 6: 통과를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests" --no-build`
Expected: 전부 통과(기존 + 신규 6).

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~AxisBGoldenCaseTests" --no-build`
Expected: 7 passed(손대지 않았다).

- [ ] **Step 7: 레드-그린을 되돌림으로 확인한다**

`CheckUpdateSetTargets` 안에서 `headerAlternatives.AddRange(aliases.Select(Regex.Escape));` 줄을 임시로 주석 처리하고 재빌드·재실행한다.
Expected: `RejectsAnOutOfContractColumnInAnAliasedUpdate`와 `RejectsADisallowedStatusValueInAnAliasedUpdate`가 실패한다.
확인 뒤 되돌리고 다시 초록을 확인한다.

- [ ] **Step 8: 전체 회귀와 경고를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental 2>&1 | grep -E "경고 [0-9]+개|오류 [0-9]+개" && dotnet test`
Expected: 요약 줄 `경고 9개` / `오류 0개`, 실패 0, 통과 1884(기준선 1878 + 6).

- [ ] **Step 9: 커밋한다**

```bash
cd /Users/payletter/git-root/ReSet-axis-b
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs
git commit -m "fix: 별칭 UPDATE를 제어 어휘·행 출처 검사가 인식한다

UPDATE bsj SET bsj.ExecutionStatus = ... FROM batch.BatchStepJournal bsj가
두 검사를 모두 우회해 B2 9건과 B3 6건이 초록 게이트 아래 남았다.
FROM/JOIN에서 별칭 묶임을 모아 헤더와 대입 대상 양쪽에 반영한다.
다른 테이블에 묶인 별칭은 담지 않는다 - 담으면 업무 테이블을 별칭으로
갱신하는 정상 구문이 제어 테이블 검사에 걸린다."
```

---

### Task 2: `CheckStepInterface`의 파라미터 구간을 펜스와 괄호 깊이로 앵커한다

**근거(최종 리뷰 실행 재현):** 축 B의 병합 차단 수정이 `params`에 `SELECT|DECLARE|BEGIN|FROM`이 있으면 매치를 통째로 버리는 방식을 넣었다. 그 결과 새 미탐이 생겼다:

- 산문에 `` `CREATE PROCEDURE dbo.UP_X` ``가 있고 그 뒤에 진짜 선언이 오면, 게으른 `.*?`가 산문 지점에서 시작해 **진짜 선언의 `AS`를 지나** 소비한다. 폐기 조건에 걸려 매치가 버려지고, `Regex.Matches`는 소비한 구간 **뒤**부터 재개하므로 **진짜 선언이 영영 검사되지 않는다.**
- 같은 이유로 파라미터 목록 안 주석에 `FROM`이 있거나, 기본값이 `= 'FROM'`이거나, 파라미터 이름이 `@From`이면 그 선언의 검사가 통째로 꺼진다.

리뷰가 확인한 것: `@FromDate`·`@BeginYmd`·`@SelectMode`는 `\b` 경계 덕에 안전하다. 위험한 것은 **정확히 `@From`·`@Select`·`@Begin`·`@Declare`**와 위 세 형태다.

**해법:** 폐기 판별자를 버리고 구간을 구조로 앵커한다. ①스캔을 ```sql 펜스 안으로 제한하면 산문 언급이 애초에 들어오지 않는다. ②펜스 안에서 `BlankCommentsAndStrings`를 쓰면 주석과 문자열 리터럴이 공백이 된다. ③선언의 `AS`는 언제나 본문의 테이블 별칭 `AS`보다 앞이므로, 괄호 깊이 0의 첫 `AS`가 곧 선언의 `AS`다.

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — `CheckStepInterface`만
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`

**Interfaces:**
- Consumes: `BlankCommentsAndStrings(string)`(기존), `StepInterfaceFacts.ParameterNames(StepInterface)`(기존)
- Produces: 없음(private 검사 하나의 내부 구조만 바뀐다)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        // 최종 리뷰 실측: 산문의 CREATE PROCEDURE 언급이 게으른 .*?의 출발점이 되어
        // 진짜 선언의 AS를 지나 소비하고, 키워드 폐기 조건에 걸려 매치가 버려진다.
        // Regex.Matches는 소비한 구간 뒤부터 재개하므로 진짜 선언이 영영 검사되지 않는다.
        [Fact]
        public void ValidateBatchStep_StillChecksARealDeclarationAfterAProseMention()
        {
            var markdown = $$"""
                ### S17 완료 파티션 원자적 게시

                원본 `CREATE PROCEDURE dbo.UP_UTIL_SETTLE_INS`를 SELECT ... FROM 기준으로 옮긴다.

                ```sql
                CREATE PROCEDURE batch.usp_S17 @pi_strYMD varchar(8), @pi_bypassPreCheck bit AS
                SELECT 1 FROM dbo.TSettleMst AS t;
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        // 파라미터 목록 안 주석에 FROM이 있어도 검사가 꺼지면 안 된다.
        [Fact]
        public void ValidateBatchStep_StillChecksWhenTheParamListHasACommentContainingAKeyword()
        {
            var markdown = Section(@"
CREATE PROCEDURE batch.usp_S17
    @pi_strYMD varchar(8), -- 원본 SELECT ... FROM 기준일
    @pi_bypassPreCheck bit
AS
SELECT 1;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        // 기본값 문자열 리터럴에 FROM이 있어도 검사가 꺼지면 안 된다.
        [Fact]
        public void ValidateBatchStep_StillChecksWhenADefaultLiteralContainsAKeyword()
        {
            var markdown = Section(@"
CREATE PROCEDURE batch.usp_S17 @pi_mode nvarchar(10) = 'FROM', @pi_bypassPreCheck bit AS
SELECT 1;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_mode nvarchar(10)"));

            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        // 파라미터 이름이 정확히 @From이어도 검사가 꺼지면 안 된다.
        [Fact]
        public void ValidateBatchStep_StillChecksWhenAParameterIsNamedExactlyFrom()
        {
            var markdown = Section(@"
CREATE PROCEDURE batch.usp_S17 @From varchar(8), @pi_bypassPreCheck bit AS
SELECT 1;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@From varchar(8)"));

            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        // 본문의 테이블 별칭 AS가 파라미터 구간으로 새어 들어오면 안 된다.
        // 선언의 AS는 언제나 본문의 별칭 AS보다 앞이다.
        [Fact]
        public void ValidateBatchStep_DoesNotTreatABodyTableAliasAsThePartOfTheParamList()
        {
            var markdown = Section(@"
CREATE PROCEDURE batch.usp_S17 @pi_strYMD varchar(8) AS
DECLARE @v_currentStepId INT = 0;
SELECT 1 FROM dbo.TSettleMst AS t WHERE t.YMD = @pi_strYMD;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.DoesNotContain(result.Errors, e => e.Contains("@v_currentStepId"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("@pi_strYMD"));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests" --no-build`
Expected: 신규 5개 중 **4개 실패**(`StillChecksARealDeclarationAfterAProseMention`, `StillChecksWhenTheParamListHasACommentContainingAKeyword`, `StillChecksWhenADefaultLiteralContainsAKeyword`, `StillChecksWhenAParameterIsNamedExactlyFrom`). `DoesNotTreatABodyTableAliasAsThePartOfTheParamList`는 현재 폐기 조건 덕에 우연히 통과한다 — 구현 후 오탐 방어로 남는다.

- [ ] **Step 3: 최소 구현을 쓴다**

`CheckStepInterface`의 선언 순회 부분(`foreach (Match declaration in Regex.Matches(...))` 블록 전체)을 아래로 바꾼다. 함수 앞부분(`iface == null` 소프트 스킵, `allowed` 조립)은 그대로 둔다.

```csharp
            // 선언은 SQL 펜스 안에서만 찾는다.
            //
            // [왜 펜스로 제한하는가]
            // 산문이 원본 SP를 `CREATE PROCEDURE dbo.UP_X`로 언급하면 게으른 .*?가
            // 그 지점에서 출발해 진짜 선언의 AS를 지나 소비한다. Regex.Matches는
            // 소비한 구간 뒤부터 재개하므로 진짜 선언이 영영 검사되지 않는다
            // (최종 리뷰 실측). 선언은 펜스 안에 있고 산문 언급은 밖에 있다.
            //
            // [왜 괄호 깊이 0의 첫 AS인가]
            // 선언의 AS는 언제나 본문의 테이블 별칭 AS(FROM t AS x)보다 앞이다.
            // 깊이 0 조건은 varchar(8)·decimal(18,2) 같은 타입 괄호 안에서 끊기지
            // 않게 한다. 주석과 문자열은 미리 공백으로 지우므로 `= 'FROM'`이나
            // 목록 안 주석에 속지 않는다 - 그 둘 때문에 검사가 통째로 꺼지던
            // 키워드 폐기 방식을 이 앵커가 대신한다.
            foreach (Match fence in Regex.Matches(
                stepMarkdown, @"```sql(?<sql>.*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var sql = fence.Groups["sql"].Value;
                var cleaned = BlankCommentsAndStrings(sql);

                foreach (Match header in Regex.Matches(
                    cleaned,
                    @"(?:CREATE\s+(?:OR\s+ALTER\s+)?|ALTER\s+)PROC(?:EDURE)?\s+[^\s(]+\s*\(?",
                    RegexOptions.IgnoreCase))
                {
                    var paramsEnd = FindTopLevelAs(cleaned, header.Index + header.Length);
                    if (paramsEnd < 0) continue;

                    // 이름은 원본에서 뽑는다 - 지운 사본은 문자열 안이 공백이지만
                    // 파라미터 이름은 리터럴 밖이라 어느 쪽에서 뽑아도 같다.
                    // 길이가 보존되므로 인덱스가 정렬된다.
                    var paramsText = sql[(header.Index + header.Length)..paramsEnd];

                    foreach (Match parameter in Regex.Matches(paramsText, @"@\w+"))
                    {
                        if (allowed.Contains(parameter.Value)) continue;

                        result.Errors.Add(
                            $"{step.Code} 섹션이 원본에 없는 입력 파라미터 '{parameter.Value}'를 선언합니다. " +
                            $"이 단계의 인터페이스는 원본 프로시저의 파라미터가 전부입니다 " +
                            $"({string.Join(", ", iface.Parameters)}). 재시작·스킵·검사 우회를 위해 " +
                            "입력을 늘리지 마십시오 - 이미 완료된 단계는 오케스트레이터가 " +
                            "체크포인트를 보고 호출하지 않으며, 업무 보호 검사는 호출될 때마다 " +
                            "무조건 수행되어야 합니다.");
                    }
                }
            }
```

같은 파일의 `CheckStepInterface` 아래에 헬퍼를 더한다.

```csharp
        /// <summary>
        /// startIndex부터 괄호 깊이 0에 있는 첫 `AS` 토큰의 시작 인덱스를 낸다.
        /// 없으면 -1. 입력은 이미 주석·문자열이 공백으로 지워진 사본이어야 한다.
        /// </summary>
        private static int FindTopLevelAs(string cleaned, int startIndex)
        {
            var depth = 0;

            for (var i = startIndex; i < cleaned.Length; i++)
            {
                var ch = cleaned[i];

                if (ch == '(') { depth++; continue; }
                if (ch == ')') { if (depth > 0) depth--; continue; }
                if (depth != 0) continue;

                if ((ch != 'A' && ch != 'a') ||
                    i + 1 >= cleaned.Length ||
                    (cleaned[i + 1] != 'S' && cleaned[i + 1] != 's'))
                {
                    continue;
                }

                var beforeIsBoundary = i == 0 || !char.IsLetterOrDigit(cleaned[i - 1]) && cleaned[i - 1] != '_';
                var afterIndex = i + 2;
                var afterIsBoundary = afterIndex >= cleaned.Length ||
                                      !char.IsLetterOrDigit(cleaned[afterIndex]) && cleaned[afterIndex] != '_';

                if (beforeIsBoundary && afterIsBoundary) return i;
            }

            return -1;
        }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests" --no-build`
Expected: 전부 통과.

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~AxisBGoldenCaseTests" --no-build`
Expected: 7 passed. 골든 `S10_ConditionalGuardOnABypassParameter`가 여전히 `@pi_bypassPreCheck`를 잡는지가 이 태스크의 핵심 미탐 방어다.

- [ ] **Step 5: 레드-그린을 되돌림으로 확인한다**

`CheckStepInterface`의 호출부를 임시로 주석 처리하고 재빌드·재실행한다.
Expected: 신규 4개와 기존 인터페이스 검사 테스트들, 골든 `S10`이 실패한다.
확인 뒤 되돌리고 다시 초록을 확인한다.

- [ ] **Step 6: 전체 회귀와 경고를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental 2>&1 | grep -E "경고 [0-9]+개|오류 [0-9]+개" && dotnet test`
Expected: 요약 줄 `경고 9개` / `오류 0개`, 실패 0, 통과 1889(1884 + 5).

**기존 테스트가 하나라도 깨지면 고치지 말고 보고한다** — 펜스 밖에 선언을 두는 기존 픽스처가 있다는 뜻이고, 그 경우 계약을 다시 판단해야 한다.

- [ ] **Step 7: 커밋한다**

```bash
cd /Users/payletter/git-root/ReSet-axis-b
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs
git commit -m "fix: 인터페이스 검사의 파라미터 구간을 펜스와 괄호 깊이로 앵커한다

키워드 폐기 방식은 산문 언급·목록 안 주석·기본값 리터럴·@From 이름에서
선언의 검사를 통째로 껐다. 게으른 .*?가 진짜 선언의 AS를 지나 소비한 뒤
매치가 버려지면 Regex.Matches가 그 뒤부터 재개해 선언이 영영 검사되지 않는다.
스캔을 SQL 펜스 안으로 제한하고, 주석·문자열을 지운 사본에서 괄호 깊이 0의
첫 AS까지를 파라미터 구간으로 잡는다."
```

---

### Task 3: 그림자 복원 검사(b)를 CATCH 문맥에 앵커한다

**근거(최종 리뷰 실행 재현):** 축 B의 병합 차단 수정이 (b)를 "열린 트랜잭션 구간 안이면 제외"로 좁혔다. Few-Shot 스왑 오탐은 닫혔지만 새 미탐이 생겼다:

```sql
BEGIN CATCH
  BEGIN TRAN;
    DELETE FROM dbo.TSettleByTX;
    INSERT INTO dbo.TSettleByTX SELECT * FROM batch_shadow.TSettleByTX_RunId_S12;
  COMMIT TRAN;
END CATCH
```

리뷰 판정: "감사 S12 결함 그대로에 원자성 래퍼만 씌운 것이다. 래퍼는 다른 거래일의 행을 되돌려주지 않고 피해를 원자적으로 커밋할 뿐이다. L1이 보상 복원을 지적한 뒤 모델이 자연스럽게 손댈 형태이기도 하다."

**해법:** 제외 조건을 트랜잭션 깊이가 아니라 **문맥**으로 바꾼다. 정방향 스왑은 CATCH 밖에 있고 보상 복원은 CATCH 안에 있다. 제외는 `열린 트랜잭션 안 && CATCH 밖`일 때만 한다.

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — `CheckShadowBackupContract`의 (b) 부분만
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`

**Interfaces:**
- Consumes: `CatchBlockPattern`(기존 `private static readonly Regex`), `BlankCommentsAndStrings`(기존), `openTransactionSpans`(같은 함수가 (a)에서 이미 계산한 지역 변수)
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        // 최종 리뷰 실측: 보상 복원을 자기 트랜잭션으로 감싸면 (b)가 통째로 제외한다.
        // 래퍼는 다른 거래일의 행을 되돌려주지 않고 피해를 원자적으로 커밋할 뿐이다.
        [Fact]
        public void ValidateBatchStep_RejectsAWhereLessRestoreWrappedInItsOwnTransactionInsideCatch()
        {
            var markdown = Section(@"
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    BEGIN TRAN;
        DELETE FROM dbo.TSettleByTX;
        INSERT INTO dbo.TSettleByTX SELECT * FROM batch_shadow.TSettleByTX_RunId_S12;
    COMMIT TRAN;
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("전량 삭제"));
        }

        // 정방향 스왑은 CATCH 밖이므로 계속 제외되어야 한다.
        // 프롬프트의 Few-Shot이 가르치는 형태다 - 잡으면 지배 계약 위반이다.
        [Fact]
        public void ValidateBatchStep_StillAcceptsTheForwardSwapOutsideCatch()
        {
            var markdown = Section(@"
BEGIN TRAN;
    DELETE FROM dbo.TargetTable;
    INSERT INTO dbo.TargetTable SELECT * FROM batch_shadow.TargetTable_RunId_S13;
COMMIT TRAN;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("전량 삭제"));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests" --no-build`
Expected: `RejectsAWhereLessRestoreWrappedInItsOwnTransactionInsideCatch`가 실패한다. `StillAcceptsTheForwardSwapOutsideCatch`는 이미 통과한다 — 구현 후 오탐 방어로 남는다.

- [ ] **Step 3: 최소 구현을 쓴다**

`CheckShadowBackupContract`의 (b) 부분에서 제외 판정을 바꾼다. `openTransactionSpans` 계산부와 (a)·(c)는 손대지 않는다.

(b) 순회 **앞**에 CATCH 구간을 모은다.

```csharp
            // 보상 복원은 CATCH 안에 있고 정방향 스왑은 밖에 있다. 이 구분이
            // (b)의 실제 판별 기준이다 - 트랜잭션 깊이로만 제외하면 보상 복원을
            // 자기 트랜잭션으로 감싼 형태가 통째로 빠져나간다(최종 리뷰 실측).
            var catchSpans = CatchBlockPattern.Matches(cleaned)
                .Select(m => (Start: m.Index, End: m.Index + m.Length))
                .ToList();
```

(b) 순회 안의 제외 조건을 바꾼다.

```csharp
                var insideOpenTransaction =
                    openTransactionSpans.Any(span => restore.Index >= span.Start && restore.Index < span.End);
                var insideCatch =
                    catchSpans.Any(span => restore.Index >= span.Start && restore.Index < span.End);

                // 열린 트랜잭션 안이면서 CATCH 밖일 때만 제외한다. 그것이 정방향
                // 스왑이다. CATCH 안이면 트랜잭션으로 감쌌든 아니든 보상 복원이다.
                if (insideOpenTransaction && !insideCatch) continue;
```

- [ ] **Step 4: 통과를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests" --no-build`
Expected: 전부 통과. 특히 기존 `ValidateBatchStep_StillRejectsAWhereLessRestoreOutsideAnyTransaction`과 `ValidateBatchStep_RejectsARestoreThatDeletesWithoutARange`가 그대로 통과해야 한다.

- [ ] **Step 5: Few-Shot 회귀 테스트가 여전히 초록인지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~FewShotExamples" --no-build`
Expected: 통과. 이 테스트가 프롬프트의 모범 예시 4블록이 L1을 통과하는지 본다 — 이번 변경이 스왑 제외를 좁히므로 **여기가 깨지면 지배 계약 위반이다.** 깨지면 되돌리고 보고한다.

- [ ] **Step 6: 레드-그린을 되돌림으로 확인한다**

`if (insideOpenTransaction && !insideCatch) continue;`를 `if (insideOpenTransaction) continue;`로 임시로 되돌리고 재빌드·재실행한다.
Expected: `RejectsAWhereLessRestoreWrappedInItsOwnTransactionInsideCatch`만 실패한다.
확인 뒤 되돌리고 다시 초록을 확인한다.

- [ ] **Step 7: 전체 회귀와 경고를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental 2>&1 | grep -E "경고 [0-9]+개|오류 [0-9]+개" && dotnet test`
Expected: 요약 줄 `경고 9개` / `오류 0개`, 실패 0, 통과 1891(1889 + 2).

- [ ] **Step 8: 커밋한다**

```bash
cd /Users/payletter/git-root/ReSet-axis-b
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs
git commit -m "fix: 그림자 복원 검사를 트랜잭션 깊이가 아니라 CATCH 문맥에 앵커한다

보상 복원을 자기 BEGIN TRAN으로 감싸면 깊이 기반 제외가 통째로 빠뜨렸다.
래퍼는 다른 거래일의 행을 되돌려주지 않고 피해를 원자적으로 커밋할 뿐이다.
정방향 스왑은 CATCH 밖, 보상 복원은 CATCH 안이라는 것이 (b)의 실제 판별
기준이므로, 열린 트랜잭션 안이면서 CATCH 밖일 때만 제외한다."
```

---

### Task 4: `RunId` 발급 수단과 기본 키를 계약에 담는다

**근거(최종 리뷰):** `BatchControlContract.RenderDdl()` 산출에 **IDENTITY도 SEQUENCE도 PRIMARY KEY도 DEFAULT도 없다**(`RunId bigint NOT NULL`뿐). 프롬프트 표는 "The FIRST step … INSERTs this row and issues RunId"라고 말하는데 **어떻게** 발급하는지가 계약에 없다. 네 테이블 모두 키 제약이 없어 "자기가 INSERT한 행을 UPDATE한다"는 계약에 보장도 없다.

리뷰 판정: "이 축이 없애려던 실패 모드 — 18번의 독립 호출이 각자 방식을 지어내는 것 — 이 `RunId` 발급 축에서 그대로 재현될 수 있다."

**설계 결정(이 계획이 정한다):**
- `batch.BatchRun.RunId`만 `IDENTITY(1,1)`이다. 발급 지점이 하나여야 하고 그 지점이 첫 단계의 INSERT다. 다른 테이블의 `RunId`는 그 값을 받아 쓰는 자리이므로 IDENTITY가 아니다.
- 기본 키: `BatchRun(RunId)`, `BatchStepJournal(RunId, StepCode)`, `BatchCheckpoint(RunId, StepCode)`.
- `BatchValidationIssue`는 기본 키를 두지 않는다. `ProducerInsertsOnly`라 전이가 없고, 한 단계가 같은 `IssueCode`를 여러 번 낼 수 있어 자연 키가 없다. 대리 키를 새로 넣으면 단계가 써야 할 컬럼이 늘어나므로 계약을 넓히지 않고 **주석으로 이유를 남긴다.**
- 외래 키는 넣지 않는다. 이 계획의 범위 밖이고, 배치 제어 테이블에 FK를 거는 것은 운영 정책 결정이다.

**Files:**
- Modify: `src/ReSet.Core/Services/BatchControlContract.cs`
- Test: `tests/ReSet.Core.Tests/BatchControlContractTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `ControlColumn`에 `bool IsIdentity = false` 위치 매개변수가 **맨 뒤**에 추가된다. 기존 생성 호출은 그대로 컴파일된다.
  - `ControlTable`에 `IReadOnlyList<string>? PrimaryKey = null`이 **맨 뒤**에 추가된다. 기존 생성 호출은 그대로 컴파일된다.
  - `RenderDdl()`과 `RenderPromptTable()`의 시그니처는 그대로다(산출 문자열만 바뀐다).

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/BatchControlContractTests.cs`에 더한다.

```csharp
    // 최종 리뷰: 프롬프트 표는 "첫 단계가 INSERT하며 RunId를 발급한다"고 말하는데
    // DDL에 발급 수단이 없었다. 18번의 독립 호출이 각자 방식을 지어내는 실패 모드가
    // 이 축에서 재현될 수 있다.
    [Fact]
    public void RenderDdl_IssuesRunIdWithIdentityOnTheRunTableOnly()
    {
        var ddl = BatchControlContract.RenderDdl();

        Assert.Contains("RunId bigint IDENTITY(1,1) NOT NULL", ddl);
        // 저널·체크포인트의 RunId는 발급받아 쓰는 자리다. 거기까지 IDENTITY면
        // 각 테이블이 자기 번호를 새로 매겨 실행 단위가 갈라진다.
        Assert.Equal(1, Regex.Matches(ddl, @"IDENTITY\(1,1\)").Count);
    }

    [Fact]
    public void RenderDdl_DeclaresAPrimaryKeyForEveryTableThatHasATransition()
    {
        var ddl = BatchControlContract.RenderDdl();

        Assert.Contains("CONSTRAINT PK_BatchRun PRIMARY KEY (RunId)", ddl);
        Assert.Contains("CONSTRAINT PK_BatchStepJournal PRIMARY KEY (RunId, StepCode)", ddl);
        Assert.Contains("CONSTRAINT PK_BatchCheckpoint PRIMARY KEY (RunId, StepCode)", ddl);
    }

    // 전이가 없는 테이블에는 키를 두지 않는다. 한 단계가 같은 IssueCode를 여러 번
    // 낼 수 있어 자연 키가 없고, 대리 키를 넣으면 단계가 써야 할 컬럼이 늘어난다.
    [Fact]
    public void RenderDdl_DoesNotDeclareAPrimaryKeyForTheInsertOnlyTable()
    {
        Assert.DoesNotContain("PK_BatchValidationIssue", BatchControlContract.RenderDdl());
    }

    // 프롬프트 표도 발급 수단을 말해야 한다 - DDL에만 있으면 단계 문서를 쓰는
    // 모델이 그 사실을 못 본다.
    [Fact]
    public void RenderPromptTable_SaysHowRunIdIsIssued()
    {
        Assert.Contains("IDENTITY", BatchControlContract.RenderPromptTable());
    }
```

파일 상단에 `using System.Text.RegularExpressions;`가 없으면 더한다.

- [ ] **Step 2: 실패를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~BatchControlContractTests" --no-build`
Expected: 신규 4개 중 3개 실패(`IssuesRunIdWithIdentityOnTheRunTableOnly`, `DeclaresAPrimaryKeyForEveryTableThatHasATransition`, `SaysHowRunIdIsIssued`). `DoesNotDeclareAPrimaryKeyForTheInsertOnlyTable`은 아직 어떤 PK도 없으므로 우연히 통과한다 — 구현 후 방어로 남는다.

- [ ] **Step 3: 레코드에 두 사실을 더한다**

`BatchControlContract.cs`의 `ControlColumn`과 `ControlTable`을 바꾼다.

```csharp
    /// <param name="IsIdentity">
    /// 이 컬럼이 값을 스스로 발급하는가. batch.BatchRun.RunId만 참이다 -
    /// 발급 지점이 하나여야 실행 단위가 갈라지지 않는다.
    /// </param>
    public sealed record ControlColumn(
        string Name,
        string SqlType,
        bool Nullable,
        IReadOnlyList<string>? AllowedValues = null,
        bool IsIdentity = false);

    /// <param name="StatusColumn">상태 어휘를 담은 컬럼. 없으면 null.</param>
    /// <param name="PrimaryKey">
    /// 기본 키 컬럼 목록. 전이가 없는 테이블(ProducerInsertsOnly)에는 두지 않는다 -
    /// 한 단계가 같은 IssueCode를 여러 번 낼 수 있어 자연 키가 없고, 대리 키를
    /// 넣으면 단계가 써야 할 컬럼이 늘어난다.
    /// </param>
    public sealed record ControlTable(
        string Name,
        IReadOnlyList<ControlColumn> Columns,
        ControlRowOrigin Origin,
        string? StatusColumn,
        IReadOnlyList<string>? PrimaryKey = null);
```

- [ ] **Step 4: 네 테이블 정의를 고친다**

`Tables` 초기화에서 세 곳을 바꾼다.

`batch.BatchRun`의 `RunId` 컬럼을 IDENTITY로 바꾸고 테이블에 PK를 더한다.

```csharp
            new ControlTable(
                "batch.BatchRun",
                new[]
                {
                    new ControlColumn("RunId", "bigint", false, null, IsIdentity: true),
                    new ControlColumn("JobName", "nvarchar(128)", false),
                    new ControlColumn("BatchYmd", "varchar(8)", false),
                    new ControlColumn("RunStatus", "nvarchar(20)", false, RunStates),
                    new ControlColumn("ResumeFromStepCode", "nvarchar(10)", true),
                    new ControlColumn("StartedAtUtc", "datetime2(3)", false),
                    new ControlColumn("CompletedAtUtc", "datetime2(3)", true),
                    new ControlColumn("ErrorMessage", "nvarchar(max)", true)
                },
                ControlRowOrigin.FirstStepInserts,
                "RunStatus",
                new[] { "RunId" }),
```

`batch.BatchStepJournal`과 `batch.BatchCheckpoint`에는 PK만 더한다(컬럼 목록은 그대로).

```csharp
                ControlRowOrigin.EachStepInserts,
                "StepStatus",
                new[] { "RunId", "StepCode" }),
```

```csharp
                ControlRowOrigin.EachStepInserts,
                "CheckpointStatus",
                new[] { "RunId", "StepCode" }),
```

`batch.BatchValidationIssue`는 그대로 둔다(`PrimaryKey`가 기본값 null).

- [ ] **Step 5: `RenderDdl`이 두 사실을 내게 한다**

`RenderDdl()`의 컬럼 줄 조립과 제약 조립을 바꾼다.

```csharp
                var lines = new List<string>();
                foreach (var col in table.Columns)
                {
                    var identity = col.IsIdentity ? " IDENTITY(1,1)" : "";
                    var nullability = col.Nullable ? "NULL" : "NOT NULL";
                    lines.Add($"    {col.Name} {col.SqlType}{identity} {nullability}");
                }

                if (table.PrimaryKey is { Count: > 0 })
                {
                    lines.Add($"    CONSTRAINT PK_{BareName(table.Name)} " +
                              $"PRIMARY KEY ({string.Join(", ", table.PrimaryKey)})");
                }

                foreach (var col in table.Columns.Where(c => c.AllowedValues is { Count: > 0 }))
                {
                    var values = string.Join(", ", col.AllowedValues!.Select(v => $"N'{v}'"));
                    lines.Add($"    CONSTRAINT CK_{BareName(table.Name)}_{col.Name} " +
                              $"CHECK ({col.Name} IN ({values}))");
                }
```

- [ ] **Step 6: `RenderPromptTable`이 발급 수단을 말하게 한다**

`RenderPromptTable()`의 행 출처 문구에서 `FirstStepInserts` 가지를 바꾼다.

```csharp
                    ControlRowOrigin.FirstStepInserts =>
                        "The FIRST step in the step list INSERTs this row; RunId is issued by IDENTITY, " +
                        "so read it back with SCOPE_IDENTITY() and pass it to every later step. " +
                        "NEVER compute a RunId yourself. Later steps UPDATE this row.",
```

- [ ] **Step 7: 통과를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~BatchControlContractTests" --no-build`
Expected: 전부 통과.

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~TaskFileComposerTests" --no-build`
Expected: 전부 통과. 부트스트랩 문서가 `RenderDdl()`을 그대로 싣는다 — 산출이 바뀌었으므로 여기가 깨지면 그 테스트가 무엇을 고정하고 있었는지 확인하고 보고한다.

- [ ] **Step 8: 프롬프트 캐시 불변성을 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~PromptCacheBreakpoint" --no-build && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~SharedPrefixIsIdenticalAcrossSteps" --no-build`
Expected: 전부 통과. `RenderPromptTable()`이 공유 접두사에 실리므로 문구가 길어져도 **단계마다 바이트 동일**이라는 성질은 유지되어야 한다(설계 §4).

- [ ] **Step 9: 전체 회귀와 경고를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental 2>&1 | grep -E "경고 [0-9]+개|오류 [0-9]+개" && dotnet test`
Expected: 요약 줄 `경고 9개` / `오류 0개`, 실패 0, 통과 1895(1891 + 4).

- [ ] **Step 10: 커밋한다**

```bash
cd /Users/payletter/git-root/ReSet-axis-b
git add src/ReSet.Core/Services/BatchControlContract.cs tests/ReSet.Core.Tests/BatchControlContractTests.cs
git commit -m "feat: RunId 발급 수단과 기본 키를 제어 테이블 계약에 담는다

프롬프트 표는 첫 단계가 RunId를 발급한다고 말했는데 DDL에 발급 수단이
없었다. 18번의 독립 호출이 각자 방식을 지어내는 실패 모드가 이 축에서
재현될 수 있다. BatchRun.RunId만 IDENTITY로 두고 SCOPE_IDENTITY()로
읽어 넘기라고 프롬프트 표에 적는다. 전이가 있는 세 테이블에 기본 키를
두고, 전이가 없는 BatchValidationIssue에는 두지 않는다."
```

---

### Task 5: `batch.BatchRun` 행 생성을 문서 수준에서 검사한다

**근거(최종 리뷰 COVERAGE 판정):** `CheckBatchControlRowOrigin`은 `ControlRowOrigin.EachStepInserts`인 테이블(저널·체크포인트)만 본다. `batch.BatchRun`의 `FirstStepInserts`는 **어떤 검사도 보지 않는다.** 감사가 실측한 "`INSERT INTO batch.BatchRun`이 번들 전체에 0건"은 프롬프트와 DDL로만 닫히고 기계 검사가 없다.

축 B 설계 §3이 "18개 문서를 한꺼번에 읽는 교차 단계 검사는 만들지 않는다"고 명시했으므로 단계 검사로는 이것을 잡을 수 없다 — 어느 단계가 첫 단계인지 단계 문서 하나만 봐서는 모른다. 그러나 **통합 문서**(`ValidateConsolidated`)는 계획서 전체를 보고, 카티전 검사가 이미 그 자리에서 돈다. 문서 전체에 `INSERT INTO batch.BatchRun`이 최소 한 번 나타나는지 보는 것으로 닫힌다.

**소프트 스킵:** 문서가 `batch.BatchRun`을 언급조차 하지 않으면 검사하지 않는다. 이 계획이 다루지 않는 형태의 Job일 수 있다.

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — `ErrorType`에 값 하나 추가, `ValidateConsolidated`에 호출 하나 추가, 검사 하나 추가
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `BatchControlContract.Tables`, `ControlRowOrigin`(기존), `BlankCommentsAndStrings`(기존)
- Produces: `ErrorType.BatchRunRowNeverCreated`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`에 더한다. 이 파일이 이미 쓰는 `ConsolidatedDocumentWithVerificationSql` 헬퍼와 같은 관용으로 최소 문서를 만든다 — **그 헬퍼의 실제 이름과 시그니처를 먼저 읽고 재사용한다.** 없으면 같은 파일의 기존 통합 문서 조립 방식을 그대로 복사한다.

```csharp
    // 감사 실측: INSERT INTO batch.BatchRun이 번들 전체에 0건이었다. 단계 검사로는
    // 잡을 수 없다 - 어느 단계가 첫 단계인지 단계 문서 하나만 봐서는 모른다.
    // 통합 문서는 계획서 전체를 보므로 여기서 닫는다.
    [Fact]
    public void ValidateConsolidated_RejectsADocumentThatUpdatesBatchRunButNeverInsertsIt()
    {
        var markdown = ConsolidatedDocumentWithStepBody(@"
UPDATE batch.BatchRun SET RunStatus = N'Succeeded', CompletedAtUtc = SYSUTCDATETIME()
WHERE RunId = @RunId;");

        var result = new MechanicalValidator().ValidateConsolidated(markdown);

        Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
    }

    [Fact]
    public void ValidateConsolidated_AcceptsADocumentThatInsertsBatchRunSomewhere()
    {
        var markdown = ConsolidatedDocumentWithStepBody(@"
INSERT INTO batch.BatchRun (JobName, BatchYmd, RunStatus, StartedAtUtc)
VALUES (@JobName, @BatchYmd, N'Running', SYSUTCDATETIME());
SET @RunId = SCOPE_IDENTITY();
UPDATE batch.BatchRun SET RunStatus = N'Succeeded' WHERE RunId = @RunId;");

        var result = new MechanicalValidator().ValidateConsolidated(markdown);

        Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
    }

    // 소프트 스킵: 문서가 이 테이블을 언급조차 하지 않으면 검사하지 않는다.
    [Fact]
    public void ValidateConsolidated_SkipsTheBatchRunCheckWhenTheDocumentNeverMentionsIt()
    {
        var markdown = ConsolidatedDocumentWithStepBody("SELECT 1;");

        var result = new MechanicalValidator().ValidateConsolidated(markdown);

        Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
    }
```

같은 파일에 헬퍼를 더한다(기존 `ConsolidatedDocumentWithVerificationSql`과 같은 골격, 본문 위치만 다르다).

```csharp
    /// <summary>
    /// RequiredConsolidatedHeaders의 네 헤더를 갖춘 최소 통합 문서를 만들고,
    /// 단계 상세 절에 주어진 SQL을 싣는다. 검증 SQL 절은 비워 둔다 -
    /// 카티전 검사가 함께 발화하면 이 테스트가 무엇을 보는지 흐려진다.
    /// </summary>
    private static string ConsolidatedDocumentWithStepBody(string sql) => $$"""
        ## 통합 배치 아키텍처 개요

        내용.

        ## Mermaid 기반 통합 흐름도

        ```mermaid
        flowchart TD
        A["시작"] --> B["끝"]
        ```

        ## 단계별 이행 상세 및 의사코드

        ```sql
        {{sql}}
        ```

        ## 통합 데이터 정합성 검증 SQL 세트

        내용.
        """;
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ValidateConsolidated" --no-build`
Expected: 컴파일 실패 — `ErrorType.BatchRunRowNeverCreated`가 없다.

- [ ] **Step 3: `ErrorType`에 값을 더한다**

`General` **앞**에 더한다.

```csharp
        // 이 값을 여기 넣으면 General의 서수가 뒤로 한 칸 더 밀린다. 이 코드베이스
        // 어디에도 (int)ErrorType 캐스트나 숫자 직렬화가 없으므로(문자열 이름으로만
        // 비교·표시한다) 기능 영향은 없다.
        BatchRunRowNeverCreated,
```

- [ ] **Step 4: 검사를 쓴다**

`CheckVerificationCartesianComparison` 옆에 더한다.

```csharp
        /// <summary>
        /// 실행 행을 만드는 지점이 계획서 전체에 하나도 없는지 본다.
        ///
        /// 감사 실측: INSERT INTO batch.BatchRun이 번들 전체에 0건이었다. 모든
        /// 단계가 UPDATE만 해서 0행이 갱신되고, 실행 단위 자체가 존재하지 않았다.
        ///
        /// [왜 통합 문서에서 보는가]
        /// 단계 검사로는 잡을 수 없다 - 어느 단계가 첫 단계인지 단계 문서 하나만
        /// 봐서는 모르고, 설계 §3이 18개 문서를 한꺼번에 읽는 교차 검사를 배제했다.
        /// 통합 문서는 계획서 전체를 보므로 "문서 어딘가에 최소 한 번"으로 닫힌다.
        ///
        /// [왜 소프트 스킵하는가]
        /// 문서가 이 테이블을 언급조차 하지 않으면 이 계약이 적용되는 Job이
        /// 아닐 수 있다. 없는 것을 결함으로 들지 않는다.
        /// </summary>
        private static void CheckBatchRunRowCreation(string markdown, ValidationResult result)
        {
            var cleaned = BlankCommentsAndStrings(markdown);

            foreach (var table in BatchControlContract.Tables)
            {
                if (table.Origin != ControlRowOrigin.FirstStepInserts) continue;

                var bare = table.Name[(table.Name.LastIndexOf('.') + 1)..];

                var mentioned = Regex.IsMatch(
                    cleaned, $@"(?:\w+\.)?{Regex.Escape(bare)}\b", RegexOptions.IgnoreCase);
                if (!mentioned) continue;

                var inserted = Regex.IsMatch(
                    cleaned,
                    $@"(INSERT\s+INTO|MERGE)\s+(?:\w+\.)?{Regex.Escape(bare)}\b",
                    RegexOptions.IgnoreCase);
                if (inserted) continue;

                var message =
                    $"계획서 전체에 `{table.Name}` 행을 만드는 지점이 없습니다. " +
                    "이 테이블은 단계 목록의 첫 단계가 INSERT하며 RunId를 발급하는 계약인데, " +
                    "생성 없이 UPDATE만 하면 0행이 갱신되어 실행 단위 자체가 존재하지 않습니다. " +
                    "첫 단계에 INSERT를 두고 SCOPE_IDENTITY()로 발급된 RunId를 이후 단계에 넘기십시오.";

                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.BatchRunRowNeverCreated,
                    Message = message,
                    RawContext = table.Name
                });
            }
        }
```

- [ ] **Step 5: 호출부를 더한다**

`ValidateConsolidated`의 `CheckVerificationCartesianComparison(cleansed, result);` 바로 아래에 더한다.

```csharp
                CheckBatchRunRowCreation(cleansed, result);
```

- [ ] **Step 6: 통과를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorTests" --no-build`
Expected: 전부 통과.

- [ ] **Step 7: 레드-그린을 되돌림으로 확인한다**

Step 5에서 더한 호출부를 임시로 주석 처리하고 재빌드·재실행한다.
Expected: `RejectsADocumentThatUpdatesBatchRunButNeverInsertsIt`만 실패한다.
확인 뒤 되돌리고 다시 초록을 확인한다.

- [ ] **Step 8: 전체 회귀와 경고를 확인한다**

Run: `cd /Users/payletter/git-root/ReSet-axis-b && dotnet build --no-incremental 2>&1 | grep -E "경고 [0-9]+개|오류 [0-9]+개" && dotnet test`
Expected: 요약 줄 `경고 9개` / `오류 0개`, 실패 0, 통과 1898(1895 + 3).

- [ ] **Step 9: 커밋한다**

```bash
cd /Users/payletter/git-root/ReSet-axis-b
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: 실행 행을 만드는 지점이 없는 계획서를 L1이 잡는다

감사 실측: INSERT INTO batch.BatchRun이 번들 전체에 0건이었다. 단계
검사로는 잡을 수 없다 - 어느 단계가 첫 단계인지 단계 문서 하나만 봐서는
모르고 설계 §3이 교차 단계 검사를 배제했다. 통합 문서는 계획서 전체를
보므로 문서 어딘가에 최소 한 번으로 닫는다. 언급조차 없으면 소프트 스킵한다."
```

---

## 마무리 — 이 계획이 끝나도 남는 것

- **실물 코퍼스 대조는 여전히 불가능하다.** `output/`이 gitignore이고 18개 문서가 저장소에 없다. 별칭 UPDATE와 `CREATE OR ALTER` 표기가 실제 산출물에 얼마나 나오는지는 **Job 재생성 1회 후 재감사로만** 답할 수 있다. 이 계획은 "나오면 잡힌다"를 보장할 뿐 "나온다"를 증명하지 않는다.
- **Job 재생성은 이 계획에서 하지 않는다.** 축 A가 안정된 뒤 1회만 돌리고, 그 1회에 축 A·B의 수정이 함께 반영된다.
- **다음 작업으로 남기는 것(최종 리뷰 Important, 이 계획 범위 밖):**
  - 단일 호출 폴백 경로(`GenerateConsolidatedBatchPlanAsync`)에 계약 표가 실리지 않는데 규칙 5가 없는 표를 가리킨다. 그 회차에는 단계 L1도 돌지 않는다.
  - 그림자 생성 검사(a)가 리터럴 `batch_shadow.`만 보므로 `BatchObjectSchemaRule`이 명령하는 런타임 이름 조립 형태에 눈이 멀다.
  - M2가 `OUTPUT` 수식어를 잃는다(`SqlStaticParser.cs:1125-1131`이 `node.Modifier`를 버린다). 프롬프트 표가 "this list is exhaustive"라고 말하면서 `OUTPUT`을 표시하지 못한다.
  - 세미콜론 없이 빈 줄로만 구분된 다문 펜스에서 카티전 검사 오탐.
- **재감사로 확인할 것.** 이 계획이 닫으려는 것은 검사의 **인식 범위**이지 새 결함 범주가 아니다. 재감사에서 B2·B3가 여전히 열려 있으면 별칭 형태가 아닌 다른 우회 경로가 있다는 뜻이다.
