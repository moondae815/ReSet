# UPDATE 매핑 계약 설계

- 작성일: 2026-08-09
- 상태: 설계 승인, 구현 전
- 선행: `2026-08-08-static-analysis-identity-design.md` §후속 1~3, `2026-08-09-type-classification-policy-design.md` §후속 2

## 배경

원본 대조에서 명세서 세 종류의 결함이 확인됐다. 셋 다 데이터 정합성이 아니라 **프롬프트 계약**의 문제이며, 셋 다 뿌리가 하나다.

`SqlStaticParser`는 `InsertSpecification`을 방문할 때 대상 테이블·타겟 컬럼·소스 쿼리 블록을 뽑아 `AstInsertMappings`에 담는다. `AiService.BuildSpecificationPrompts`는 그것으로 **미리 채운 마크다운 표**를 프롬프트에 박고, AI가 채울 칸을 `(FILL_SOURCE_DATA_HERE)`·`(FILL_DESCRIPTION_HERE)` 둘로 제한한다. 그래서 INSERT는 컬럼을 빠뜨릴 수 없다.

`UpdateSpecification` 방문자는 `RecordDmlTarget`으로 **대상 테이블 이름 하나만** 기록하고 `SetClause`는 보지 않는다. 그 결과 UPDATE에 대해서는 컬럼 매핑도, FROM 절 자기참조 정보도, SET 우변의 자기 컬럼 참조도 프롬프트에 도달하지 않는다.

### 결함 1 — UPDATE 컬럼 매핑표가 없다

`COMM_UPD`의 취소건 음수 전환은 16개 컬럼에 `* -1`을 적용한다. 명세서에는 "금액 및 수수료 관련 컬럼을 `-1`배 처리합니다"라는 산문 한 줄만 남는다. 어느 컬럼이 대상인지, 어느 컬럼이 대상이 아닌지 문서에서 복원할 수 없다.

### 결함 2 — `UPDATE ... FROM` 자기참조 의미가 기술되지 않는다

갱신 대상이 FROM 절에 등장하는 그 별칭 인스턴스라는 사실, 그리고 조인이 대상 행 하나에 여러 소스 행을 매칭시킬 때 **T-SQL이 어느 값을 반영할지 정의하지 않는다**는 사실이 어느 스펙에도 없다.

### 결함 3 — `SET` 절 동시평가가 누락된다

`COMM_UPD` 410-419행의 부가세포함 재계산은 우변을 모두 **갱신 전 값**으로 평가한다. 이 규약 없이 절차형 언어로 옮기면 순차 대입이 되어 **금액이 틀린다.** 세 결함 중 유일하게 산출물의 계산 결과를 바꾼다.

### 왜 검증이 잡지 못했는가

14개 명세서가 88~94점으로 전부 검증을 통과하는 동안 이 셋이 하나도 걸리지 않았다. L2 Critic의 5대 기준에 스키마 주장 사실검증이 없고, L1 기계 검증은 헤더 존재와 Mermaid 문법만 본다. 검증 게이트 자체의 재설계는 별도 후속 과제이며 이 설계의 범위 밖이다 — 다만 이 설계가 UPDATE 컬럼에 대해 **AI 판단에 의존하지 않는 기계적 대조**를 하나 추가한다.

## 목표와 범위

**목표**: 파서가 SET 절을 추출하고, 그것이 프롬프트에서 fill-in-the-blank 표로 강제되며, 누락이 L1 기계 검증에 걸린다.

**범위 안**

- `SqlStaticParser`의 `UpdateSpecification` 처리 확장
- `SpDefinition`의 정적 분석 모델 확장, `StaticAnalysisNormalizer`의 대칭 처리
- `AiService.BuildSpecificationPrompts`의 정적 분석 블록과 규칙 목록
- `MechanicalValidator.Validate`의 선택적 기대값 대조
- `CacheManager.CurrentCacheFormatVersion` 상승

**범위 밖**

- 마이그레이션 계획서·Step 지시서의 공통 규약. 그쪽은 `Spec.md`를 읽으므로 명세서가 정확해지면 따라온다. 규약 문구를 codegen 프롬프트에도 심는 것은 별도 사안이다.
- INSERT 템플릿의 문장 서수 부재. 같은 테이블에 INSERT가 둘이면 `### INSERT 대상 테이블: X` 제목이 중복되는데, 지금 오작동을 만들고 있지 않으므로 건드리지 않는다.
- L2 Critic 기준 재설계 및 명세서 재발 방지 게이트.
- 함수(`CodeObjectType.Function`) 경로. `BuildFunctionSpecificationPrompts`는 별도 분기이며 UPDATE를 다루지 않는다.

## 설계

### 1. 파서가 SET 절을 본다

`SpDefinition.cs`에 INSERT와 대칭인 모델을 추가한다.

```csharp
public class AstUpdateMapping
{
    public string TargetTable { get; set; } = string.Empty;
    public int StatementOrdinal { get; set; }
    public List<AstUpdateAssignment> Assignments { get; set; } = new();
    public string? FromClauseText { get; set; }
    public List<string> SelfReferencedColumns { get; set; } = new();
}

public class AstUpdateAssignment
{
    public string Column { get; set; } = string.Empty;
    public string SourceExpression { get; set; } = string.Empty;
}
```

`SpStaticAnalysisResult`에 `List<AstUpdateMapping> AstUpdateMappings`를 더한다.

`ExplicitVisit(UpdateSpecification)`은 기존 `RecordDmlTarget` 호출을 그대로 두고, 그 뒤에 매핑을 만든다.

**대상 해석에 실패한 문장은 매핑을 만들지 않는다.** `RecordDmlTarget`이 `false`를 돌려주면(대상이 `NamedTableReference`가 아니거나 별칭을 못 푼 경우) 그 문장은 건너뛴다. 잘못 푼 테이블 이름에 컬럼을 붙이면 L1이 존재하지 않는 표를 요구하게 되고, 그것은 무한 재시도로 이어진다. **과다 보고는 허용하지만 오귀속은 허용하지 않는다** — 이 방향은 `RecordDmlTarget`의 기존 판단(대상을 잃는 것보다 과다 보고가 낫다)과 반대 방향처럼 보이지만 같은 원칙이다. 거기서는 못 푼 대상을 문맥 전체 수집으로 **넓게** 되돌리고, 여기서는 못 푼 대상에 대해 검사를 **걸지 않는다**. 둘 다 틀린 단언을 만들지 않는 쪽이다.

`StatementOrdinal`은 이 SP 안에서 같은 `TargetTable`에 대한 몇 번째 UPDATE 문장인지를 1부터 센다. 같은 테이블을 분기별로 여러 번 갱신하는 SP에서 표가 뭉개지지 않게 하기 위한 것이다.

**SET 절 처리**

- `AssignmentSetClause`: `Column`이 `null`이면 `SET @var = ...` 변수 대입이므로 건너뛴다. 컬럼이 아니다.
- `FunctionCallSetClause`(`.WRITE()`): 컬럼만 뽑고 표현식은 원문 그대로 담는다.
- 좌변이 한정된 경우(`A.COMM`) 마지막 파트만 `Column`에 담는다. 우변은 원문을 손대지 않는다.
- `SourceExpression`은 `GetFragmentText`로 토큰 스트림에서 그대로 뽑는다. INSERT의 `SourceQueryBlock`이 쓰는 것과 같은 함수다.

**FROM 절**: `node.FromClause`가 있으면 `GetFragmentText`로 원문을 담는다. 없으면 `null`이며, 결함 2의 경고는 그 문장에 붙지 않는다.

**자기참조 감지**: 각 `SourceExpression` 안의 `ColumnReferenceExpression`을 모아(마지막 파트로 정규화) 그 문장의 좌변 컬럼 집합과 교집합을 취한다. 결과가 `SelfReferencedColumns`다. 교집합이 비면 결함 3의 경고는 그 문장에 붙지 않는다.

이 판정은 **한 문장 안에서만** 한다. 전역 컬럼 사전을 쓰면 다른 문장이 갱신하는 동명 컬럼이 섞여 오탐이 난다. `RecordDmlTarget`이 전역 별칭 사전을 쓰지 않는 것과 같은 이유다.

### 2. 정규화기는 이름만 다룬다

`StaticAnalysisNormalizer.Normalize`가 `AstUpdateMappings`를 복사하며 `TargetTable`만 canonical 3-part로 바꾼다. `Assignments`·`FromClauseText`·`SelfReferencedColumns`는 그대로 옮긴다.

컬럼명과 표현식을 건드리지 않는 것은 그 클래스의 기존 계약이다 — "AST도 DB도 보지 않는다. 이름만 다룬다." 표현식을 정규화하려 들면 SQL 재작성이 되고, 그것은 이 클래스가 하지 않기로 한 일이다.

### 3. 프롬프트가 표를 미리 채운다

**정적 분석 블록** — `BuildPromptContextSections`의 `UPDATE 대상 테이블` 줄 뒤에 붙인다. INSERT 블록과 같은 형태다.

```
[AST UPDATE 타겟-소스 1:1 매핑 추출 데이터 (ABSOLUTE SOURCE OF TRUTH)]
* L1 정적 파서(SqlScriptDom)가 SET 절의 타겟 컬럼과 원천 표현식을 기계적으로 정확히 추출했습니다.
* 아래 정보를 매핑 원천으로 절대적으로 신뢰하고 반영하십시오. 원본 쿼리에 없는 변환이나
  추가 논리를 임의로 지어내지(할루시네이션) 마십시오.
  <update-target table="SETTLE_POQ_DB.dbo.TCommMst" statement="2">
    <set column="CLVT">CLVT * -1</set>
    <set column="PGVT">PGVT * -1</set>
    <from-clause>FROM TCommMst A INNER JOIN #TMP B ON A.SEQ = B.SEQ</from-clause>
    <self-referenced-columns>CLVT, PGVT</self-referenced-columns>
  </update-target>
```

`<from-clause>`와 `<self-referenced-columns>`는 해당 문장에서 감지됐을 때만 나온다.

**규칙 템플릿** (`AiService.BuildUpdateMappingTemplate`) — INSERT 템플릿 바로 뒤에 넣는다. **원천 표현식까지 채워져 있고 AI가 채울 칸은 `설명` 하나뿐이다.**

```
### UPDATE 대상 테이블: SETTLE_POQ_DB.dbo.TCommMst (문장 2)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| SETTLE_POQ_DB.dbo.TCommMst | CLVT | CLVT * -1 | (FILL_DESCRIPTION_HERE) |
```

INSERT 템플릿과 달리 컬럼이 없는 폴백 행(`(COLUMN_NAME)`)을 두지 않는다. SET 절이 없는 UPDATE는 존재하지 않으므로, `Assignments`가 비면 그 매핑 자체를 만들지 않는다.

**표 셀에 넣기 전에 원천 표현식의 파이프를 이스케이프한다.** SET 우변에 비트 OR가 오면(`SET FLAGS = FLAGS | 4`) 그 문자가 셀 구분자로 읽혀 표 전체가 어긋나고, 그 표를 그대로 베낀 명세서는 L1 대조에서 컬럼을 찾지 못한다. 개행도 같은 이유로 공백으로 접는다. 파서가 표현식 원문을 보존한다는 계약은 유지된다 — 이스케이프는 표에 쓸 때만 하고 `AstUpdateAssignment.SourceExpression` 자체는 손대지 않는다.

**조건부 경고** — `hasUdf`·`hasLinkedServers`·`hasDynamicSql`가 이미 쓰는 동적 Pruning과 같은 자리, 같은 방식이다. 감지된 문장에만 붙는다.

FROM 절이 있는 문장(결함 2):

> 갱신 대상은 FROM 절에 등장하는 해당 별칭의 인스턴스입니다. 조인이 대상 행 하나에 여러 소스 행을
> 매칭시킬 경우 T-SQL은 어느 값이 반영될지 정의하지 않습니다(비결정적). 조인 키의 유일성이 보장되는지
> 판단할 수 없으면 "보장되지 않으면 결과가 비결정적"이라는 사실만 기술하고, 유일성 여부를 추측하지 마십시오.

자기참조가 감지된 문장(결함 3):

> 다음 컬럼은 SET 우변에서 자기 자신을 참조합니다: {목록}. SQL의 SET 절은 우변을 모두 **갱신 전 값**으로
> 동시에 평가합니다. 절차형 언어로 이행할 때 순차 대입하면 계산 결과가 달라지므로, 이 사실을 CRUD 분석에
> 명시적으로 기술하십시오.

### 4. L1이 기계적으로 대조한다

`MechanicalValidator`에 선택적 기대값을 받는 오버로드를 둔다.

```csharp
public sealed record SpecExpectations(IReadOnlyList<UpdateColumnExpectation> UpdateColumns);
public sealed record UpdateColumnExpectation(string Table, IReadOnlyList<string> Columns);

public ValidationResult Validate(string markdown, SpecExpectations? expectations = null)
```

`null`이면 종전 동작 그대로다. 이 형태를 고른 이유는 대안이 더 나빠서다. 별도 검사기 클래스로 분리하면 `ValidationResult`·`DetailedError`·`RegenerationScope.FromL1Errors`가 모두 `MechanicalValidator`의 산출을 전제하고 있어 두 결과를 합치는 지점이 호출부마다 생긴다. 한 곳이라도 빠뜨리면 그 경로에서만 검사가 조용히 빠진다 — 이 저장소가 반복해서 겪은 실패 양식이다. 대조 로직 자체는 private 메서드 하나(`CheckUpdateMappings`)에 격리되므로 테스트 가능성은 분리안과 동등하다.

**기대값 생성** — `SpecExpectations.FromStaticAnalysis(SpStaticAnalysisResult)` 정적 팩토리가 `AstUpdateMappings`를 테이블 단위로 접어 만든다. 같은 테이블의 여러 문장은 컬럼 합집합이 된다(대조가 테이블 단위 합집합이므로 기대도 같은 단위여야 한다). 매핑이 없으면 `null`을 돌려주고, 호출부는 그대로 넘긴다 — `null` 검사를 호출부마다 쓰지 않기 위해서다.

**호출부**

- `VerificationPipelineOrchestrator`의 6개 호출부는 모두 `spDef`가 스코프에 있다. 기대값을 넘긴다.
- `SpecificationLinker`는 참조 섹션을 덧붙인 뒤 정화 목적으로만 부르고 `IsValid`를 보지 않는다. 기본값(`null`)으로 둔다.

**대조 절차**

1. 정화본(`PostProcessMarkdown` 결과)의 `## CRUD 분석` 섹션 본문을 잡는다.
2. `### UPDATE 대상 테이블:` 로 시작하는 H3를 찾아 **테이블별로 구간을 합집합으로** 모은다. 제목에서 접두를 걷어낸 나머지의 첫 공백 앞까지를 테이블명으로 읽는다 — 프롬프트가 요구하는 `(문장 N)` 꼬리와 AI가 덧붙일 수 있는 부연을 함께 떨어낸다. 구간의 끝은 다음 H3 또는 H2다.
3. 기대 테이블에 해당하는 구간이 하나도 없으면 오류로 보고한다.
4. 구간이 있으면, 기대 컬럼 중 그 텍스트에 단어 경계 기준으로 등장하지 않는 것을 누락으로 보고한다.

**문장 서수까지 대조하지 않는다.** 프롬프트는 문장별 표를 요구하지만, AI가 표를 합쳐 썼다는 이유로 재생성을 강요하면 내용이 옳은데도 루프가 돈다. L1은 형식 검증이고, 과잉 엄격은 무한 재시도를 만든다. 잡아야 할 것은 **누락**이다.

**테이블 이름은 마지막 파트로 비교한다.** 프롬프트는 canonical 3-part를 요구하지만 AI가 짧게 쓰는 것은 결함이 아니다. 컬럼은 단어 경계(`\b`)로 봐서 `CLVT`가 `CLVTOTAL`에 걸리지 않게 한다. 대괄호 한정(`[CLVT]`)은 단어 경계가 자연히 처리한다.

`ErrorType`에 `UpdateMappingMissing`을 추가한다. `RegenerationScope.FromL1Errors`는 이 타입이 섞이면 `allMermaid`가 거짓이 되어 `Overview + Crud + Logic` 재생성으로 간다. 이미 옳은 동작이므로 그쪽은 수정하지 않는다.

**`BuildSuggestedPromptFix`에는 이 타입의 블록을 반드시 추가한다.** 그 함수는 `HeaderMissing`·`MermaidQuoteMissing`·`MermaidCliError`·`General` 넷만 열거하므로, 새 타입을 추가하고 여기를 손대지 않으면 **L1이 실패해도 재생성 프롬프트에 사유가 실리지 않는다.** `CriticFeedbackLog.ComposeAfterL1Failure`가 받을 재료가 비고, 같은 명세서가 같은 결함으로 재생성을 반복한다. 새 블록은 기존 "기타 에러" 앞에 놓는다 — 기타는 마지막이어야 한다.

### 5. 캐시 무효화

`SpStaticAnalysisResult`에 필드가 늘어나므로 `CacheManager.CurrentCacheFormatVersion`을 2에서 3으로 올린다. 기존 캐시 항목은 버전 불일치로 폐기되고 전체 재분석된다. 저장된 DDL에서 재분석하므로 오프라인 모드에서도 성립한다.

## 오류 처리

**새로 던지는 예외 경로는 없어야 한다. 그 확인은 다음 세 함수를 이름으로 특정해 호출부까지 따라가며 한다.**

- `SqlStaticParser.ExplicitVisit(UpdateSpecification)` — `TSqlFragmentVisitor` 안에서 던지면 `SqlStaticParser.Analyze`의 봉투까지 올라간다. 그 봉투가 무엇을 잡는지 확인하고, 좁으면 넓힌다.
- `AiService.BuildUpdateMappingTemplate` — 프롬프트 조립 중 실패하면 명세서 생성 자체가 불가능해진다.
- `MechanicalValidator.CheckUpdateMappings` — `Validate`의 기존 soft-fail `try/catch` 안에 둔다. 검사기 자체 오류가 툴을 멈추지 않는다는 기존 계약을 그대로 쓴다.

직전 두 브랜치가 연속으로 "예외 탈출 경로를 확인했다"고 적고 실제로는 한 함수만 확인했다. 함수를 특정하지 않은 확인 선언은 다음 사람에게 전부를 확인했다는 인상을 준다.

**검사가 안 도는 경우** — 아래는 모두 정상이며 새로 실패하지 않는다.

| 상황 | 결과 |
|---|---|
| 파싱 실패 (`IsParsedSuccessfully == false`) | 매핑 없음 → 기대 없음 → 종전 검증 |
| UPDATE 대상 해석 실패 | 그 문장만 매핑 생략 → 그 테이블 기대 없음 |
| UPDATE가 없는 SP | 매핑 없음 → 종전 검증 |
| 함수(`CodeObjectType.Function`) | 별도 프롬프트 분기이므로 무관 |

## 테스트

**파서** (`SqlStaticParserTests`)

| 케이스 | 기대 |
|---|---|
| 단순 `SET A = 1, B = @v` | 컬럼 2개, 표현식 원문 보존 |
| 한정 좌변 `SET T.COMM = 0` | `Column`이 `COMM` |
| `SET @var = 1` 혼재 | 변수 대입은 제외, 컬럼 대입만 수집 |
| `UPDATE ... FROM` | `FromClauseText`가 원문, 대상은 별칭이 풀린 이름 |
| FROM 없음 | `FromClauseText == null` |
| `SET A = A * -1` | `SelfReferencedColumns`에 `A` |
| `SET A = B * -1` (B는 이 문장의 타겟 아님) | `SelfReferencedColumns` 비어 있음 |
| 같은 테이블 UPDATE 2회 | `StatementOrdinal`이 1, 2 |
| 대상이 테이블 변수/미해결 | 매핑 생성 안 됨 |

**정규화기** (`StaticAnalysisNormalizerTests`): `TargetTable`이 canonical 3-part가 되고 `Assignments`·`SelfReferencedColumns`는 바이트 단위로 불변인지.

**프롬프트** (`AiServiceTests` 또는 그에 준하는 지점): 매핑이 있으면 표가 나오고, `FromClauseText`가 있을 때만 결함 2 경고가, `SelfReferencedColumns`가 있을 때만 결함 3 경고가 붙는지. 매핑이 없으면 블록 전체가 나오지 않는지.

**L1 대조** (`MechanicalValidatorTests`)

| 케이스 | 기대 |
|---|---|
| 기대 컬럼 전부 존재 | 통과 |
| 컬럼 하나 누락 | `UpdateMappingMissing` 오류, 메시지에 누락 컬럼명 |
| `### UPDATE 대상 테이블` 자체 부재 | 오류 |
| `expectations == null` | 종전 동작과 동일 |
| 기대 `CLVT`, 본문에 `CLVTOTAL`만 | 누락으로 보고 (부분 일치 오탐 방지) |
| 제목이 짧은 테이블명 | 통과 (마지막 파트 비교) |
| 같은 테이블 표 2개로 분리 | 합집합으로 통과 |

**뮤테이션 저항 확인** — 위 테스트를 작성한 뒤, 각 가드를 실제로 지우고 테스트가 깨지는지 확인한 다음 복원한다. 구현 계획의 명시적 단계로 둔다. 최소한 다음 넷을 확인한다: 변수 대입 제외 조건, 대상 미해결 시 매핑 생략, 단어 경계 매칭, 자기참조 교집합 판정.

직전 브랜치에서 테스트 8개가 가드를 지워도 전부 통과했다. 그 브랜치가 닫으려던 결함(빈 배열이 검사 루프를 0회 돌게 해 "에러 0개"를 출력하던 것)과 정확히 같은 모양이었다. 처방은 매번 같다.

## 문서 동기화

- `docs/architecture.md`의 정적 분석 절에 UPDATE SET 절 추출과 자기참조 감지 추가
- `AGENTS.md`에 UPDATE 매핑은 파서가 확정하며 프롬프트의 fill-in-the-blank 표를 지우면 안 된다는 규칙. 지웠을 때의 증상이 "명세서가 산문으로 뭉개지고 검증은 통과함"이라 코드만 봐서는 이유를 알 수 없다

`README.md`는 손대지 않는다. 캐시 포맷 버전을 올리면 전체 재분석된다는 설명이 이미 있다(37행, 선행 브랜치가 넣었다).

## 완료 기준

- `dotnet clean && dotnet build`에서 오류 0건, 경고 정확히 8건 (기존 `DbMetadataServiceTests`의 CS8600/CS8602 — 현재 기준선 유지)
- `dotnet test`가 기존 1,211건 + 신규분 전부 통과 (기준선은 `dotnet test --list-tests` 실측치, 2026-08-09 기준). 실측치가 이보다 적으면 의도치 않은 테스트 삭제가 있었다는 뜻이다
- 뮤테이션 저항 확인 4건 완료
- `docs/architecture.md`와 `AGENTS.md` 동기화 완료

## 사람이 직접 확인해야 하는 것

이 설계의 테스트는 전부 단위 수준이다. 실제 AI 응답으로 명세서를 끝까지 생성한 검증은 포함되지 않는다.

1. 실제 Job 1회 — `COMM_UPD`의 명세서에 16개 컬럼이 실제로 표로 나오는지, `* -1`이 글자 그대로 남는지
2. 자기참조가 감지된 문장에서 동시평가 규약이 명세서 본문에 실리는지
3. L1 대조가 걸렸을 때 재생성이 실제로 그 누락을 해소하는지 — 해소하지 못하고 같은 오류로 재시도만 반복하면 대조가 너무 엄격한 것이다
