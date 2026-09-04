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

---

# 도입 스윕 (Task 8 3·4단계) — 2026-09-04

계획 `2026-09-03-prd-derivation.md` Task 8 의 마지막 두 단계다. 기능은 `6f797c76` 으로
병합됐지만 **실재하는 문서에서 검사가 무언가를 잡는지는 미확인**이었고, 이 절이 그것을 닫는다.

작업공간: 격리 워크트리 `.worktrees/prd-sweep` (branch `feature/prd-adoption-sweep`, main `bcdbdbb8` 기준).
코퍼스 링크 셋 셋 다 연결. AI 제공자 `claude-cli` / `claude-sonnet-5` / effort `high`.

피어 조율: 피어 `reset-b8` 이 통제군 5판을 준비 중이었다. 확인 결과 얼어붙은 입력은
`output.bak-stage4-control-20260828/` 하나이고 `output/` 은 자유였다. 피어가 전수로 재 준 것:
`output/` 을 읽는 코퍼스 테스트 중 `docs/` 를 **파일명 없이 훑는 자리는 0건**이며
진짜 도화선은 `metadata.json` 이다 — 이 스윕은 `docs/Prd.md` 만 쓰므로 해당 없다.

## 합격 기준 (돌리기 전에 선언함)

1. 게이트: 실패 0 · 건너뜀 0 · `warning CS` 0. 통과 수 절대값은 게이트로 안 쓴다.
2. `Validate_ShouldFire_WhenASingleCharacterOfTheQuoteIsAltered` 통과 = 검사 생존.
3. 최소 3건 도출, 각 건 배너의 귀속 결함 수 기록.
4. **발화 0 은 합격이 아니라 의심 신호.** 0 이면 (2) 재확인 → 실재 문서에서 검사가
   정말 그 인용을 보는지 손으로 대조 → 그래도 0 이면 사실로만 적고 합격 선언 금지.
5. 발화가 나오면 최소 3건을 열어 오탐/진짜를 가르고, 오탐이면 원인을 고쳐 회귀를 남긴다.
   원장의 미뤄둔 항목과 같은 것이면 그 줄을 갱신한다.

## 잰 것

게이트(스윕 전, `bcdbdbb8`): 실패 0 · 건너뜀 0 · 통과 3506 · warning CS 0.
게이트(수정 후): 실패 0 · 건너뜀 0 · **통과 3512** · warning CS 0 — 정확히 신규 테스트 6건만큼 늘었다.
`Prd.md` 8건이 코퍼스에 생긴 뒤에도 HEAD 판 전체 수는 3506 그대로였다(피어의 사전 재기와 일치).

**도출 8건 (`output/Procedures`, 기존 `Prd.md` 0건이라 덮어쓰기 없음)**

| 대상 | 첫 판정 | 최종(배너) |
| --- | --- | --- |
| `dbo.UP_Util_Settle_Summary_AcqManual` | (미계측) | 0 |
| `dbo.UP_Util_PG_Client_CMRate_Ins` | 0 | 0 |
| `dbo.UP_UTIL_SETTLE_PROC_ETC` | 0 | 0 |
| `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC` | **1 발화** | 0 (교정 재호출이 지움) |
| `dbo.UP_UTIL_SETTLE_CANCEL_INS` | (미계측) | 0 |
| `dbo.UP_UTIL_STAT_PGCOLLECT_INS` | (미계측) | 0 |
| `dbo.UP_UTIL_SETTLE_INS` | (미계측) | 0 |
| `dbo.UP_UTIL_SETTLE_COMM_UPD` | (미계측) | **6** (`ConfidenceVocabulary`×3 · `EvidenceMissing`×3) |

**첫 판정만 재는 프로브 5건 (코퍼스에 쓰지 않음)**

| 대상 | 첫 판정 결함 |
| --- | --- |
| `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC` | 8 |
| `dbo.UP_UTIL_SETTLE_EXPECT_PROC` | 12 |
| `dbo.UP_UTIL_SETTLE_INS_EXTRA` | 0 |
| `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA` | **18** |
| `dbo.UP_Util_Settle_Summary` | **18** |
| **합계** | **56** |

### 계측 결함 하나 — 최종값만 보면 발화가 안 보인다

`PrdDerivationService` 는 첫 판정이 실패하면 교정 재호출 1회를 하고, 그 사실을
`Log.Information` 으로만 남긴다. 스윕 하네스에 Serilog 싱크가 없던 첫 배치 4건은
그래서 첫 판정이 가려졌다. 싱크를 달고서야 `EXCEPTION_PROC` 의 발화가 보였고,
프로브를 따로 만들고서야 발화의 **실제 규모(5편에서 56건)**가 보였다.

직접 대조할 수 있는 자리는 하나뿐이다 — `EXCEPTION_PROC` 은 스윕 실행에서 첫 판정 1건이
로그에 남고 최종 0으로 저장됐다. **같은 문서·같은 실행에서 재호출이 발화를 지운 것이
확인된 유일한 사례**이고, 지워진 양은 1건이다. 프로브의 56과 배너의 6을 빼서 소실량으로
읽어서는 안 된다 — 두 수는 문서 집합이 다르고(프로브 5편에 `COMM_UPD` 가 없다) 생성
실행도 다르다. 실제로 `EXCEPTION_PROC` 은 스윕 실행에서 1, 프로브 실행에서 8이 나왔다.
**같은 문서라도 생성이 다르면 첫 판정 수가 다르므로 회차를 넘겨 수를 옮길 수 없다.**

그래도 이 수치로 분명한 것은 남는다: **첫 판정의 발화 대부분은 최종값에 도달하지 않는다.**
배너만 보는 관찰자는 8편 중 7편을 「결함 0」으로 읽지만, 그것은 문서가 깨끗해서가 아니라
재호출이 덮었거나 애초에 재지 않았기 때문이다.

## 판정 — 발화 전량이 같은 원인의 오탐이었다

프로브 56건 + 코퍼스 6건, 전부 `ConfidenceVocabulary` 와 `EvidenceMissing` 이 **같은 행에서 짝으로** 났다.

실측한 행(`COMM_UPD`):

```
| REQ-IN-01 | …정산 기준일을 입력 조건으로… | ## 파라미터 목록 > "@pi_strYMD | CHAR(8) | 입력 | 명시 없음 | 정산 기준일 (YYYYMMDD)" | 도출 |
```

인용은 `Spec.md` 111행에 **글자 그대로 실재**한다. 즉 모델은 정확했고, 검사가 낸 두 진단
(「확신도가 `CHAR(8)` 이다」·「근거 칸이 형식이 아니다」)은 **둘 다 거짓**이었다.

원인은 `PrdDocumentParser.SplitRow` 의 날것 `body.Split('|')` 이다. 생성 프롬프트는 근거를
"verbatim 인용"으로 요구하는데 `Spec.md` 의 알찬 사실은 표 안에 산다 — 모델이 지시를
지킬수록 인용에 표 파이프가 섞이고, 행이 여덟 칸으로 터져 근거 칸에는 조각만, 확신도
칸에는 `CHAR(8)` 이 들어간다.

설계 §6.1-4 는 이미 「**표 파이프**를 정규화한 뒤 대조한다」고 못박았고 `NormalizeForQuoteMatch`
는 실제로 `|` 를 지운다. **비교기는 파이프를 감안하는데 그 앞의 행 분해기가 비교에 닿기도
전에 행을 부수고 있었다.** 저장소에는 이 왕복을 위한 중립 헬퍼 `MarkdownTableCellCodec` 이
이미 있고 `AiService`·`MechanicalValidator` 가 공유하는데, `PrdDocumentParser` 는 파이프 분해를
손수 다시 구현한 **네 번째 자리이자 유일하게 헬퍼를 안 쓰는 자리**였다 — R6(「같은 Spec
헤딩의 세 번째 소비자인데 폴백만 빠졌다」)과 같은 모양이다.

거짓 진단의 값은 배너에서 끝나지 않는다. 그것이 `BuildPromptFix` 를 타고 교정 재호출의
피드백이 되어 모델에게 「확신도를 9 에서 고쳐라」는 **실행 불가능한 지시**로 간다.

### Ruling R16: 행 분해를 공용 헬퍼로 옮기고, 터진 행은 계약 문법으로 도로 잇는다

세 갈래로 고쳤다.

1. `PrdDocumentParser.SplitRow` 가 `MarkdownTableCellCodec.SplitRow` 를 쓴다 — 이스케이프된
   `\|` 를 칸 경계가 아니라 칸 내용으로 되돌린다.
2. 칸이 계약(4)보다 많으면 `RejoinOverSplitEvidence` 가 근거 칸 문법(`## 헤딩 > "구절"`)을
   읽어 그 문법이 여는 칸부터 인용이 닫히는 칸까지만 도로 잇는다. **추측으로 붙이지 않는다.**
   문법이 없으면 손대지 않고 원래대로 고발되게 둔다 — 어긋난 행을 조용히 그럴듯하게
   만들면 검사가 문서 결함을 숨기게 된다. 칸 수가 계약과 같은 행은 아예 건드리지 않으므로
   **지금 통과하는 문서의 판정은 이 되살리기로 달라질 수 없다.**
3. 생성 프롬프트 규칙 4 에 「인용 안의 `|` 는 `\|` 로 적어라」를 더했다. 파서가 터진 행을
   되살리더라도, **사람이 읽는 표**는 프롬프트가 이스케이프를 시켜야 어긋나지 않는다.

근거: 이 오탐은 발화의 100% 였다. 고치지 않으면 스윕이 낸 유일한 신호가 전부 거짓이고,
그 거짓이 사람용 배너와 교정 루프 양쪽을 오염시킨다. 틀렸을 때 비용: 되살리기가 계약
문법을 잘못 읽으면 이미 어긋난 행에서만 근거 칸 경계가 밀린다 — 정상 행은 사정거리 밖이다.

회귀 6건을 남겼고 **네 건은 수정을 되돌리면 RED 임을 실측 확인**했다
(`Parse_ShouldKeepEscapedPipeInsideTheEvidenceCell`, `Parse_ShouldRecoverTheRow_WhenTheExcerptCarriesUnescapedPipes`,
`Validate_ShouldStayClean_WhenTheExcerptIsASpecTableRowWithPipes`, `Validate_ShouldStayClean_WhenTheExcerptEscapesItsPipes`).
나머지 둘은 되살리기가 **넘치지 않는지**를 고정한다
(`Parse_ShouldNotReassemble_WhenNoCellOpensTheEvidenceGrammar`, `GeneratePrdFromSpecAsync_ShouldTellTheModelToEscapePipesInsideTheExcerpt`).

### 고친 뒤 다시 잰 것 — 발화 62건이 0건이 됐다

**같은 산출물**을 고친 검사로 다시 재서 얻은 수다. 새로 생성한 문서로 바꿔치기한 것이
아니라, 발화를 냈던 바로 그 파일들을 다시 판정했다.

| 잰 대상 | 수정 전 | 수정 후 |
| --- | --- | --- |
| 프로브 초안 5편(`EXCEPTION_PROC` 8 · `EXPECT_PROC` 12 · `INS_EXTRA` 0 · `SUMMARY_EXTRA` 18 · `Settle_Summary` 18) | 56 | **0** |
| 코퍼스 저장본 `COMM_UPD`(배너가 결함 6건을 주장하던 그 파일) | 6 | **0** |
| **합계** | **62** | **0** |

발화의 **100%**가 한 결함에서 나왔다는 뜻이다. 이 저장소가 반복해서 겪는 형태이므로
수를 남긴다 — 다음 사람이 「검사가 62건이나 잡았다」를 문서 품질 신호로 읽지 않게.

코퍼스 8편 전부 결함 0.

**정정 이력(2026-09-04, 병합 후).** 이 절은 처음에 「프로브 4편 · 38 → 0, 합계 44」로
적혔다. 두 가지가 틀렸다.

1. **프로브는 5편이었다.** 백그라운드 로그가 끝나기 전에 읽고 그 수로 원장을 썼다 —
   다섯 번째(`dbo.UP_Util_Settle_Summary`, 18건)가 그 뒤에 끝났다. 초안이 임시
   디렉터리에 남아 있어 고친 검사로 다시 잴 수 있었고 역시 **18 → 0** 이었다.
2. **「38과 6의 차 32건이 조용히 지워졌다」고 피어에게 전했는데 성립하지 않는다.**
   두 수는 문서 집합이 다르다. 피어(`reset-b8`)가 분모를 확인하지 않은 채 확정으로
   쓰지 않고 되물어 잡혔다.

교훈 둘. **끝나지 않은 로그를 읽고 수를 확정하지 말 것** — 「읽지 말고 재라」의 변종이다.
그리고 **두 관측의 차를 소실량으로 읽기 전에 분모가 같은지 먼저 볼 것.** 이 원장이
44라는 틀린 수를 커밋 메시지(`5ddca01e`)에까지 실은 채 병합됐다는 사실 자체를 남긴다 —
지우면 다음 사람이 같은 자리에서 같은 실수를 한다.

### 앵커 — 거짓 배너의 원문 (`COMM_UPD`, 수정 전 저장본)

수정 전 저장본은 **재생성으로 덮였다.** 「고친 검사가 정말 조용해졌나」를 나중에 다시
물을 자리가 사라지지 않도록, 그 파일이 달고 있던 배너와 그것을 유발한 세 행을 여기
그대로 옮긴다. 원본 파일은 세션 임시 디렉터리에만 있었고 보존하지 않았다.

배너(거짓):

```
> **귀속 검사 미통과**: 아래 6건의 결함이 남아 있습니다.
> - `## 수행 조건 및 입력 계약 / REQ-IN-01` — 확신도는 '도출' 또는 '추정'이어야 합니다. 실제 값: 'CHAR(8)'
> - `## 수행 조건 및 입력 계약 / REQ-IN-01` — 근거 칸이 '## 헤딩 > "원문 구절"' 형식이 아닙니다.
> - `## 수행 조건 및 입력 계약 / REQ-IN-02` — 확신도는 '도출' 또는 '추정'이어야 합니다. 실제 값: 'INT'
> - `## 수행 조건 및 입력 계약 / REQ-IN-02` — 근거 칸이 '## 헤딩 > "원문 구절"' 형식이 아닙니다.
> - `## 예외 및 비기능 요구사항 / REQ-NFR-02` — 확신도는 '도출' 또는 '추정'이어야 합니다. 실제 값: '-1'
> - `## 예외 및 비기능 요구사항 / REQ-NFR-02` — 근거 칸이 '## 헤딩 > "원문 구절"' 형식이 아닙니다.
```

유발한 세 행과, 그 인용이 원본에 실재하는 자리:

| 행 | 근거 칸 | `Spec.md` 실재 위치 |
| --- | --- | --- |
| `REQ-IN-01` | `## 파라미터 목록 > "@pi_strYMD \| CHAR(8) \| 입력 \| 명시 없음 \| 정산 기준일 (YYYYMMDD)"` | 111행 (`## 파라미터 목록` 안) |
| `REQ-IN-02` | `## 파라미터 목록 > "@po_intRetVal \| INT \| 출력 \| 명시 없음 \| 반환값: 0=성공, …"` | 112행 (`## 파라미터 목록` 안) |
| `REQ-NFR-02` | `## 로직 흐름 요약 > "UPDATE 1 \| -1 \| @po_intRetVal"` | 689행 (`## 로직 흐름 요약`, 591행부터) |

세 인용 모두 자기가 지목한 절 안에 글자 그대로 있다. 배너가 여섯 번 말한 것은 전부
거짓이었고, 「확신도가 `CHAR(8)`·`INT`·`-1` 이다」는 실제로는 인용 안의 두 번째 칸이다.

**그리고 이 앵커는 텍스트로만 있지 않다.** 위 세 행과 명세서 세 줄을 글자 그대로 실은
검사 둘을 `PrdPipeInQuoteCorpusRegressionTests` 에 두었다(`be5d9971`). 파서를 `bcdbdbb8`
판으로 되돌리면 두 검사가 RED 이고 **결함 수가 정확히 6** — 실제 배너에 박혔던 수와 같다.
텍스트는 읽어야 알고 검사는 돈다.

앞서 남긴 회귀 여섯 건은 결함의 **형태**만 본뜬 합성 픽스처였다. 형태 픽스처는 계약을
지키지만 「이 입력에서 여섯 번 거짓 고발했다」를 증언하지 못한다 — 실물을 따로 실어야 한다.


| | 표 행 | 파서가 본 행 | 결함 | 행 단위 생존 | R7 의존 행 |
| --- | --- | --- | --- | --- | --- |
| 코퍼스 8편 합계 | 283 | 283 | 0 | 283 발화 · 0 침묵 | 0 |

## 검사가 죽지 않았음을 어떻게 알았나

「발화 0」을 문서 품질로 읽으려면 검사의 생존을 따로 세워야 한다(선언한 기준 4).
합성 픽스처(§8.2)만으로는 부족해 **실재 코퍼스 문서의 모든 행**에 같은 주입을 했다:
행마다 인용의 마지막 글자 하나를 바꿔 넣고 그 행이 발화하는지 봤다.

- 283행 중 **283행 발화, 침묵 0**.
- 파서가 본 행 수 = 파일의 `REQ-` 표 행 수 (계약 밖 섹션에 숨은 행 0 — 최종 리뷰 F1 이
  걱정한 구멍이 이 8편에는 없다).

## 미뤄둔 항목 대조

- **R7(공백 전삭제 정규화, park)** — 「T8 스윕에서 이 형태가 실제로 나오는지 보고, 나오면
  그때 좁힌다」였다. **실측: 코퍼스 8편 283행 중 이 구멍에 기대는 행 0.** 공백을 지우는
  대신 하나로 접어도 매치가 깨지는 행이 하나도 없었다. **park 을 유지한다** — 이제
  근거가 추정이 아니라 실측이다.
- **Task 2 minor(다중 따옴표에서 `LastIndexOfAny` 오절단)** — 스윕에서 재현되지 않았다.
  감사 하네스가 두 편에서 「인용 추출 실패 1」을 냈으나 확인 결과 **하네스 자신의 한계**다
  (되살린 칸의 `" | "` 이음새가 원문 줄의 간격과 달라 치환이 실패). 검사기 자신은 그 행들에
  결함 0 을 냈다. **이 줄은 미뤄둔 채로 둔다.**
- **Task 3 minor(느슨 폴백 `Contains` 부분 문자열 겹침)** — 계약 헤딩 넷이 여전히 서로
  부분 문자열이 아니라 발동하지 않았다. 그대로 둔다.
- **Task 3 minor(`EvidenceSourceNotAllowed` 가 `continue` 안 해 중복 발화)** — 이번 발화가
  전부 다른 두 종류였으므로 재현되지 않았다. 그대로 둔다.

## 남은 사실과 다음

- `COMM_UPD` 는 거짓 배너(결함 6건)를 단 채 저장됐었다. 고친 코드로 **재생성**해 걷어냈다.
  나머지 7편은 배너가 처음부터 깨끗했으므로 손대지 않았다.
- 이 스윕은 `output/Procedures` 14건 중 **8건**을 덮었다. 나머지 6건은 도출하지 않았다.
- **후속 후보(범위 밖) — 반려된 초안이 아무 데도 안 남는다.** `PrdDerivationService` 는
  교정 재호출이 성공하면 첫 초안을 버린다. 그래서 검사가 **무엇을** 잡았는지의 실물
  증거가 그 자리에서 파괴되고, 남는 것은 `Log.Information` 의 개수뿐이다. 이번 스윕이
  발화 규모를 보려고 별도 프로브를 만들어야 했던 이유가 정확히 이것이고,
  `PrdPipeInQuoteCorpusRegressionTests` 의 실물 픽스처도 초안이 임시 디렉터리에 **살아
  있는 동안** 건져서 만든 것이다 — 조금만 늦었으면 못 만들었다.

  이것은 이 기능만의 문제가 아니다. 피어 `reset-b8` 이 통제군 하네스에서 같은 형태를
  독립적으로 찾았다(2026-09-04): 산출물 트리가 **채택본만** 담아 L1이 반려한 중간 문서가
  아무 데도 없고, 그래서 판3 사건(깨진 mermaid 하나가 6회차를 태운 건)의 실물 픽스처를
  **원리적으로 만들 수 없다**. 그쪽은 하네스의 관측 결함으로 올렸다.

  **검사가 잡은 것을 사람이 나중에 검증하려면 반려된 판이 어딘가 남아야 한다.** 여기서
  고치지 않는 이유는 「초안을 어디에 어떤 이름으로 남길 것인가」가 설계 결정이고
  (산출물 트리를 오염시키지 않으면서), 이 스윕의 범위가 아니기 때문이다.

  이 항목은 `docs/known-defects.md` 의 「정책 결정이 선행되어야 하는 것」 표에 **반복 2**로
  올라가 있다(피어 `reset-b8`, 브랜치 `docs/rejected-draft-preservation` — 이 원장을 쓰는
  시점에 main 미병합). 정책으로 분류한 이유는 **두 파이프라인이 같은 규약을 써야 하기
  때문**이다 — 한쪽만 정하면 오케스트레이터의 L1 반려본과 여기의 첫 초안이 서로 다른
  자리에 다른 이름으로 쌓인다.
- **범위 밖으로 남긴 것**: 이스케이프 안 된 파이프가 든 행은 되살려도 **사람이 볼 때는
  여전히 표가 어긋난다.** 그것을 결함으로 고발하려면 §6.1 의 여섯 가지에 일곱째를
  더해야 하므로 설계 변경이다. 프롬프트 규칙으로 예방만 하고 검사는 넓히지 않았다.
