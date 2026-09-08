using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public class CacheManager : ICacheManager
    {
        private static readonly object FileLock = new object();
        private static volatile bool _hasMigrated = false;
        private static readonly object _migrationLock = new object();
        private const string CacheIndexFileName = ".sp_cache_index.json";
        // 2: 정적 분석 식별자 정규화. DDL이 안 바뀌어도 프롬프트에 들어가는 스키마 표와
        //    테이블 목록이 달라지므로, 이전 버전으로 만든 산출물은 전부 다시 만들어야 한다.
        // 3: SpStaticAnalysisResult에 AstUpdateMappings가 추가되어 프롬프트 입력이 달라졌다.
        //    DDL이 같아도 기존 산출물은 UPDATE 매핑표가 없으므로 재분석해야 한다.
        // 4: 집합 술어 수집 범위가 넓어졌다(리터럴 우변 등호·부등호, ISNULL 래핑 좌변,
        //    파생 테이블 내부 술어). 표에 연산·범위 칸이 생겨 프롬프트 입력이 달라졌고,
        //    옛 산출물은 그 칸이 없어 L1을 통과할 수 없으므로 전부 재분석해야 한다.
        //    2026-08-19 축 A 감사에서 이 재료가 없어 새어 나간 대상 행 집합 결함이 4건이었다.
        // 5: 참조 함수 표가 조립기 산출물로 바뀌었고 함수 동작 서술이 금지되었다.
        //    프롬프트 입력과 출력 계약이 둘 다 달라졌으므로 옛 산출물은 재분석해야 한다.
        //    2026-08-20 축 A 교차 대조에서 이 표의 10행 중 8행이 결함이었고 🔴이 5건이었다.
        // 6: UPDATE 절 제목의 문장 번호가 "갱신 0"에서 실제 번호로 고쳐졌고(정규화가
        //    GlobalStatementOrdinal을 유실하고 있었다), 오류 반환 코드 앵커의 줄 번호가
        //    빈 줄만큼 밀리던 것을 바로잡았다. 둘 다 프롬프트 입력이 달라진 것이므로
        //    옛 엔트리를 재사용하면 산출물이 옛 재료 그대로 남는다.
        // 7: 추출기 결함 셋을 닫았다(2026-08-20 축 A 감사). 자기참조 판정이 갱신 대상
        //    별칭을 FROM 절에서 풀고, 집합 술어가 LEFT/RIGHT 같은 전용 노드로 감싼
        //    좌변도 담고, 의존성 이름이 카탈로그 표기로 정규화된다. 셋 다 프롬프트에
        //    실리는 기계 확정 재료라 옛 엔트리를 재사용하면 틀린 재료가 그대로 남는다.
        // 8: 잠금 힌트·객체 선언 표가 새로 실리고 DML 범위 표에 ORDER BY 칸이 붙었다
        //    (2026-08-21 축 A 감사의 🟡 다섯). 프롬프트 입력이 달라졌으므로 옛 엔트리를
        //    재사용하면 산출물이 옛 재료 그대로 남는다.
        // 9: 실행 의미 표(DB 배치·집계 대입·@@ROWCOUNT·커서 수명·식 타입 경로 다섯 종류)와
        //    CASE 분기 표가, 이 버전을 올릴 당시 존재하던 프롬프트 호출부 네 갈래(SP
        //    전체·함수·CrudAnalysis·LogicAndVisualization) 전부에 새로 실렸다 - 이후
        //    Task 17이 다섯 번째 호출부(OverviewAndParameters)를 추가했다(AiService.cs
        //    참고). 요약이 곧 결함이었다(2026-08-22 축 A 감사, UIF_SettleYMD 🟠 3건).
        //    DML 범위 표에는 GROUP BY 칸이 붙었다. 스키마 표 과소 포함도 고쳐져 주석에만
        //    등장하는 컬럼과 별칭 한정 표기(예: X.PRODUCTNAME)가 다시 실린다. 과소
        //    포함이 "그 컬럼은 없다"는 잘못된 서술을 14개 명세서에 남긴 결함이었다
        //    (UP_UTIL_SETTLE_PROC_ETC 실측).
        //    전부 프롬프트 입력이 달라진 것이므로 옛 엔트리를 재사용하면 새 표가 없는
        //    옛 산출물이 그대로 남고, 이 계획이 세운 L1 검사도 캐시 히트에서는 영영
        //    발동하지 않는다.
        // 10: 프롬프트 입력이 둘 바뀌었다 - 스키마 표 컬럼 필터가 INSERT·UPDATE 대상
        //     컬럼(입력원 ⑤)도 보게 됐고(오직 대상으로만 등장하는 컬럼이 잘려 모델이
        //     "스키마에 없다"고 단정하던 결함), 실행 의미 표의 `DB 배치` 문장이 3부
        //     식별자를 소속 DB 접두사로 안과 밖으로 가른다(홈 DB 참조가 크로스 DB로
        //     읽히던 결함). 이 회차가 세운 L1 검사도 셋 늘었다 - 기계 확정 표의 헤더·
        //     구분·데이터 행 셀 수, INSERT 매핑 표 테이블명의 파서 표기 대조(Ordinal),
        //     널 허용 주장과 `Dependencies.IsNullable`의 테이블 앵커 대조. 프롬프트
        //     입력이 달라진 것이므로 옛 엔트리를 재사용하면 틀린 재료로 만든 산출물이
        //     그대로 남고, 새 L1 셋도 캐시 히트에서는 영영 발동하지 않는다.
        //     2026-08-22 축 A 재감사 실측 6결함이 근거다.
        // 11: 기계 확정 표 셋의 모양이 한꺼번에 바뀌었다(2026-08-22 축 A 재감사 ③).
        //     표 종류는 늘지 않았다 - 기존 표가 담는 것이 넓어진 회차다.
        //     (1) 집합 술어 표에 「술어 원문」 열이 마지막 칸으로 붙고, 행 단위가
        //     "분해된 컬럼-리터럴 쌍"에서 "최상위 AND 항"으로 올라갔다. 그래서 분해되지
        //     않는 항(OR 결합·컬럼 대 컬럼·부등식)도 컬럼·연산·원소 수·리터럴이 전부
        //     `—`인 행으로 표에 자리를 얻는다 - 그런 항은 원문 칸이 유일한 기록이다.
        //     (2) 잠금 힌트 표와 DML 범위 표의 문장 집합이 넓어졌다 - 다만 넓어진
        //     폭이 서로 달라서, 둘을 "두 표"로 묶어 적으면 그 차이가 지워진다.
        //     잠금 힌트 표는 DML 밖 독립 SELECT와 `IF` 술어 안의 스캔까지 담으므로
        //     문장 칸에 `SELECT n`과 `IF n`이 둘 다 실리고, 범위 칸에 `하위 질의`
        //     값이 더해졌다(술어 안에서 다시 열린 질의가 훑는 자리).
        //     DML 범위 표는 독립 SELECT(커서 원천 질의 포함)까지만 담는다 - 문장
        //     칸에는 `SELECT n`만 실리고 `IF n`은 실리지 않으며(DmlScopeVisitor는
        //     IfStatement를 오버라이드하지 않는다), 이 표에는 범위 칸 자체가 없다
        //     (렌더러 헤더는 문장·라인·대상·WHERE 최상위 술어 컬럼·기준일 파라미터
        //     적용·조인 키·GROUP BY·ORDER BY 여덟 칸이고, DmlScopeFact에도 Scope
        //     필드가 없다).
        //     (3) 이 회차가 세운 L1도 함께 넓어졌다 - 집합 술어 행 대조 키에 범위와
        //     술어 원문이 들어갔고(같은 줄의 분해 불가 항 둘이 키에서 겹쳐 한 항이
        //     통째로 사라지는 것을 못 잡던 구멍이다), 리터럴 목록 칸 인덱스가 새 열만큼
        //     밀렸다.
        //     프롬프트 입력이 달라진 것이므로 옛 엔트리를 재사용하면 옛 표로 만든
        //     산출물이 그대로 남는다 - 원문 칸이 없어 분해되지 않는 술어가 통째로
        //     빠진 명세서, `IF` 술어의 잠금이 없는 명세서가 그대로 살아남고, 넓어진
        //     L1 셋도 캐시 히트에서는 영영 발동하지 않는다.
        // 12: CRUD 분석의 `SELECT 대상 테이블` 표 설명 칸이 조인 키·WHERE 술어를 담지
        //     않게 됐다(2026-08-23 ④ 진단). 술어의 기준값은 DML 범위·집합 술어 표이고
        //     둘 다 문장별로 행을 낸다. 버전 5가 "함수 동작 서술이 금지되었다 -
        //     프롬프트 입력과 출력 계약이 둘 다 달라졌으므로 옛 산출물은 재분석해야
        //     한다"로 올린 것과 같은 모양이다. 옛 엔트리를 재사용하면 금지된 서술을
        //     담은 산출물이 그대로 남고, 그것이 다시 감사에서 결함으로 잡힌다.
        //     실측 근거: UP_UTIL_SETTLE_EXCEPTION_PROC의 설명 칸이 UPDATE 3과 4의
        //     조인 키를 묶어 적어 UPDATE 4에 없는 MALLID 조인을 주장했고, Critic이
        //     UPDATE 3의 근거로 그것을 통과시켰다.
        // 13: 기계 확정 표 넷이 또 한꺼번에 넓어졌다(2026-08-23 축 A ③(b)). 11과 마찬가지로
        //     표 종류는 늘지 않았다. 넓어진 폭이 표마다 다르므로 표별로 갈라 적는다 -
        //     11의 (2)를 "두 표"로 묶어 적었다가 문서 여섯 자리에 거짓이 실린 전례가 있다.
        //     (1) 참조 함수 표(ReferencedFunctionVisitor): DML 셋에 더해 FROM이 있는 독립
        //     SELECT와 `IF` 술어의 호출까지 담는다. 이 표에는 문장 칸이 없다 - 렌더러
        //     헤더는 함수·호출 위치·인자·명세서 넷이고, 넓어진 문장 집합은
        //     「호출 위치」 칸의 `SELECT n (라인 L)`·`IF n (라인 L)`으로 나타난다.
        //     (2) 집합 술어 표(SetPredicateVisitor): DML 셋에 더해 FROM이 있는 독립
        //     SELECT의 WHERE까지 담는다 - 문장 칸에 `SELECT n`이 실린다. `IF` 술어는
        //     담지 않으므로 `IF n` 행은 이 표에 나오지 않는다(참조 함수 표와 폭이 다르다).
        //     실물은 UF_GET_COLLECTYMD:100의 `CollectFlag = 1`로, 넓히기 전에는 어떤
        //     기계 확정 표에도 없고 산문에만 있었다.
        //     (3) 잠금 힌트 표(LockHintVisitor): 문장 집합은 11 그대로 다섯이다. 바뀐 것은
        //     하위 질의 수집 범위로, WHERE 절 한정에서 문장 노드 전체로 넓어졌다 -
        //     SELECT 목록·DML의 `SET` 절·독립 SELECT의 WHERE에 걸린 스칼라 하위 질의가
        //     이제 `하위 질의` 범위로 실린다(곁가지로 `VALUES`·HAVING·ORDER BY·`OUTPUT`
        //     절도 같은 경로로 들어온다). 코퍼스 전후 대조로 실제로 늘어난 행은
        //     UF_Get_CLComm4MobileCo:32의 NOLOCK 한 행이고 기존 230행은 불변이다.
        //     넓힌 경로는 NextOrdinal을 부르지 않으므로 네 표가 공유하는 `SELECT n`은
        //     밀리지 않는다.
        //     (4) 실행 의미 표(ExecutionSemanticsFacts): 종류가 다섯에서 일곱이 됐다 -
        //     `비집계 대입`(NonAggregateAssignmentExtractor)과
        //     `루프 내 재설정`(LoopVariableResetExtractor)이 AllKinds 끝에 붙었다.
        //     표의 열은 그대로 넷(종류·라인·대상·확정 사실)이다.
        //     (5) 집합 술어 표의 도입문(프롬프트 문자열)도 바뀌었다 - `SELECT n` 행의
        //     술어가 쓰는 대상 행이 아니라 읽는 행을 가른다는 구절을 더했다. 표의 모양이
        //     아니라 지시문만 바뀐 것이지만 프롬프트 바이트가 달라지는 것은 같으므로
        //     이 회차의 무효화 사유에 함께 적는다.
        //     (6) L1은 이 회차에 넓히지 않았다 - CheckSetPredicates가 사실을 묶는 키는
        //     (연산·라인·컬럼·범위·술어 원문) 다섯이지만 실제 행 매칭은 라인·컬럼·범위·
        //     술어 원문 네 칸만 보고 연산 칸을 보지 않으며, CheckExecutionSemantics도
        //     네 칸의 문자열 일치라 종류 목록을 보지 않는다. 그래서 새 행이 그대로
        //     흘러간다. 확인 테스트를 두어 못 박았다(MechanicalValidatorTests의
        //     Validate_SetPredicateSelectRow*·Validate_ExecutionSemantics*).
        //     프롬프트 입력이 달라진 것이므로 옛 엔트리를 재사용하면 새 행이 없는 옛
        //     산출물이 그대로 남는다 - 독립 SELECT의 집합 술어와 `IF` 술어의 함수 호출이
        //     빠진 명세서, 32행 NOLOCK이 표 어디에도 없는 명세서, 루프 내 재설정과
        //     비집계 대입이 산문에만 있는 명세서가 그대로 살아남는다.
        // 14: 2026-08-23 9회차 축 A 재감사 🟠(회귀) - 집합 술어 표가 JOIN ON 절의
        //     조인 키 등식이 아닌 항을 `조인 ON T`(파생 안이면 `파생 테이블 X · 조인 ON T`,
        //     외부 조인이면 `LEFT OUTER 조인 ON T`) 범위로 싣는다
        //     (SetPredicateVisitor.CollectJoinOnTerms). INS_EXTRA4PLCARD 다섯 문장의
        //     `PG.ExtraType IN (2,3)`이 설명 칸 술어 금지(12)로 자리를 잃고 어떤 표에도
        //     없었다 - 13은 그 자리를 없앴지만 받아 줄 표를 먼저 올리지 않았다. 표의 열은
        //     그대로 여덟이고 도입문(프롬프트 문자열)에 `조인 ON` 범위의 뜻과 외부 조인
        //     서술 규칙이 더해졌다. 코퍼스 전수 열거에서 늘어난 행은 정확히 다섯
        //     (INS_EXTRA4PLCARD 4 · EXPECT_PROC:210 1)이고 기존 578행은 불변이다 -
        //     DmlScopeExtractorTests의 코퍼스 앵커 583·91·0. L1 CheckSetPredicates는
        //     범위 칸을 행 매칭 키에 이미 넣고 있어 새 값이 그대로 흘러간다(검사 변경 없음).
        //     옛 엔트리를 재사용하면 조인 ON 행이 없는 명세서가 그대로 남으므로 전건 무효.
        //     (main 대조: 인상 직전 main 값 13 - reset-l1-check 스킬의 번호 충돌 규칙.)
        // 15: 2026-08-23 9회차 축 A 재감사 ⚪ (A)·(G) - 프롬프트 재료 둘이 바뀌었다.
        //     (A) DML 범위 표 아래 안내문("`아니오`는 최상위 WHERE에 없다는 뜻일 뿐이고
        //     하위 질의·파생 테이블 안에서 기준일을 쓰는 문장이 있다")이 모든 객체에 고정으로
        //     붙어 하위 질의가 없는 4객체에서 거짓이었다. 이제 DmlScopeFact.DateParameterInNestedQuery가
        //     참인 `아니오` 문장이 있을 때만, 그 문장 번호와 함께 싣는다(BuildDmlScopeTableLines).
        //     (G) SqlStaticParser가 파생 테이블이 투영하는 이름의 한정자 없는 컬럼을 같은 FROM의
        //     하나뿐인 물리 테이블에 붙이던 폴백을 막았다 - 코퍼스 31개에서 귀속 4건이 빠진다
        //     (EXCEPTION_PROC TPGProperty.PLTID·ID, COLLECTYMD TPGCollectPeriodMst.YMD, UIF_SettleYMD
        //     TSettlePeriodMst.YMD), 추가 0. 그 결과가 프롬프트의 스키마 표·SELECT 대상 표 재료다.
        //     두 변경 모두 기존 객체의 프롬프트 바이트를 바꾸므로(문장이 빠지거나 컬럼이 빠짐)
        //     "영향 객체 0" 예외가 성립하지 않아 올린다. (main 대조: 인상 직전 main 값 14.)
        // 16: 2026-08-25 기계 확정 표 확장 - 「트랜잭션 경계」·「변수 대입」 두 표가
        //     새로 생겼다. 프롬프트 입력(BuildMachineFactBlockLines가 싣는 표 뼈대 둘)과
        //     출력 계약(명세서가 그 표 둘을 담아야 한다)이 **함께** 바뀌었으므로 인상
        //     대상이다 - Critic만 느슨해지는 변경도, 영향 객체 0인 조건부 블록도 아니다.
        //     두 표는 모든 SP·함수에 무조건 실리므로 기존 31개 객체의 프롬프트 바이트가
        //     전부 달라진다. 안 올리면 재생성이 캐시 적중으로 건너뛰어져 검증 자체가
        //     일어나지 않고, 표 둘이 없는 옛 산출물이 다음 감사에서 그대로 결함이 된다.
        //     L1도 함께 조였다(CheckTransactionBoundaries·CheckSetAssignments) - 캐시
        //     히트 산출물에는 L1이 영영 안 돌므로 이 인상이 그 검사들이 처음 도는 자리다.
        //     캐시 인상 **전에** 코퍼스 전수 스윕을 돌려 거짓 양성 0을 확인했다(31쌍,
        //     다른 검사 카운트 BASE와 동일). 오탐을 안은 채 전건 재생성을 걸면 그것이
        //     곧바로 재시도 소진으로 번지기 때문이다.
        //     (main 대조: 인상 직전 main 값 15. 다른 브랜치도 15 이하라 16이 비어 있음을
        //     확인했다 - reset-l1-check 스킬의 번호 충돌 규칙.)
        // 17: 2026-08-27 「오류 코드」 표(문장 번호·오류 코드·대입 대상 변수)가 산출물에
        //     실린다. 표 자체는 2026-08-25에 프롬프트와 카탈로그에 들어갔으나 버전은 16에
        //     머물러 있었다 - 인상 전 스윕에서 단계 검사의 거짓 양성 원인 넷이 남아
        //     있었고, 그것을 닫기 전에 전건 재생성을 걸면 거짓 오류 33건이 한꺼번에
        //     켜지기 때문이다(같은 규칙의 두 번째 적용). 원인 넷은 2026-08-27에 닫혔다.
        //     프롬프트 입력(BuildErrorCodeTableLines가 싣는 표)과 출력 계약(명세서가 그
        //     표를 담아야 한다)이 함께 바뀌므로 인상 대상이다.
        //     이 표의 사실은 이미 「DML 범위」로 실린 문장 위에만 얹히므로 커버리지 맵의
        //     🟧→🟥 전이 창은 열리지 않는다.
        //     [이 인상이 처음 켜는 검사] CheckErrorCodes는 코퍼스에서 한 번도 돌아 본 적이
        //     없다 - 캐시 히트 산출물에는 L1이 영영 안 돌기 때문이다. 인상 전에
        //     ErrorCodeTableCorpusTests로 만족가능성을 확인했다: 31 객체 · 사실을 가진
        //     객체 12 · 사실 합 84 · 갈래 셋(완전 전사된 표에 발화 0 / 사실 0건 객체는
        //     조기 반환으로 침묵 / 사실 있는 객체는 표 부재에 발화) 전부 발화 0.
        //     오탐을 안은 채 전건 재생성을 걸면 그것이 곧바로 재시도 소진으로 번진다.
        //     [이 인상이 관할을 바꾼다] 앵커가 정상화되면 검사 B·C가 도달하는 문장이
        //     늘고, 그만큼 가려져 있던 침묵도 함께 켜진다. 승격 전후 스윕의 「침묵
        //     분모」 절이 그 변화를 센다 - 발화가 늘었는지만 보면 그 부류를 못 본다.
        //     기준선은 docs/audit-reports/sweeps/2026-08-27-step-sweep-pre-cache17.md.
        //     (main 대조: 인상 직전 main 값 16. 코디네이터가 전 브랜치를 확인했다 -
        //     main·origin/main·local/main·worktree-agent-ae7b39ba4f121cbb3 이 16,
        //     worktree-agent-af1fbecfcf4e8d9d5 가 15. 17이 비어 있음을 확인했다 -
        //     reset-l1-check 스킬의 번호 충돌 규칙.)
        // 18: 2026-08-29 - 기계 확정 「지역 변수」 표 신설(known-defects (5-3-7)).
        //     MachineConfirmedTables.All에 표가 하나 늘어 Critic 면제 블록의 바이트가
        //     바뀌고, Actor 프롬프트의 세 갈래(SP 전체·함수·OverviewAndParameters)에
        //     새 표가 실린다. AGENTS.md 95행이 카탈로그 등록과 함께 올리라고 못박는다.
        //     [이 회차는 재생성을 하지 않는다] 강제만 걸고 다음 재생성이 켜게 둔다 -
        //     그래서 이 승격은 "다음에 생성을 돌리는 사람이 전건 재생성을 문다"는 뜻이다.
        //     [오탐 위험 - 실측은 이 커밋이 아니라 같은 물결의 Task 7이 잰 것이다]
        //     한 번도 안 돌아 본 검사가 오탐을 안고 켜지는 위험을 이 커밋 스스로
        //     재지는 않았다 - LocalVariableTableCorpusTests(Task 7)가 재고, 그
        //     테스트는 이 커밋보다 먼저 통합된다(통합 순서 6 → 7 → 8). 실측치
        //     (Task 7, 조율자 재현): 객체 31 · 사실을 가진 객체 25 · 사실 합 101
        //       프로시저     : 14 · 9 · 40
        //       함수(같은 DB): 10 · 9 · 23
        //       함수(외부 DB): 7 · 7 · 38
        //     갈래 셋(완전 전사 표 → 발화 0 / 사실 0건 객체 → 조기 반환으로 침묵 /
        //     사실 있는데 표 없음 → 발화) 전부 만족(ErrorCodeTableCorpusTests가
        //     캐시 17 승격 때 한 것과 같은 자). 이 숫자가 흔들리거나 갈래 셋 중
        //     하나라도 깨지면 이 승격은 근거를 잃는다.
        //     번호 충돌 확인: 전 브랜치에서 18이 비어 있음을 확인했다(main·origin/main·
        //     local/main·이 물결의 워크트리 브랜치 전부 17 또는 그 이하).
        // 19: 2026-09-06 - 「실행 의미」 표의 `집계 대입`·`비집계 대입` 두 갈래가 함께
        //     넓어졌다(표 종류는 늘지 않는다).
        //     (1) 비집계 대입: 우변 가드가 「맨 컬럼」에서 「감쌈을 한 겹 벗긴 뒤 전부
        //     컬럼 참조인 분기식」으로 넓어져 `IIF`·`CASE`·`ISNULL(X, 리터럴)`·
        //     `COALESCE(X, 리터럴)`로 감싼 대입이 새로 실린다. 실물은
        //     UF_GET_COMM4CLIENT4PARTIALCANCEL:43의 수수료율 분기(축 A 🔴) -
        //     넓히기 전에는 어떤 기계 확정 표에도 없고 명세서가 한 줄도 서술하지
        //     않았다. 비집계 코퍼스 대장
        //     (NonAggregateAssignmentExtractorTests.Extract_OverTheCorpus_
        //     ShouldCollectExactlyTheseRows)이 43행(NULL확정 27 · 중립 16)을 못박는다.
        //     (2) 집계 대입: `ISNULL(<집계>, 리터럴)`·`COALESCE(<집계>, 리터럴)`이 새로
        //     실리고, 그 행은 문장 갈래가 셋에서 넷("기본값대입")이 된다 - 감쌈 없는
        //     집계는 무결과 시 NULL을 넣지만 감쌈이 있으면 리터럴 기본값이 그것을
        //     덮는다. 실물은 UP_UTIL_SETTLE_PROC_ETC:116·130(축 A 🟠) - 넓히기 전에는
        //     집계 그물(최상위가 집계 이름이어야 함)과 비집계 그물(맨 컬럼이어야 함)
        //     사이로 새어 어떤 표에도 없었다. 집계 코퍼스 대장
        //     (AggregateAssignmentExtractorTests.Extract_OverTheCorpus_
        //     ShouldCollectExactlyTheseRows)이 10행 중 2행을 "기본값대입" 갈래로
        //     못박는다.
        //     (3) 두 갈래의 대상 칸이 우변 원문을 싣는다 - 다만 갈래마다 전사(前史)가
        //     다르다. 집계 쪽은 **기존 8행 전부**가 바뀐다 - `AggregateAssignmentFact`
        //     에는 `Expression` 필드 자체가 없었고(필드는 `Line·Variable·Aggregate·
        //     HasInitializer·Sentence` 뿐이었다) 대상 칸은 `fact.Aggregate`로 인자를
        //     생략한 고정 문자열 `SELECT @v = {Aggregate}(...)`였다. 이 회차가
        //     `Expression`을 레코드에 새로 추가하고(네 번째 위치) 대상 칸을 그 실제
        //     인자(예: `SELECT @v_intTotal = MIN(YMD)`)로 바꿨다. 비집계 쪽은
        //     정반대다 - `NonAggregateAssignmentFact`에는 이미 `Column`이라는 필드가
        //     있었고, 이 회차가 그 필드 **이름만** `Expression`으로 바꿨다. 맨 컬럼
        //     대입인 기존 36행은 컬럼 참조의 원문이 곧 컬럼 이름이므로 **표기 값이
        //     바이트 단위로 그대로다**. 새 표기(분기식 축자, 예: 위 IIF 전체)를 싣는
        //     것은 이 회차가 새로 담기 시작한 7행뿐이다. 옛 엔트리와 새 엔트리를
        //     섞으면 집계 표에서는 표기가 갈리고, 비집계 표에서는 새 7행의 유무 자체가
        //     갈린다.
        //     프롬프트 입력이 달라졌으므로 옛 엔트리를 재사용하면: 수수료율 분기
        //     (UF_GET_COMM4CLIENT4PARTIALCANCEL:43류)가 산문에도 표에도 없는 명세서,
        //     대사 집계식(UP_UTIL_SETTLE_PROC_ETC:116·130류)이 어디에도 없는 명세서,
        //     그리고 살아남은 옛 집계 행마저 대상 칸이 `SELECT @v = SUM(...)`처럼
        //     인자를 생략한 낡은 표기 그대로인 명세서가 그대로 배송된다.
        //     번호 충돌 확인: main·origin/main·integration/settlement-policy·
        //     feat/settlement-policy-redesign 전부 18이고 19는 비어 있음을 확인했다.
        //     [2026-09-07 2 회차 - 같은 19 안에서 비집계 갈래가 다시 넓어졌다] 분기식
        //     (`IIF`/`CASE`)의 결과(THEN·ELSE) 판정이 "전부 컬럼 참조"에서 "컬럼 참조 /
        //     리터럴 / 그 둘의 산술식"으로 넓어졌다(한 겹만 - 결과 안에 또 분기식이
        //     오거나 함수 호출·하위 질의가 오면 여전히 침묵한다). 비집계 코퍼스 대장이
        //     43행에서 **52행**(NULL확정 31 · 중립 21)으로 늘었다. 실물은
        //     UF_GET_COMM4CLIENT4INTEREST:35·UF_GET_COMM4PG4INTEREST:42
        //     (`CASE … END / 100.0` - 최상위가 분기식을 품은 산술식) ·
        //     UF_GET_EXTRACOMM4CLIENT:41·53·66(`ISNULL(CASE … THEN 컬럼-컬럼 산술식
        //     … ELSE 0 END, 0)`) · UF_Get_ExtraCardCommissionAmt:42·47(`ISNULL(CASE
        //     … THEN 컬럼 … ELSE 0 END, 0)` - THEN이 맨 컬럼이고 리터럴은 ELSE뿐,
        //     EXTRACOMM4CLIENT와는 다른 모양이다) · UF_GET_PGCommOption:21(`CASE …
        //     THEN 컬럼 … ELSE 0 END`) · UF_GET_SETTLE_EXCHANGERATE:26(컬럼 산술 +
        //     곱셈으로 묶인 **형제** `IIF` 둘, 그중 하나의 결과가 리터럴·컬럼·산술식)
        //     이다. 집계 대장은 10행으로 무변경 -
        //     `AggregateAssignmentExtractor`는 이 판정을 쓰지 않는다. 통째 완화(모든
        //     산술식 재귀 허용)를 먼저 실측했으나 원본 줄 주석이 대상 칸 안으로 섞여
        //     드는 부작용이 나와(중첩 분기식·함수 호출·하위 질의 다섯 자리) 그 셋을
        //     배제하는 좁은 술어로 되돌렸다 - 늘어난 9행 전량을 원본 DDL로 대조해
        //     거짓 행 0·주석 섞임 0을 확인했다. **캐시 형식 버전은 20으로 올리지
        //     않는다** - 19가 아직 어떤 산출물에도 적용되지 않았다(명세서 재생성 전).
        //     설계는 `docs/superpowers/specs/2026-09-06-대입-감쌈-벗기기-design.md`
        //     §10에 있다.
        //     [2026-09-07 최종 브랜치 검토 - 위 넓힘이 연 구멍을 가드 둘로 닫는다]
        //     최상위 리터럴 개방(`case Literal: return true;`)이 `GROUP BY` 없는
        //     `HAVING`과 만나면 T-SQL의 암묵적 한 그룹 규칙이 무결과여도 1행을 돌려줘
        //     "무결과 시 대입이 일어나지 않는다"는 확정 문장을 거짓으로 만들고
        //     (`SELECT @v = 1 FROM T HAVING COUNT(*) = 0`), 같은 SelectElements 안의
        //     형제 집계(`SELECT @a = 1, @b = COUNT(*) FROM T`)도 같은 함정을 열어
        //     정반대 확정 문장 둘을 한 표에 나란히 싣는다. `HavingClause` 존재와
        //     형제(집계를 품었거나 `SelectSetVariable`이 아닌) 요소를 침묵 조건으로
        //     추가해 닫았다 - 코퍼스(52행)에 두 모양 다 0건이라 대장 행이 한 행도 안
        //     줄었다. 설계는 위 §10과 같은 문서 §11-5(다)·§11-9에 있다.
        //     [2026-09-07 3 회차(설계서 §12) - 진리 조건을 단일 재귀 술어로 접는다]
        //     위 두 회차가 더한 조기 반환들(FROM 절 집계·HAVING·GROUP BY·형제)은
        //     FROM 절 집계 하나만 하위로 훑고 나머지는 최상위 질의 한 층만 봤다 -
        //     그래서 같은 위험한 모양(집계 없이 무결과가 1행이 되는 조건)이 파생
        //     테이블·APPLY 안으로 한 층만 들어가면 판정을 벗어나 다시 열렸다(3 회차
        //     검토가 세 번째로 낸 Critical). 이 회차는 조건의 집합을 넓히거나
        //     좁히지 않고 흩어진 여섯 조기 반환을 단일 재귀 술어
        //     (`GuaranteesZeroRowsWhenSourcesAreEmpty`)로 접는다 - FROM의 각 원천
        //     (이름 있는 테이블·파생 테이블·INNER/LEFT/RIGHT/FULL 조인·CROSS/OUTER
        //     APPLY·VALUES·TVF·PIVOT/UNPIVOT·테이블 변수)에 같은 질문을 재귀로
        //     던진다. 코퍼스 대장은 52행 그대로다(비집계) - 층을 넘나드는 이 모양이
        //     코퍼스에 0건이라서다. 집계 대장도 10행 그대로다 -
        //     `AggregateAssignmentExtractor`는 이 술어를 쓰지 않는다. 같은 회차가
        //     `AggregateNames`에 `JSON_ARRAYAGG`·`JSON_OBJECTAGG`도 더했다(§12-5).
        //     프롬프트·출력 형식은 바뀌지 않아(추출기 내부 판정만 재구성했을 뿐
        //     담김/침묵의 경계는 그대로다) **캐시 형식 버전은 20으로 올리지 않는다**.
        //     설계는 위와 같은 문서 §12에 있다.
        // v20 (2026-09-08) - 집합 술어 표가 `HAVING` 절의 항을 싣는다.
        //     축 A 감사 🟠: `COMM_UPD:248`의 `HAVING SUM(TxAmt) = 0`이 **어느 기계 확정
        //     표에도 없어** 명세서에서 통째로 사라졌고, 단계·계획서까지 전파됐다
        //     (원본 1 · 명세서 0 · steps/S05.md 0 · 계획서 0 - 코딩 에이전트의 입력에
        //     이미 실려 있었다). 이 표의 관할이 최상위 WHERE · 파생 테이블 WHERE ·
        //     조인 ON 셋뿐이고 HAVING이 그 밖이었다. HAVING은 그룹을 좁히므로 빠지면
        //     대상 행 집합이 넓어진다 - 위 실측에서 「부분취소 합이 승인금액과 상계된
        //     PLTID만 대상」이라는 한정이 사라져 상계 안 된 건까지 갱신 대상이 된다.
        //     **프롬프트에 실리는 재료가 늘었으므로 인상한다** - 안 올리면 COMM_UPD가
        //     캐시 적중으로 건너뛰어져 새 행이 영영 안 실리고 L1도 안 돈다.
        //     코퍼스 31개 중 HAVING을 가진 객체는 COMM_UPD 하나뿐이라(전수 grep)
        //     영향 객체는 1이다. 스윕 차분: BASE 0 → 신규 1(그 진짜 양성), 거짓 양성 0,
        //     다른 검사 카운트 불변. 집합 술어 대장은 583 → 584(비최상위 91 → 92).
        //
        //     [같은 v20 에 둘째 변경] DML 범위 표의 `조인 키` 칸이 **외부 조인 종류**를
        //     함께 싣는다(`PGName · 외부 조인 Y(LEFT OUTER)`). 축 A 감사 🟠:
        //     `INS_EXTRA:205`의 `LEFT OUTER JOIN TPGProperty Y ON X.PGName = Y.PGName`의
        //     조인 종류가 명세서 어디에도 없었다 - 종류 라벨은 집합 술어 표의 범위 칸에만
        //     얹혀 나가는데 이 ON 은 조인 키 등식뿐이라 그 행이 필터로 빠져 라벨이 탈
        //     자리가 사라진다. 파생 테이블 안의 조인은 별칭을 앞세운다
        //     (`파생 테이블 X · B(LEFT OUTER)`) - 층위를 뭉개면 파생 안의 조인이 최상위
        //     조인처럼 읽힌다. 렌더(AiService)와 L1 기대값(MechanicalValidator)이
        //     `DmlScopeFact.JoinKeysCell` 한 자리에서 나온다 - 축자 정확 일치 대조라
        //     두 곳이 갈리면 전 객체가 거짓 양성이 된다.
        //     스윕: BASE 0 → 신규 10(6 객체: COLLECTYMD 2 · UIF_SettleYMD 2 ·
        //     EXCEPTION_PROC 3 · SETTLE_INS 1 · INS_EXTRA 1 · INS_EXTRA4PLCARD 1),
        //     전부 「새 재료가 아직 명세서에 없다」이고 재생성으로 닫힌다. 거짓 양성 0.
        //     21 로 올리지 않는 이유: 19 가 아직 어떤 산출물에도 적용되지 않았다
        //     (공유 캐시 인덱스 31 건 전량 19, 재생성 전) - 20 한 판이 둘을 함께 나른다.
        // v21 (2026-09-08) - 3부 참조 목록을 **소속 DB 안/밖으로 갈라** 프롬프트에 싣는다.
        //     [왜 20 을 재활용하지 않는가] 20 이 이미 산출물 셋에 적용됐다
        //     (G0 이 INCVTAXRATE·ROUND4VAT·WORKDAY2 를 v20 으로 올린 뒤 중단됐다).
        //     프롬프트가 바뀐 지금 20 을 그대로 두면 그 셋만 옛 계약으로 만들어진 채
        //     캐시 적중으로 남아 **같은 번호 아래 두 계약**이 생긴다 - 나중에
        //     「왜 이 셋만 다른가」를 못 가른다. 그래서 21 로 올려 31 건을 한 계약으로 맞춘다.
        //
        //     [사고 경위] v20 재생성을 돌리자 `UIF_SettleYMD` 가 L1
        //     (DatabasePlacementProseContradiction)에 걸려 재시도를 소진하기 시작했다.
        //     원인은 검사가 아니라 프롬프트다 - 「3부 식별자면 크로스 데이터베이스 참조」
        //     라고만 지시하고 목록에서 소속 DB 안 객체를 갈라 주지 않아, 모델이 자기 DB
        //     객체(`SETTLE_POQ_DB.dbo.THoliday`)까지 크로스 DB 라 불렀다. **재료를 안 주고
        //     L1 으로 벌만 주면 루프가 스스로 닫히지 않는다** - 6 회 소진 뒤 검증 못 통과한
        //     판이 배너를 달고 배송된다. 코퍼스 실측으로 같은 자리에 걸릴 객체가 7 개였다
        //     (COLLECTYMD·UIF_SettleYMD·CANCEL_INS·EXCEPTION_PROC·EXPECT_PROC·
        //     INS_EXTRA4PLCARD·Summary_AcqManual).
        //
        //     갈래의 근거는 DatabasePlacementExtractor 가 `실행 의미` 표의 `DB 배치` 행에
        //     쓰는 것과 같다 - 프롬프트와 표가 갈리면 안 된다.
        private const int CurrentCacheFormatVersion = 21;
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
        private static readonly Regex ReferenceSectionRegex = new(
            @"(?ms)^## 참조 코드 객체(?:[ \t]*\r?\n|\z).*?(?=^##\s|\z)",
            RegexOptions.Compiled);

        public string ComputeCompositeHash(SpDefinition spDef, int maxDepth)
        {
            if (spDef == null) return string.Empty;

            // 1. SP 본문 소스 DDL 해시
            var sourceHash = ComputeSha256(spDef.DdlText);

            // 2. 의존성 개체들의 해시 수집 및 정렬 (일관된 해시 결합을 위해 SortedDictionary 사용)
            var depHashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (spDef.Dependencies != null)
            {
                foreach (var dep in spDef.Dependencies)
                {
                    var key = BuildDependencyKey(dep);
                    var ddl = dep.ReferencedDdlText ?? string.Empty;
                    depHashes[key] = ComputeSha256(ddl);
                }
            }

            // 3. 결합 문자열 구성
            var sb = new StringBuilder();
            sb.AppendLine($"Source:{sourceHash}");
            sb.AppendLine($"MaxDepth:{maxDepth}");
            foreach (var kvp in depHashes)
            {
                sb.AppendLine($"Dep:{kvp.Key}:{kvp.Value}");
            }

            return ComputeSha256(sb.ToString());
        }

        public bool IsCacheValid(
            CodeObjectKey objectKey,
            string compositeHash,
            OutputPathResolver outputPaths)
        {
            if (outputPaths != null)
            {
                EnsureMigrated(outputPaths.OutputRoot);
            }

            if (objectKey == null ||
                string.IsNullOrWhiteSpace(compositeHash) ||
                outputPaths == null)
            {
                return false;
            }

            var cacheKey = objectKey.CanonicalName;
            Log.Information("캐시 유효성 검사 - 코드 객체: {ObjectKey}", cacheKey);

            try
            {
                // 1. 실제 출력 파일 경로 확인 (존재하지 않아도 File Copy를 위해 진행)
                var specFilePath = outputPaths.ResolveSpecPath(objectKey);

                // 2. 캐시 인덱스 파일 로드 및 해시 대조
                var globalCacheDir = GetGlobalCacheDirectory(outputPaths.OutputRoot);
                var cacheIndex = LoadCacheIndex(globalCacheDir);
                if (cacheIndex != null &&
                    TryGetEntry(cacheIndex, objectKey, outputPaths, out var entry))
                {
                    // 파일 읽기와 해시 계산보다 먼저 판정한다. 해석할 수 없는 스키마의
                    // 엔트리는 내용이 일치하더라도 신뢰할 근거가 없다.
                    if (entry.FormatVersion != CurrentCacheFormatVersion)
                    {
                        Log.Information(
                            "캐시 미스(포맷 버전 {EntryVersion} != {CurrentVersion}) - 코드 객체: {ObjectKey}",
                            entry.FormatVersion,
                            CurrentCacheFormatVersion,
                            cacheKey);
                        return false;
                    }

                    string currentSpecContentHash = string.Empty;
                    if (File.Exists(specFilePath))
                    {
                        var specFileContent = NormalizeSpecificationForCache(
                            File.ReadAllText(specFilePath));
                        currentSpecContentHash =
                            entry.SpecContentLength > 0 &&
                            specFileContent.Length >= entry.SpecContentLength
                                ? ComputeSha256(
                                    specFileContent[
                                        (specFileContent.Length - entry.SpecContentLength)..])
                                : string.Empty;
                    }

                    var isValid =
                        entry.ObjectKey == objectKey &&
                        !string.IsNullOrWhiteSpace(entry.SpecContentHash) &&
                        (!File.Exists(specFilePath) || string.Equals(
                            entry.SpecContentHash,
                            currentSpecContentHash,
                            StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(
                            entry.CompositeHash,
                            compositeHash,
                            StringComparison.OrdinalIgnoreCase);

                    if (isValid)
                    {
                        // Copy the original file to the new destination if they differ
                        if (!string.IsNullOrEmpty(entry.OriginalSpecPath) && 
                            File.Exists(entry.OriginalSpecPath) &&
                            !string.Equals(entry.OriginalSpecPath, specFilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var destDir = Path.GetDirectoryName(specFilePath);
                                if (!string.IsNullOrEmpty(destDir)) 
                                    Directory.CreateDirectory(destDir);
                                File.Copy(entry.OriginalSpecPath, specFilePath, overwrite: true);
                                Log.Information("캐시 파일 복사 완료: {Src} -> {Dest}", entry.OriginalSpecPath, specFilePath);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "캐시 파일 복사 실패, Cache Miss로 간주합니다: {Dest}", specFilePath);
                                return false;
                            }
                        }
                        else if (!File.Exists(specFilePath))
                        {
                            // We hit the cache but the file doesn't exist AND we have no OriginalSpecPath to copy from
                            Log.Debug("캐시 히트이나 원본 파일이 존재하지 않아 Cache Miss 처리");
                            return false;
                        }

                        Log.Information(
                            "캐시 히트 - 코드 객체: {ObjectKey} (분석 생략 가능)",
                            cacheKey);
                    }
                    else
                    {
                        Log.Debug(
                            "캐시 미스 (객체 키 또는 복합 해시 불일치) - 코드 객체: {ObjectKey}, EntryHash: {EntryHash}, CurrentHash: {CurrentHash}",
                            cacheKey,
                            entry.CompositeHash,
                            compositeHash);
                    }
                    return isValid;
                }
            }
            catch (Exception ex)
            {
                // 캐시 로드 실패 시 안전하게 Soft Fail (false 반환하여 재분석 진행)
                Log.Warning(
                    ex,
                    "캐시 인덱스 파일 로드 중 오류 발생 - 코드 객체: {ObjectKey}",
                    cacheKey);
                return false;
            }

            Log.Debug(
                "캐시 미스 (캐시 인덱스 내 항목 없음) - 코드 객체: {ObjectKey}",
                cacheKey);
            return false;
        }

        public void UpdateCache(
            CodeObjectKey objectKey,
            SpDefinition spDef,
            string compositeHash,
            OutputPathResolver outputPaths,
            string specificationMarkdown)
        {
            if (outputPaths != null)
            {
                EnsureMigrated(outputPaths.OutputRoot);
            }

            if (objectKey == null ||
                spDef == null ||
                string.IsNullOrWhiteSpace(compositeHash) ||
                outputPaths == null ||
                string.IsNullOrEmpty(specificationMarkdown))
            {
                return;
            }

            var cacheKey = objectKey.CanonicalName;
            try
            {
                lock (FileLock)
                {
                    var globalCacheDir = GetGlobalCacheDirectory(outputPaths.OutputRoot);
                    var cacheIndex =
                        LoadCacheIndex(globalCacheDir) ??
                        new CacheIndex();

                    // 의존성 개별 해시 구성
                    var depHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (spDef.Dependencies != null)
                    {
                        foreach (var dep in spDef.Dependencies)
                        {
                            var key = BuildDependencyKey(dep);
                            var ddl = dep.ReferencedDdlText ?? string.Empty;
                            depHashes[key] = ComputeSha256(ddl);
                        }
                    }

                    var cacheableSpecification = NormalizeSpecificationForCache(
                        specificationMarkdown);
                    var entry = new CacheEntry
                    {
                        ProcedureName = $"{objectKey.Schema}.{objectKey.Name}",
                        FormatVersion = CurrentCacheFormatVersion,
                        ObjectKey = objectKey,
                        LastAnalyzed = DateTime.UtcNow,
                        SourceHash = ComputeSha256(spDef.DdlText),
                        DependencyHashes = depHashes,
                        CompositeHash = compositeHash,
                        SpecContentHash = ComputeSha256(cacheableSpecification),
                        SpecContentLength = cacheableSpecification.Length,
                        OriginalSpecPath = outputPaths.ResolveSpecPath(objectKey)
                    };

                    cacheIndex.Entries[cacheKey] = entry;

                    SaveCacheIndex(globalCacheDir, cacheIndex);
                    Log.Information(
                        "캐시 인덱스 갱신 성공 - 코드 객체: {ObjectKey}",
                        cacheKey);
                }
            }
            catch (Exception ex)
            {
                // 캐시 쓰기 실패 시 예외 격리 (분석은 통과했으므로 로깅 외 무시)
                Log.Warning(
                    ex,
                    "캐시 인덱스 갱신 실패 (예외 격리) - 코드 객체: {ObjectKey}",
                    cacheKey);
            }
        }

        private void EnsureMigrated(string outputRoot)
        {
            if (_hasMigrated) return;
            lock (_migrationLock)
            {
                if (_hasMigrated) return;
                MigrateLegacyCaches(outputRoot);
                _hasMigrated = true;
            }
        }

        public void MigrateLegacyCaches(string outputRoot)
        {
            try
            {
                var globalDir = GetGlobalCacheDirectory(outputRoot);
                if (!Directory.Exists(globalDir)) return;

                var globalIndexPath = Path.Combine(globalDir, CacheIndexFileName);
                var globalIndex = LoadCacheIndex(globalDir) ?? new CacheIndex();
                bool migratedAny = false;

                // Search for all .sp_cache_index.json files in subdirectories
                var legacyFiles = Directory.GetFiles(globalDir, CacheIndexFileName, SearchOption.AllDirectories);
                foreach (var file in legacyFiles)
                {
                    if (string.Equals(file, globalIndexPath, StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        var json = File.ReadAllText(file);
                        var legacyIndex = JsonSerializer.Deserialize<CacheIndex>(json, JsonOptions);
                        if (legacyIndex?.Entries != null)
                        {
                            var legacyDir = Path.GetDirectoryName(file);
                            var legacyResolver = new OutputPathResolver("legacy", legacyDir!); // Used just to resolve SpecPaths if needed

                            foreach (var kvp in legacyIndex.Entries)
                            {
                                // Update OriginalSpecPath if it was missing in legacy
                                if (string.IsNullOrEmpty(kvp.Value.OriginalSpecPath) && kvp.Value.ObjectKey != null)
                                {
                                    var expectedPath = legacyResolver.ResolveSpecPath(kvp.Value.ObjectKey);
                                    if (File.Exists(expectedPath))
                                    {
                                        kvp.Value.OriginalSpecPath = expectedPath;
                                    }
                                }

                                // Only merge if the file actually exists
                                if (!string.IsNullOrEmpty(kvp.Value.OriginalSpecPath) && File.Exists(kvp.Value.OriginalSpecPath))
                                {
                                    globalIndex.Entries[kvp.Key] = kvp.Value;
                                    migratedAny = true;
                                }
                            }
                        }
                        
                        // Optionally delete or rename the legacy file to prevent re-migration
                        File.Move(file, file + ".migrated", overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "레거시 캐시 마이그레이션 실패 (파일 수준): {File}", file);
                    }
                }

                if (migratedAny)
                {
                    SaveCacheIndex(globalDir, globalIndex);
                    Log.Information("레거시 캐시 마이그레이션 완료 (통합 캐시에 병합됨)");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "레거시 캐시 마이그레이션 중 오류가 발생하여 중단되었습니다.");
            }
        }

        private string GetGlobalCacheDirectory(string outputRoot)
        {
            var parent = Directory.GetParent(outputRoot);
            if (parent != null && parent.Name.Equals("output", StringComparison.OrdinalIgnoreCase))
            {
                return parent.FullName;
            }
            return outputRoot;
        }

        private CacheIndex? LoadCacheIndex(string outputDirectory)
        {
            var cacheIndexPath = Path.Combine(outputDirectory, CacheIndexFileName);
            if (!File.Exists(cacheIndexPath))
            {
                return null;
            }

            lock (FileLock)
            {
                var json = File.ReadAllText(cacheIndexPath);
                var cacheIndex = JsonSerializer.Deserialize<CacheIndex>(
                    json,
                    JsonOptions);
                if (cacheIndex == null)
                {
                    return null;
                }

                cacheIndex.Entries = new Dictionary<string, CacheEntry>(
                    cacheIndex.Entries,
                    StringComparer.OrdinalIgnoreCase);
                return cacheIndex;
            }
        }

        private void SaveCacheIndex(string outputDirectory, CacheIndex cacheIndex)
        {
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var cacheIndexPath = Path.Combine(outputDirectory, CacheIndexFileName);
            var json = JsonSerializer.Serialize(cacheIndex, JsonOptions);

            lock (FileLock)
            {
                File.WriteAllText(cacheIndexPath, json);
            }
        }

        private static bool TryGetEntry(
            CacheIndex cacheIndex,
            CodeObjectKey objectKey,
            OutputPathResolver outputPaths,
            out CacheEntry entry)
        {
            if (cacheIndex.Entries.TryGetValue(
                    objectKey.CanonicalName,
                    out entry!))
            {
                return true;
            }

            if (cacheIndex.Entries.TryGetValue(
                    objectKey.LegacyCanonicalName,
                    out entry!))
            {
                return true;
            }

            var legacyKey = $"{objectKey.Schema}.{objectKey.Name}";
            return objectKey.Type == CodeObjectType.Procedure &&
                outputPaths.IsCurrentDatabase(objectKey.Database) &&
                cacheIndex.Entries.TryGetValue(legacyKey, out entry!);
        }

        private static string BuildDependencyKey(DependencyInfo dependency) =>
            string.Join(
                    ".",
                    CodeObjectKey.EncodeCanonicalSegment(dependency.Database ?? string.Empty),
                    CodeObjectKey.EncodeCanonicalSegment(dependency.Schema),
                    CodeObjectKey.EncodeCanonicalSegment(dependency.Name),
                    CodeObjectKey.EncodeCanonicalSegment(dependency.Type))
                .ToUpperInvariant();

        private static string NormalizeSpecificationForCache(string specificationMarkdown) =>
            ReferenceSectionRegex.Replace(
                    specificationMarkdown ?? string.Empty,
                    string.Empty)
                .TrimEnd();

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private static string ComputeSha256(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                var hashBytes = sha.ComputeHash(bytes);
                
                var sb = new StringBuilder();
                foreach (var b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

    }
}
