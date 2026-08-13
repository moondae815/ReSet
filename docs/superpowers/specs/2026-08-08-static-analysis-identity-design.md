# 정적 분석 식별자 정합성 복구 설계

- 작성일: 2026-08-08
- 상태: 설계 승인됨 (구현 계획 수립 전)
- 선행: [2026-08-01 크로스 데이터베이스 분석 활성화](2026-08-01-cross-database-analysis-design.md)

## 배경

`output/Procedures` 아래 14개 SP 명세서를 원본 DDL과 1:1 대조한 결과, 흐름·오류코드·트랜잭션 경계는 충실하지만 **스키마 정보가 계통적으로 오염**되어 있다. 오염이 정확히 통합 배치 전환 설계서의 핵심 입력(청크 키·Shadow 대상·컬럼 매핑)을 때린다.

### 확인된 결함과 근본 원인

**① 실존 컬럼을 "존재하지 않음"으로 단정한다.**

`COMM_UPD/Spec.md:217-235`가 TSettleMst 15개 컬럼을, `AcqManual/Spec.md:68`이 23개 컬럼을, `PG_Client_CMRate_Ins/Spec.md:71`이 `TClient.CompanySalesType`·`ExtraSettleFlag`를 "스키마 불일치"로 기록한다. 실제 DDL 대조 결과 **전부 존재한다**(`offline_snapshot.json` 기준 TSettleMst 59컬럼, TClient 79컬럼).

원인은 `AiService.cs:51-61`이다.

```csharp
if (kvp.Key.Contains(dep.Name, StringComparison.OrdinalIgnoreCase))
{
    foreach (var c in kvp.Value) keepCols.Add(c);
    break;          // 첫 매치에서 중단
}
```

AST는 `TSettleMst`를 두 키로 나눠 담는다 — `SETTLE_POQ_DB.dbo.TSettleMst`(SELECT 측)와 `TSettleMst`(INSERT 대상 컬럼 목록). `break` 때문에 앞 키만 채택되고, INSERT 목록에만 등장하는 `CYMD`·`INSTATE`·`OUTSTATE`·`NonSettleAmt`가 84행 필터에서 탈락한다. 프롬프트의 스키마 표에서 사라지니 AI가 성실하게 "불일치"로 적는다.

`Contains`에 의한 부분 매칭도 잠재 결함이다. `dep.Name = "TSettleMst"`는 `TSettleMstBackup` 키에도 매칭된다.

**② 같은 물리 테이블이 3개로 쪼개진다.**

`EXCEPTION_PROC/Spec.md`가 `SETTLE_POQ_DB.dbo.TSettleMst`(48행)·`dbo.TSettleMst`(51행)·`TSettleMst`(55행)를 CRUD 표에서 별개 행으로 나열한다. `EXPECT_PROC/Spec.md:158`은 "실제 갱신 대상은 `dbo.TSettleMst`와 `SETTLE_POQ_DB.dbo.TSettleMst`입니다"라고 단정한다.

AI의 자유 서술이 아니다. 프롬프트의 "진실의 원천" 블록(`AiService.cs:233-240`)이 세 줄로 알려준다.

**③ FROM 절 조인 원본이 DML 대상으로 분류된다.**

`_statementContext`가 `UPDATE`/`DELETE`로 눌린 동안 방문되는 모든 `NamedTableReference`가 대상 목록에 들어간다(`SqlStaticParser.cs:405,408`). 실측:

```
EXCEPTION_PROC UpdateTables = [SETTLE_POQ_DB.dbo.TSettleMst, TSettleMst, dbo.TSettleMst,
                               TClientSettleRate, TPGCMRate, TClientCMRate,
                               PaymentDB.dbo.TVAccountTxMst, PLCardDB.dbo.TPLCardTxMst, ...]  (11개)
EXPECT_PROC    ReferencedTables = [..., dbo.TSettleMst, SETTLE_POQ_DB.dbo.TSettleMst, 'A']
4PLCARD        DeleteTables = ['A', 'TSettleMst', 'TPGProperty']
```

`'A'`는 `UPDATE A SET ...`(EXPECT_PROC 2-6절)·`DELETE A FROM ...`(4PLCARD)의 대상 별칭이 미해석된 채 테이블로 등록된 것이다. INSERT는 이미 `InsertSpecification.Target`을 붙잡는 올바른 패턴을 갖고 있으나(`SqlStaticParser.cs:258-260`) UPDATE/DELETE에는 대응물이 없다.

**④ 테이블 반환 함수(TVF)의 DDL이 주입되지 않는다.**

`EXPECT_PROC/Spec.md:191`이 `UDF definition not provided; detailed logic excluded from analysis`로 끝난다. `UIF_SettleYMD`는 정산일(`OutYMD`)을 산출하는 이 배치의 핵심 함수이고, `EXPECT_PROC`의 `-10`/`-11`/`-12`/`-13`/`-17` 다섯 단계가 여기 의존한다.

원인은 `DbMetadataService.cs:828`이다.

```csharp
if (rawDep.Type.Contains("TABLE") || rawDep.Type.Contains("VIEW"))
```

`SQL_TABLE_VALUED_FUNCTION`이 `"TABLE"`에 걸려 테이블 취급되고, `ReferencedDdlText`를 아예 가져오지 않는다. 증거는 `prompt-context.md:232`의 `### 테이블: dbo.UIF_SettleYMD (SQL_TABLE_VALUED_FUNCTION)` 렌더링과 `440행`의 `[DDL 소스코드 수집 실패 / 미제공]`이다. 형제 경로인 `IsTableOrViewType`(756행)은 `!IsCodeObjectType(...)` 가드를 이미 갖고 있다 — **한쪽만 고쳐져 있다.**

### 왜 배치 설계에 치명적인가

통합 배치 계획 생성기는 `Spec.md` 본문만 입력으로 받는다(`AiService.AppendSharedStepContext`, 2215-2260행). DDL은 받지 않는다. 그런데 `ConsolidatedPlanRules` 12번은 "청크 키를 쓰기 전에 타깃 테이블 DDL을 교차 확인하라"고 요구한다. **그 DDL 역할을 하는 것이 바로 이 오염된 표다.**

게다가 최종 지시서 번들은 필터 없는 `MetadataExporter.FormatTableSchemaToMarkdown`으로 전체 컬럼을 붙인다(`InstructionBundleWriter.cs:501`). 같은 번들 안에서 계획서와 첨부 DDL이 서로 모순된다.

## 목표와 범위

정적 분석이 만들어 내는 테이블 식별자와 스키마 주입을 사실과 일치시킨다.

**범위 안**

- UPDATE/DELETE 대상 테이블 해석 (별칭 포함)
- 테이블 식별자 정규화와 중복 제거
- `AiService`의 컬럼 필터 매칭 정정
- TVF의 코드 객체 분류 정정
- 온라인·오프라인 양쪽 경로 적용
- 캐시 무효화

**범위 밖**

- **스펙 프롬프트 계약 강화.** UPDATE 컬럼 매핑표 강제, `UPDATE ... FROM` 자기참조 의미 기술, `SET` 절 동시평가 명시는 별도 설계로 분리한다. 이번 변경은 AI에게 주는 *데이터*를 고치고, 그 데이터로 *무엇을 쓰라고 요구하는가*는 건드리지 않는다.
- **검증 게이트.** L1/L2에 "스키마 부재 주장 사실검증"을 추가하지 않는다. 재발 방지는 별도 설계.
- **`ReferencedFunctions` 정규화.** 같은 종류의 문제지만 이번 검토에서 실제 결함이 나오지 않았다.
- **외부 DB UDF 수집.** `SETTLE_CARD_DB`의 5개 UDF 공백은 코드 문제가 아니라 `AllowExternalDatabaseConnections` 설정 선택이다(이미 구현됨, 기본값 `false`).
- **스냅샷 포맷 확장.** 호환성 수준 필드를 추가해도 지금 갖고 있는 스냅샷에는 없으므로 오늘의 검증에 도움이 안 된다.

## 설계

### 1. 책임 분리

네 개의 독립 단위로 나눈다. 서로의 내부를 몰라도 되고 각각 따로 테스트된다.

| 단위 | 책임 | 위치 |
|---|---|---|
| `SqlStaticParser` | 무엇이 DML 대상인가 (의미) | 기존 수정 |
| `StaticAnalysisNormalizer` | 어떻게 부르는가 (표기) | 신규 |
| 정의 조립부 | 정규화기를 언제 부르는가 | 기존 수정 2곳 |
| 소비자 | canonical 데이터를 정확 비교로 쓴다 | 기존 수정 2곳 |

정규화기를 파서에 합치지 않는 이유: 표기 통일에는 현재 DB 이름이 필요한데 파서는 그것을 모른다(`Analyze(ddlText, compatLevel, tableColumnsMap)`). 넘겨줄 수는 있으나 파서 공개 계약이 바뀌고 `SqlStaticParserTests.cs:307-322`의 3-키 분리 단언을 재작성해야 한다. 파서가 "쓰인 대로" 보고하는 성질은 그대로 둔다.

### 2. 파서 수정 — DML 대상 해석

`UpdateSpecification` / `DeleteSpecification`을 방문할 때 `Target`을 먼저 붙잡는다. INSERT가 이미 하는 것과 대칭이다.

- `Target`이 단일 식별자이고 그 문장의 별칭 사전에 있으면 실제 테이블로 해석한다
- **해석된 대상 하나만** `UpdateTables` / `DeleteTables`에 넣는다
- 문장 안의 나머지 `NamedTableReference`는 `SelectTables`로 간다 (읽기 원본이므로)
- 대상 테이블이 FROM 절에도 나타나면(`UPDATE T ... FROM T A` 패턴) 양쪽 목록에 모두 들어간다 — 실제로 읽고 쓰므로 그것이 사실이다

별칭 `'A'` 누출은 이 수정으로 함께 사라진다. 별도 필터가 필요 없다.

### 3. `StaticAnalysisNormalizer` — 표기 통일

`SpStaticAnalysisResult`와 `(database, defaultSchema)`를 받아 정리본을 돌려주는 순수 함수다. AST도 DB도 보지 않는다.

**정규 형식**은 `{Database}.{Schema}.{Name}`이다. 대괄호를 벗기고 대소문자 무시로 비교하되, 표시는 첫 등장 표기를 쓴다.

- `Database` 누락 → 분석 대상 객체의 DB (`spDef.ObjectKey.Database`)
- `Schema` 누락 → 분석 대상 객체의 스키마 (`spDef.Schema`)

두 번째 규칙은 SQL Server의 실제 해석 규칙(사용자 기본 스키마 → `dbo`)과 정확히 같지 않다. 이 코드베이스는 전부 `dbo`이므로 실질 차이가 없고, `dbo`를 상수로 박는 것보다 낫다는 판단이다. **이 가정은 여기 명시된 것이 전부이며, 비-`dbo` 스키마를 쓰는 환경으로 확장할 때 재검토해야 한다.**

**베이스 이름만으로 병합하면 안 된다.** `UP_UTIL_SETTLE_INS_EXTRA4PLCARD`는 `dbo.TPGProperty`(= SETTLE_POQ_DB)와 `PaymentDB.dbo.TPGProperty`를 둘 다 의존성으로 갖는다. 컬럼 구성이 동일해서 더 위험하다. 3-part 전체가 같아야만 같은 테이블이다.

| 대상 | 처리 |
|---|---|
| `ReferencedTables`, `SelectTables`, `InsertTables`, `UpdateTables`, `DeleteTables` | canonical 변환 후 중복 제거 |
| `AstInsertMappings[].TargetTable` | canonical 변환 |
| `ReferencedColumnsPerTable` 키 | canonical 변환, 충돌 시 컬럼 합집합 |
| 임시 테이블(`#t`, `##g`), 테이블 변수(`@t`), 4-part 링크드 서버 이름 | 그대로 통과 |
| `CreatedTempTables`, `LinkedServerReferences`, `ReferencedFunctions` | 손대지 않음 |

컬럼 합집합은 **첫 등장 순서를 보존한다.** 프롬프트가 이 목록을 "축약 없이 기술하라"는 진실의 원천으로 쓰고(`AiService.cs:235`), INSERT 매핑표의 행 순서에도 영향을 주기 때문이다.

### 4. 적용 지점

**온라인** — `DbMetadataService`, 정밀 `Analyze()` 직후(604행)

```
의존성 수집 [TVF 분류 수정 반영]
  → Analyze(ddl, compat)            (1차, 515행)
  → tableColumnsMap 구성
  → Analyze(ddl, compat, map)       (정밀, 604행)
  → Normalize(...)                  ← 신규
  → SpDefinition.StaticAnalysis
```

이후 `metadata.json`과 스냅샷에 canonical 표기로 저장된다.

**오프라인** — `OfflineDbMetadataService.GetDirectDefinitionAsync`

스냅샷은 `StaticAnalysis`를 **결과물로** 저장하고 파서를 다시 돌리지 않는다(`OfflineDbMetadataService.cs:106-142`). 정규화기만 걸면 표기는 통일되지만 §2의 파서 수정이 반영되지 않는다 — 저장된 `UpdateTables`는 옛 파서가 만든 11개짜리 목록 그대로다.

스냅샷은 `DdlText`를 온전히 갖고 있다(EXPECT_PROC 기준 11,190자). 따라서:

```
스냅샷에서 역직렬화
  → ObjectKey 설정, RawPromptContext null, Dependencies 필터   [기존]
  → CodeObjects에서 코드 객체 DDL 재링크                        ← 신규
  → 저장된 DdlText로 Analyze() 재실행                           ← 신규
  → Normalize(...)                                              ← 신규
```

**저장된 파생 분석을 신뢰하지 않고 저장된 원본에서 다시 계산한다.** 스냅샷은 *데이터베이스*의 스냅샷이지 *분석 결과*의 스냅샷이 아니다. 이렇게 하면 앞으로 파서를 고칠 때마다 스냅샷 재추출을 요구하지 않아도 된다.

감수할 점: 스냅샷에 호환성 수준이 없어 오프라인 재파싱은 `Analyze`의 기본값 160을 쓴다. 이 SP들의 구문 범위에서는 차이가 없다.

TVF DDL 재링크가 가능한 이유는 스냅샷의 `CodeObjects`에 `SETTLE_POQ_DB.dbo.UIF_SettleYMD.Function`이 이미 들어 있기 때문이다. 의존성 항목의 `ReferencedDdlText`만 비어 있다(`ddl_len=0`, `cols=1`).

### 5. 소비자 정리

**`AiService.FormatTableSchemaToMarkdown`** (51-61행) — substring `Contains`를 canonical 정확 비교로 바꾸고 `break`를 없앤다.

§3을 거치면 테이블당 키가 하나이므로 정상 경로에서는 루프가 한 번만 매칭된다. `break`를 없애는 것은 §7의 폴백 대비다 — `ObjectKey`가 null이라 한정을 못 하면 `TSettleMst`와 `SETTLE_POQ_DB.dbo.TSettleMst`가 분리된 채 남고, 그때도 컬럼이 유실되지 않아야 한다.

**`AiService.BuildSpMetadataTexts`** (128행) — `<dependencies>` 블록이 `- Schema: dbo, Name: TTxMst` 형태로 DB를 찍지 않는다. 그래서 `PaymentDB.dbo.TTxMst`와 `dbo.TTxMst`가 프롬프트에서 구별되지 않는다. 바로 아래 `<referenced-table-schemas>`는 `[PaymentDB].[dbo].[TTxMst]`로 찍고 있어 표기도 어긋난다. 같이 맞춘다.

**`DbMetadataService.cs:828`** — `rawDep.Type.Contains("TABLE")` 분기에 형제 경로가 이미 쓰는 `IsCodeObjectType` 가드를 붙인다.

### 6. 캐시 무효화

`CacheManager.CurrentCacheFormatVersion`을 `1` → `2`로 올린다. 이미 있는 장치이고(`CacheManager.cs:90-95`) 불일치 시 미스 처리 후 정상 재분석·재저장된다.

복합 해시는 DDL만 본다(`ComputeCompositeHash`). 원본 SP가 그대로면 고친 코드가 무의미하므로 이 조치 없이는 변경 전체가 무효다.

### 7. 실패 처리

PRD §4.2의 소프트 페일 원칙을 따른다. **이번 수정 중 어느 것도 새로운 예외 경로를 만들지 않는다.**

| 상황 | 동작 |
|---|---|
| `SpDefinition.ObjectKey`가 null이라 DB 컨텍스트 없음 | 한정 없이 통과 (대괄호 제거·정확 중복 제거만). 이름을 지어내지 않는다 |
| 정규화기가 모르는 형태 (임시 테이블, 4-part, 테이블 변수) | 그대로 통과 |
| 파서가 `Target`을 해석 못 함 (변수, TVF 등) | **해당 문장에 한해** 기존 동작으로 폴백 — 문맥 내 모든 테이블 수집. 대상을 통째로 잃는 것보다 과다 보고가 낫다 |
| 오프라인 재파싱 실패 | 저장된 `StaticAnalysis`로 폴백 후 정규화만 적용. 오프라인 모드가 현재보다 나빠지지 않는다 |
| 스냅샷 `CodeObjects`에 재링크할 DDL 없음 | 비워 둔다 (현행 동작) |
| TVF의 `GetObjectDdlAsync` 실패 | 기존 try/catch가 경고 누적 (변경 없음) |

세 번째 줄이 유일하게 조용한 지점이다. 다만 그 폴백은 현재 동작과 동일하므로 회귀가 아니라 개선의 부재다.

## 테스트

TDD로 진행한다. 각 단위마다 실패하는 테스트를 먼저 쓴다.

`output/`은 `.gitignore` 대상이라 추적되지 않는다. 테스트는 산출물에 의존할 수 없고, 기존 파서 테스트처럼 인라인 DDL 픽스처를 쓴다.

**`StaticAnalysisNormalizerTests`** (신규)
- `TSettleMst` / `dbo.TSettleMst` / `SETTLE_POQ_DB.dbo.TSettleMst` → 1개로 병합
- `dbo.TPGProperty`와 `PaymentDB.dbo.TPGProperty` → 분리 유지 (베이스 이름 병합 금지 방어)
- 컬럼 합집합의 첫 등장 순서 보존
- 임시 테이블·4-part·테이블 변수 통과
- `ObjectKey` null일 때 한정 생략

**`SqlStaticParserTests`** (추가)
- `UPDATE A SET ... FROM T A, S B` → `UpdateTables = [T]`, `SelectTables ⊇ [T, S]`
- `DELETE A FROM T A INNER JOIN S` → `DeleteTables = [T]`
- `UPDATE T ... FROM T A` → 대상과 원본 양쪽에 등장
- 해석 불가 대상 → 폴백 동작

기존 3-키 분리 단언(`SqlStaticParserTests.cs:307-322`)은 손대지 않는다. 파서 계약이 안 바뀌었다는 증거로 남긴다.

**`AiServiceTests`** (추가)
- canonical 키 병합으로 INSERT 전용 컬럼이 스키마 표에 살아남는지
- `TSettleMst`가 `TSettleMstBackup`과 교차 매칭되지 않는지 (substring 버그 정면 검증)
- `<dependencies>` 블록에 DB가 찍히는지

**`DbMetadataServiceTests`** (추가) — `SQL_TABLE_VALUED_FUNCTION`이 코드 객체로 분류되어 컬럼이 아니라 DDL을 요청하는지

**`OfflineDbMetadataServiceTests`** (추가) — 재파싱 적용, `CodeObjects` 재링크, 파싱 실패 시 폴백

**`CacheManagerTests`** (추가) — 저장된 `FormatVersion = 1` 항목이 미스 처리되는지

### 수동 검증 체크리스트

단위 테스트로는 "14개 문서가 실제로 좋아졌는가"를 잡을 수 없다. `output/`이 추적되지 않으므로 골든 테스트를 만들지 않고, 지금 있는 `offline_snapshot.json`으로 재분석한 뒤 아래를 확인한다.

| 항목 | 기대 |
|---|---|
| 전 스펙의 "스키마 불일치 / 존재하지 않음" 문구 | 0건 (TSettleMst·TClient·TPGCollectPeriodMst 대상) |
| EXCEPTION_PROC·EXPECT_PROC의 CRUD 표 TSettleMst 행 | 3행 → 1행 |
| EXCEPTION_PROC `UpdateTables` | 11개 → `SETTLE_POQ_DB.dbo.TSettleMst` 1개 |
| EXPECT_PROC `UpdateTables` | 11개(`'A'` 포함) → 1개 |
| 4PLCARD `DeleteTables` | `['A','TSettleMst','TPGProperty']` → 1개 |
| AcqManual `DeleteTables` | 2개 → 1개 |
| EXPECT_PROC의 `UIF_SettleYMD` 기술 | `definition not provided` 문구 소멸, 정산일 산출 로직 기술됨 |
| 오류코드 재현율 | 100% 유지 |

마지막 줄이 기준선이다. 이번 변경이 이미 잘 되던 것을 깨지 않았는지 본다.

## 문서 동기화

- `docs/architecture.md`의 정적 분석 절에 정규화 단계 추가
- `docs/architecture.md`의 오프라인 모드 설명에 "저장된 DDL에서 재분석" 규칙
- `AGENTS.md`에 테이블 식별자는 canonical 3-part로만 비교한다는 규칙
- `README.md`의 캐시 설명에 `FormatVersion` 상승 시 전체 재분석된다는 한 줄

## 완료 기준

- `dotnet clean && dotnet build`에서 오류 0건, 경고 정확히 8건 (기존 `DbMetadataServiceTests`의 CS8600/CS8602 — 현재 기준선 유지)
- `dotnet test`가 기존 1,040건 + 신규분 전부 통과
- 위 문서 4종 동기화 완료
- 수동 검증 체크리스트 8항목 전부 충족

## 후속 (이번 범위 밖)

원본 대조에서 함께 확인됐으나 데이터 정합성 문제가 아니라 프롬프트 계약 문제인 것들이다. 별도 설계로 다룬다.

**네 항목 모두 해소됐다.** 1~3은 [UPDATE 매핑 계약](2026-08-09-update-mapping-contract-design.md)이,
4는 [스키마 주장 검증 게이트](2026-08-09-schema-claim-verification-gate-design.md)가 닫았다.

1. ~~**UPDATE 컬럼 매핑표 부재.** INSERT는 fill-in-the-blank 템플릿으로 1:1 매핑이 강제되지만(`AiService.cs:326`) UPDATE는 아니다. `COMM_UPD`의 취소건 음수전환(16개 컬럼 `* -1`)이 "금액 및 수수료 관련 컬럼을 `-1`배 처리합니다"로만 남는다.~~ **해소됨(2026-08-09).** `BuildUpdateMappingTemplateLines`가 정적 분석의 `AstUpdateMappings`에서 `| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |` 표를 컬럼별 한 행씩 미리 채우고 설명만 `(FILL_DESCRIPTION_HERE)`로 남긴다. INSERT와 같은 fill-in-the-blank 형태다. **다만 비대칭이 방향만 바뀐 채 남았다** — 이 헬퍼는 `BuildSpecificationPrompts`와 `BuildSpecSectionPrompts`의 `"CrudAnalysis"` 분기가 공유하지만, INSERT fill-in 표는 여전히 앞쪽에만 있다(같은 설계 §남은 후속 8).
2. ~~**`UPDATE ... FROM` 자기참조 의미 미기술.** 대상이 FROM의 별칭 인스턴스인지, 다중 매칭 시 어느 값이 반영되는지(T-SQL은 비결정적) 어느 스펙에도 없다.~~ **해소됨(2026-08-09).** `FromClauseText`가 있으면 "갱신 대상은 FROM 절에 등장하는 해당 별칭의 인스턴스", "조인이 대상 행 하나에 여러 소스 행을 매칭시킬 경우 T-SQL은 어느 값이 반영될지 정의하지 않습니다(비결정적)"를 프롬프트가 직접 말한다. 유일성 여부를 추측하지 말라는 금지도 함께 붙는다.
3. ~~**`SET` 절 동시평가 누락.** `COMM_UPD` 410-419행의 부가세포함 재계산은 우변을 모두 갱신 전 값으로 평가한다. 절차형 언어로 그대로 옮기면 금액이 틀린다.~~ **해소됨(2026-08-09).** `SelfReferencedColumns`가 비어 있지 않으면 "SET 절은 우변을 모두 **갱신 전 값**으로 동시에 평가한다"와 "순차 대입하면 계산 결과가 달라진다"를 해당 컬럼 이름과 함께 지시하고, `## CRUD 분석`에 명시적으로 기술하도록 요구한다.
4. ~~**재발 방지 게이트.** 14개 문서가 88~94점으로 전부 검증 통과했는데 위 결함이 하나도 걸리지 않았다. L2 Critic 5대 기준에 스키마 주장 사실검증이 없다.~~ **해소됨(2026-08-10).** L2 Critic 기준을 늘리는 대신 **L1 기계 검증**에 `CheckSchemaClaims`(`ErrorType.SchemaClaimFalse`)를 넣었다 — 5대 기준으로 14개가 통과한 것이 Critic 방식의 실측 결과였기 때문이다. 프롬프트가 제공한 컬럼을 문서가 "존재하지 않는다"고 부정하면 L1에서 떨어진다. `ab6dd5b`가 코드 펜스 안의 예시 SQL을 검사에서 제외해 오탐을 닫았다. 잔여 한계 9건은 그 설계의 §남은 후속에 있다.
