# PRD 도출 구현 실행 원장

이 문서는 `2026-09-03-prd-derivation.md` 계획을 실행하며 남긴 기록이다. 계획서가
「무엇을 만들 것인가」라면 이것은 **「실제로 무슨 일이 있었는가」**다 — 사람 대신
내린 판정 15개, 그 근거와 틀렸을 때의 비용, 고치지 않고 미뤄둔 결함, 그리고
리뷰가 잡아낸 것들이다.

남기는 이유는 판정 때문이다. 이 작업은 사람에게 매번 묻지 않고 진행됐고, 그 대가로
누군가 나중에 「왜 이렇게 되어 있나」를 물었을 때 답할 자리가 필요하다. 특히 R6·R13·
R14 는 계획서 자체의 결함을 고친 것이라, 계획서만 읽으면 코드와 어긋나 보인다.

원본은 세션 작업공간(`.superpowers/sdd/2026-09-03-prd-derivation/`)에 있었고 그
디렉터리는 병합 후 정리됐다. 본문에 나오는 그 경로의 브리프·보고서 파일들은 더 이상
없다. 커밋 해시는 살아 있다.

기능은 `6f797c76` 으로 main 에 병합됐다. **도입 스윕은 아직 안 돌았다** — 실재하는
문서에서 검사가 발화하는지는 미확인이다.

---

# SDD ledger — plan: docs/superpowers/plans/2026-09-03-prd-derivation.md

Spec: docs/superpowers/specs/2026-09-03-prd-derivation-design.md (읽음)
Worktree: /Users/payletter/git-root/ReSet/.worktrees/prd-derivation (branch feature/prd-derivation)
Baseline @7ad1fb9d: dotnet test 실패 0 · 건너뜀 0 · 통과 3441 / warning CS 0
코퍼스 링크 셋 확인(건너뜀 0이 증거): output, output.bak-2026-08-22, output.bak-stage4-control-20260828

외부 제약(피어 reset-57, 배치 계획서 5판 진행 중):
- 공유 체크아웃 /Users/payletter/git-root/ReSet 의 소스·main 병합·checkout·rebase 금지
- 빌드/테스트는 이 워크트리 안에서만
- 코퍼스 셋은 읽기 전용. 워크트리에서 CLI 재생성 금지
- 병합은 5판 종료 연락 뒤 사람이 결정 → finishing 단계에서 사용자에게 올린다

## 사전 충돌 검사 (dispatch 전)

### 파일·인터페이스를 공유하는 태스크 쌍
| 쌍 | 공유 대상 | 생산 → 소비 | 발견 |
| --- | --- | --- | --- |
| T1→T2 | PrdAttributionValidator.cs, PrdAttributionValidatorTests.cs | PrdDefectType/PrdRequirement/PrdSectionContract → 검사 2 추가 | 순차 필수. T1이 enum 7값 중 3만 사용 → YAGNI 오탐 소지 (R3) |
| T2→T3 | 같은 두 파일 | TryParseEvidence/PrdEvidenceReference → 대조 검사 | 순차 필수. 병합 지점 명확 |
| T1→T4 | PrdValidationResult, PrdDefect | ctor(IReadOnlyList<PrdDefect>) → 테스트가 new(List) 사용 | 일치 |
| T1→T6 | PrdValidationResult | .IsValid/.Defects | 일치 |
| T4→T6 | PrdAttributionReport | BuildBanner/BuildPromptFix | 일치 |
| T5→T6 | IAiService | GeneratePrdFromSpecAsync 5인자 순서 | 일치 |
| T6→T7 | OutputPathResolver 상수, PrdDerivationOutcome, IPrdDerivationService | Find/DeriveAsync | 일치 |
| T7→기존 Program.cs | activeCts, actorEffort, aiService, outputDir | — | **결함: activeCts는 분기마다 지역 선언(995·1223·1328). 새 분기가 선언 없이 쓰면 컴파일 실패 (R2)** |

### 태스크 자체 정합성
| 태스크 | 발견 |
| --- | --- |
| T1 | Interfaces 블록은 Parse(string), 코드는 Parse(string?) — 표기 불일치 (R5). Validate가 specMarkdown을 안 씀(T3에서 씀) — 미사용 매개변수 (R4) |
| T2 | 자체 정합 |
| T3 | 자체 정합. Validate 첫머리 병합 지점 명확 |
| T4 | 자체 정합 |
| T5 | **결함: 두 프로젝트 모두 <Nullable>enable</Nullable>. Assert.Contains(…, result.SystemPrompt)는 string? 인자라 CS8604 → 「경고 0」 게이트 위반 (R1)** |
| T6 | 자체 정합 |
| T7 | 위 표의 activeCts 결함 (R2) |
| T8 | 자체 정합 (코드 없음) |

## 규정 (Rulings)

Ruling R1: T5 테스트는 `Assert.NotNull(result.SystemPrompt);` 로 먼저 확정한 뒤 지역 변수로 받아 대조한다 — 계획 원문의 `Assert.Contains(x, result.SystemPrompt)` 직접 사용은 CS8604를 낸다. 근거: 「경고 0」이 Global Constraint이고 spec §8.4가 그것을 게이트로 못박는다. 틀렸을 때 비용: 테스트 표현이 두 줄 길어질 뿐 검사력은 동일하다.

Ruling R2: T7의 새 메뉴 분기는 자기 `using var activeCts = new CancellationTokenSource(); _currentCts = activeCts;` 로 열고, 분기를 빠져나가기 전에 `_currentCts = globalCts;` 로 복원한다(1223-1224·1293 관행). 근거: activeCts는 공유 변수가 아니라 분기 지역 변수다. 틀렸을 때 비용: Ctrl+C가 이 분기에서 전역 CTS를 가리켜 취소가 어긋난다.

Ruling R3: PrdDefectType 7개 값을 T1에서 전부 선언한다(T1은 3개만 발화). 근거: 열거형을 세 번 고쳐 키우면 T2·T3의 diff가 계약 변경으로 보인다. 틀렸을 때 비용: T2·T3가 취소되면 미사용 값 4개가 남는다.

Ruling R4: T1의 `Validate(prdMarkdown, specMarkdown)`는 specMarkdown을 쓰지 않은 채 유지한다. 근거: T3에서 시그니처를 바꾸면 T1·T2가 쓴 테스트 전부가 흔들린다. 틀렸을 때 비용: T1 리뷰가 미사용 매개변수를 지적할 수 있다 — 브리프에 계획 의도로 명시해 둔다.

Ruling R5: `PrdDocumentParser.Parse`의 인자는 `string?`다(`MarkdownSectionLocator.SplitLines(string?)`와 같은 결). 계획 Interfaces 블록의 `string` 표기가 낡았다. 틀렸을 때 비용: 없음 — 호출부는 모두 비-널을 넘긴다.

## 진행

Task 1: 구현자 DONE (commit 820f7e80). 보고: warning 0, 실패 0, 건너뜀 0. 태스크 리뷰 대기.
Task 1: complete (commits 7ad1fb9d..820f7e80, review clean — spec ✅, 품질 승인)
Task 1: minor (deferred): PrdAttributionValidatorTests.cs:78-95 — 「추정」 행 테스트가 앞 테스트와 같은 경로만 밟아 신호를 더하지 않는다(계획 원문의 테스트 코드 그대로)
Task 1: minor (deferred): PrdDocumentParser.IsHeaderOrSeparator 에 왜-주석이 없다(저장소 관행 대비)
Task 2: 구현자 DONE (commit ed9434e5). 보고: warning 0, 실패 0, 건너뜀 0. 태스크 리뷰 대기.
Task 2: complete (commits 820f7e80..ed9434e5, review clean — spec ✅, 품질 승인)
Task 2: minor (deferred, Task 3 관련): TryParseEvidence 가 따옴표 쌍이 둘 이상인 칸(`## H > "a" 그리고 "b"`)에서 LastIndexOfAny 때문에 Quote 를 엉뚱하게 자른다. Task 2 는 Heading 만 쓰므로 무해. Task 3 이 인용 내용을 대조하므로 그때 문제가 된다 → Task 3 dispatch 에 포인터 전달함.
Task 3: 구현자 DONE (commit eb2e4946). 보고: warning 0, 실패 0, 건너뜀 0. 태스크 리뷰 대기.

Task 3 리뷰: spec ✅, 품질 Important 2건 (둘 다 계획 원문 코드에서 유래) → 규정 후 수정 라운드.

Ruling R6: 「헤딩 정확 일치만 쓴다」는 결함으로 인정하고 고친다. 근거: MechanicalValidator.LocateCrudSection(5600-5625)이 바로 이 이유로 exact→loose 폴백을 넣었고(`## 3. CRUD 분석` 때문에 매핑 대조가 조용히 꺼져 「16개 컬럼이 산문으로 뭉개져도 L1 통과」), BatchPlanAssembler:101도 꼬리표 변형으로 같은 일을 겪었다. PrdAttributionValidator는 같은 Spec 헤딩의 세 번째 소비자인데 폴백만 빠졌다. 헤딩 드리프트가 있는 Spec에서는 그 절의 모든 행이 거짓 결함이 되어, 도입 스윕(T8)의 발화가 전부 오탐이 된다. 고칠 곳은 두 군데다 — ExtractSectionBody(실재 판정)와 Task 2의 AllowedSources 대조(허용 원천 판정). 한쪽만 고치면 드리프트 입력이 다른 쪽에서 죽는다. 틀렸을 때 비용: 느슨 매칭이 `## 개요`를 `## 개요 및 배경` 같은 다른 절에 붙일 수 있다 — 그래서 정확 일치를 먼저 시도한다.

Ruling R7: 「정규화가 공백을 전부 지워 문장 경계를 붙인다」(예: "한다.중복검사"가 통과)는 실재하는 오탐 통과이지만 고치지 않고 park 한다. 근거: 공백 런을 하나로 접는 방식으로 바꾸면 이 구멍은 닫히지만, 한국어 LLM 출력에서 흔한 띄어쓰기 변이가 전부 거짓 경보가 된다. spec §6.1의 근거 문단이 「오탐이 잦은 검사는 곧 꺼진다」를 명시적으로 더 나쁜 실패로 두었고, §6.2가 이미 「인용의 실재만 검증하며 대응은 미검증」이라고 독자에게 고지한다 — 이 구멍은 그 고지된 한계의 좁은 변종이다. 틀렸을 때 비용: 인용을 지어내되 공백만 빠뜨린 항목이 통과한다. T8 스윕에서 이 형태가 실제로 나오는지 보고, 나오면 그때 좁힌다.
Task 3: fix round 1/5 (A1·A2 둘 다 addressed, 신규 1건 open — 가드 테스트 둘이 비변별적: 하나는 기존 테스트의 바이트 단위 복사본, 하나는 느슨 경로를 안 밟는다. 되돌려도 초록불; commits eb2e4946..e2d08c94)
Task 3: minor (deferred): 느슨 폴백이 Contains 라 서로 다른 계약 헤딩이 부분 문자열로 겹치면 엉뚱한 절에 붙을 수 있다. 현재 계약 헤딩 넷은 서로 부분 문자열이 아니라 발동하지 않음(재리뷰가 실제 입력을 못 만들었다). 계약에 헤딩을 더할 때 이 줄을 다시 볼 것.
Task 3: minor (deferred): EvidenceSourceNotAllowed 가 continue 하지 않아 허용 밖 원천을 인용한 행이 원천 결함과 인용 결함을 동시에 낼 수 있다(중복 발화, 오작동 아님).
Task 3: fix round 2/5 (복사본 테스트 삭제·절 결정 가드 추가, 그러나 항목 2를 교체 없이 삭제해 1건 open; commit 79b11730). 재리뷰는 라운드 3과 합쳐 실행.
Task 3: fix round 3/5 (누락 가드 추가; commit 3a18977a). 구현자 자기보고: 과잉정규화 되돌림 → RED, 원시동등 되돌림 → GREEN(정직 보고, 이 가드의 목적은 과잉정규화 탐지).
Task 3: 라운드 2-3 재리뷰 결과 — 가드 (b) ADDRESSED, 가드 (a) 약함, 신규 Important 1건(단언 축소) + 프로세스 위반 1건(테스트 전용 라운드에 운영 코드 변경).

Ruling R8: 가드 (a)의 「실체」를 계속 요구한 것은 내 오판이다. 그 위험(허용 밖 원천이 정규화 때문에 허용으로 바뀜)은 AllowedSources 넷이 주제적으로 다른 문자열이라 성립하지 않는다 — 재리뷰도 실제 입력을 못 만들었다. 남은 과잉정규화 가드(NormalizeHeading이 전부 빈 문자열로 붕괴하면 RED)는 그 자체로 지킬 값이 있으므로 그대로 수용하고 더 요구하지 않는다. 틀렸을 때 비용: 계약에 주제가 비슷한 헤딩을 나중에 더하면 그때는 진짜 충돌 가드가 필요해진다 — 위의 Contains 부분문자열 minor 와 같은 줄에서 다시 볼 것.

Ruling R9: 단언 축소는 되돌린다. `Validate_ShouldToleratePrefixedHeading_WhenSpecHasNumberedHeading` 의 `DoesNotContain(EvidenceQuoteNotFound)` 가 `&& RequirementId == "REQ-DATA-01"` 로 좁혀져 REQ-NFR-01 의 인용이 느슨 경로로 해석되는지가 검사에서 빠졌다. 보고서에 설명이 없다. 실측: NormalizeHeading 은 `#`을 이미 벗기므로 라인 91 의 `"## " + ...` 제거는 HeadingText 관점에서 무동작이고, 따라서 축소를 정당화할 근거가 없다. 틀렸을 때 비용: 되돌린 단언이 실제로 실패하면 라인 91 변경이 무동작이 아니라는 뜻이며, 그때는 그 사실 자체가 발견이다.
Task 3: fix round 4/5 (2건 addressed, 0 open — 단언 복원·운영 코드 되돌림; commit 34aad7aa). 재리뷰: diff 정확히 2줄, 보고서 주장과 일치, 복원 단언이 올바른 이유로 통과.
Task 3: complete (commits ed9434e5..34aad7aa, review clean, 수정 4라운드 · parked 0)
Task 4: 구현자 DONE (commit 44081703). 보고: warning 0, 실패 0, 건너뜀 0, 신규 파일 2개만. 태스크 리뷰 대기.

Task 4 리뷰: spec ✅, 품질 Important 2건 (둘 다 계획 원문 코드 유래) → 규정 후 수정 라운드 1.

Ruling R10: 모델 생성 문자열(RequirementId·Message 안의 인용)이 배너에 이스케이프 없이 들어가는 것을 고친다. 리뷰가 실측으로 좁혀준 범위 — Section 은 화이트리스트라 안전하고, 줄바꿈은 표 행이 물리적 한 줄이라 파서를 통과하지 못하므로 인용 블록 탈출은 불가능하다. 남는 것은 백틱·강조 문자가 한 불릿의 렌더링을 깨고 안심시키는 스타일 조각을 끼워 넣는 것이다. CAUTION 머리와 고지 문단은 결함 내용에서 파생되지 않아 살아남는다. 그래도 고치는 이유: 이 컴포넌트의 유일한 임무가 사람에게 신뢰할 수 있게 보이는 것이고, 고치는 비용이 몇 줄이다. 틀렸을 때 비용: 이스케이프가 과하면 정상 메시지의 가독성이 조금 나빠진다.

Ruling R11: 「결함이 있는 분기에서도 고지 문단이 남는가」를 단언하는 테스트를 더한다. 지금 문단이 두 분기에 다 붙는 것은 코드 모양의 부수 효과일 뿐 어떤 단언도 그것을 고정하지 않는다. 누군가 「정리」하며 그 문단을 if 안으로 옮기면, 결함을 안은 문서가 실제보다 확신에 찬 배너를 달고 나가는 — 이 기능이 막으려던 바로 그 실패가 네 테스트 모두 초록불인 채로 되살아난다. 틀렸을 때 비용: 없다. 단언 한 줄이다.

Task 6 참고(리뷰가 넘긴 것): PrdAttributionReport 의 배너와 VerificationDocumentFormatter 의 머리말이 한 문서에서 합쳐질 때 두 인용 블록이 어떻게 렌더링되는지 Task 6 이 확인할 것.
Task 4: fix round 1/5 (Finding 2 addressed, Finding 1 부분 — 백틱·강조는 막혔으나 Message 의 대괄호/꺾쇠가 남아 링크·인라인 HTML 주입 가능; commit 3faba183)

Ruling R12: 살균은 검증기 경계가 아니라 보고 계층(PrdAttributionReport)에 둔다. 근거: 같은 문자열이 BuildPromptFix 로도 가는데 그쪽 독자는 모델이라 마크다운 렌더링이 무의미하고, 검증기에서 미리 깎으면 모델에게 되돌리는 교정문의 인용이 원문과 달라져 대조가 어긋난다. 렌더링 안전은 렌더링하는 쪽의 책임이다. 틀렸을 때 비용: 배너 말고 다른 마크다운 소비자가 생기면 각자 살균해야 한다.
Task 4: fix round 2/5 (1건 addressed, 0 open — 대괄호·꺾쇠 무력화; commit 1ee60fdb). 재리뷰: 주입 5형태 전부 차단, 자체 메시지 무손상, 신규 테스트 2건 되돌림 시 RED.
Task 4: complete (commits 34aad7aa..1ee60fdb, review clean, 수정 2라운드 · parked 0)
Task 4: minor (deferred): 맨 URL(https://... 만 있는 형태)은 GFM 자동 링크로 렌더링될 수 있다 — 대괄호·꺾쇠 치환의 사정거리 밖. 텍스트가 그대로 보이므로 「안심시키는 위조」는 아님.
Task 4: minor (deferred): 라운드 1에서 온 `_`→`-` 치환은 줄 시작에서라면 목록/수평선이 될 수 있으나 Message 는 항상 줄 중간이라 현재 무해.
Task 5: 구현자 DONE (commit 128f1494). 보고: warning 0, 실패 0, 건너뜀 0, 파일 3개(IAiService·AiService·신규 테스트). 태스크 리뷰 대기.
Task 5: complete (commits 1ee60fdb..128f1494, review clean — spec ✅, 품질 승인, findings 0)
Task 6: 구현자 DONE (commit 812881c9). 보고: warning 0, 실패 0, 건너뜀 0, 파일 4개.

Ruling R13: 문서 조립 순서를 계획과 다르게 한 구현자의 판단을 채택한다. 계획은 `BuildBanner(...) + FormatUnverifiedDocument(body, ...)` 였는데, 그러면 FormatUnverifiedDocument 가 만드는 YAML 머리말이 파일 offset 0 이 아니라 배너 뒤에 놓여 머리말로 파싱되지 않는다. 구현자가 `FormatUnverifiedDocument(banner + body, ...)` 로 뒤집고 두 인용 블록이 별개 콜아웃으로 렌더링됨을 실측 확인했다. 이것은 내 계획의 결함이고 구현자가 잡아 공개했다. 틀렸을 때 비용: 배너가 메타데이터 머리말 아래로 내려가 첫 화면에서 덜 눈에 띈다 — 머리말이 깨지는 것보다 낫다.
Task 6: complete (commits 128f1494..812881c9, review clean — spec ✅, 품질 승인, Minor 2)
Task 6: minor (deferred, 최종 리뷰가 병합 전 수정 여부를 판단할 것): 저장된 파일의 YAML 머리말이 offset 0 에서 시작하는지를 단언하는 테스트가 없다. R13 의 순서 뒤집기가 지키려는 바로 그 성질인데 손으로만 확인됐다 — 정상 경로 테스트에 Assert.StartsWith("---", written) 한 줄이면 닫힌다. **최종 리뷰의 우선 후보로 지목한다.**
Task 6: minor (deferred): File.WriteAllTextAsync 는 원자적이지 않아 쓰기 도중 취소되면 잘린 Prd.md 가 남을 수 있다. 계획 원문 코드와 동일하며 저장소의 기존 관행과도 같다.
Task 7: 구현자 DONE (commit da9a34ae), 그러나 자기 보고로 계획 코드의 결함 1건 공개 → 수정 라운드.

Ruling R14: MultiSelectionPrompt 의 선택 결과를 `picked.Any(p => p.StartsWith(t.Label))` 로 대상에 되돌리는 계획 코드는 결함이므로 고친다. 실측: output/Procedures 14건 중 접두사 충돌이 4쌍 있다
  dbo.UP_UTIL_SETTLE_INS ⊂ ..._INS_EXTRA ⊂ ..._INS_EXTRA4PLCARD, dbo.UP_Util_Settle_Summary ⊂ ..._AcqManual
따라서 ..._INS_EXTRA4PLCARD 하나만 골라도 앞의 둘이 함께 선택되어, 사용자가 고르지 않은 SP 의 Prd.md 를 덮어쓴다. 덮어쓰기 확인은 「고른 집합」 기준으로 물었으므로 확인조차 우회될 수 있다. 구현자가 「브리프 원문 로직」이라며 남겨 두고 공개했다 — 공개는 옳았고, 계획이 시킨 것이라는 이유로 남길 사안은 아니다. 틀렸을 때 비용: 없음. 표시 문자열을 키로 정확히 되돌리면 동작이 좁아질 뿐이다.
Task 7: fix round 1/5 (접두사 선택 결함 addressed — PrdTargetSelection.Resolve 정확 조회로 격상; commit 4ca4be6d). 태스크 리뷰: spec ✅, Important 1건(선택 목록 미escape).

Ruling R15: MultiSelectionPrompt 항목의 Markup.Escape 누락을 고친다. 근거: AGENTS.md 범주 3 이 외부 문자열의 escape 를 규칙으로 두고, Program.cs 의 다른 선택 프롬프트 넷(1214·1307·1529·1803)이 전부 지키는데 이 자리만 빠졌다. 다만 함정이 있다 — escape 한 문자열을 항목으로 넘기면 프롬프트가 되돌려주는 것도 그 문자열이므로, 사전 키를 escape 전 문자열로 두면 모든 선택이 조회에 실패해 조용히 아무 일도 안 하게 된다. 표시 문자열을 만드는 함수 하나가 항목과 키를 **동시에** 만들어야 한다. Core 는 Spectre 를 참조할 수 없으므로 escape 는 CLI 가 하고 Resolve 가 라벨 선택자를 받는다. 틀렸을 때 비용: 선택자 배선을 틀리면 메뉴가 조용한 무동작이 된다 — 그래서 그 실패 형태를 테스트로 고정하게 한다.
Task 7: fix round 2/5 (1건 addressed, 0 open — 단일 델리게이트로 항목·키 동시 생성; commit 95111290). 재리뷰: 발산 불가·round1 정확성 보존·escape 순서 정확·신규 테스트 2건 되돌림 시 RED.
Task 7: complete (commits 812881c9..95111290, review clean, 수정 2라운드 · parked 0)
Task 7: minor (deferred, 내 지시 실수): Program.cs 의 다른 선택 프롬프트 넷(1214·1307·1529·1803)은 AddChoices 에 원본을 넣고 `.UseConverter(x => Markup.Escape(x))` 로 렌더링만 escape 한다 — Spectre 가 원본 T 를 돌려주므로 발산 위험이 아예 없고 2인자 Resolve 로 충분했다. 내가 기존 관용구를 확인하지 않고 오버로드 형태를 지시했다. 현재 코드는 정확하고 테스트로 고정돼 있으므로 되돌리지 않고 최종 리뷰의 단순화 후보로 넘긴다.
Task 8: 문서 절반 complete (commit 174dcd78 — README·AGENTS·architecture·output-artifacts). 게이트 재확인: warning 0, 실패 0, 건너뜀 0.
Task 8: 도입 스윕(브리프 3·4단계)은 보류 — API 크레딧 소모 + 공유 코퍼스 쓰기라 사람 승인 필요. 최종 리뷰 뒤 병합과 함께 사용자에게 올린다.

최종 전체 브랜치 리뷰(opus, 16커밋): 병합 차단 사유 없음. 안전장치는 프롬프트→파서→검사기→배너→저장까지 전 경로에서 유지되며, 생성기와 검사기가 진짜로 한 계약(PrdSectionContract)을 읽는다. 문서 넷은 과장하지 않는다.

권고 3건 → 한 번의 수정 물결로 처리:
  F1 (Important) 깨끗한 배너가 "모든 요구 항목의 근거 인용이 …실재함을 확인했습니다" 라고 말하는데, 파서는 정확히 일치하는 다섯 헤딩 사이의 행만 본다. 모델이 계약 밖 섹션(## 기타 요구사항)에 요구 표를 하나 더 만들면 결함 0 으로 깨끗한 배너가 붙고 그 행들은 아무도 검사하지 않았다. 「검사된 요구 항목의」로 좁힌다.
  F2 저장된 파일의 YAML 머리말이 offset 0 에서 시작하는지 단언 없음(R13 이 지키려는 성질). 되돌리면 다섯 테스트 전부 초록불인 채 모든 Prd.md 의 --- 가 수평선으로 렌더링된다.
  F3 정상 경로에서 배너가 저장 파일에 실제로 들어갔는지 단언 없음. 서비스에서 BuildBanner 를 if 안으로 옮기면 다섯 테스트 초록불인 채 모든 깨끗한 문서에서 고지가 사라진다 — R11 이 보고 계층에서 막은 것을 서비스 계층에서 못 막고 있다.

리뷰어의 R8 판정: 내 결론에 동의(입력이 존재할 수 없다), 그러나 과정은 지적 — 라운드 1 에서 리뷰어에게 「실패 입력을 만들어 보라」고 물었어야 했고 그러면 한 라운드로 끝났다. 두 라운드로 단언 하나를 샀다.
리뷰어의 R15 판정: 내가 원장에 적은 것보다 비용이 크다 — 실패 형태가 불가능한 관용구 대신 가능한 기제를 지시하고 테스트로 막게 했으며, 그 대가로 남은 공개 API 는 이유보다 오래 남는다. 되돌리지는 말되 반복하지 말 것.
후속 후보(범위 밖, 병합 후): PRD 가 근거 Spec 의 검증 상태를 싣지 않는다(sourceOutcome: null). L1Exhausted Spec 에서 나온 PRD 가 합격 Spec 에서 나온 것과 똑같이 읽힌다.
최종 수정 물결: 3건 전부 addressed (commit 326679d2). 재리뷰: F1 한 단어·문장 참, 기존 테스트 무손상, F2·F3 단언 둘 다 명명된 변형에서 RED, 「미검증」은 배너에만 존재. 병합 차단 없음.
브랜치 완성: 18 커밋 (7ad1fb9d..326679d2). 남은 결정 둘 — 도입 스윕(API 크레딧+공유 코퍼스 쓰기), main 병합. 둘 다 사람 결정.
