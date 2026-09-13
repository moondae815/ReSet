# Consolidator 를 OpenRouter `openai/gpt-5.6-sol` 로 — 전환 판정 (사전 선언)

**작성 시점**: 2026-09-13, **실행 전**. 합격선을 결과보다 먼저 못박는다.
**코드**: main `faa9a5f4` + 스크립트 옵션(`5b214f32`)·라우팅 덮어쓰기 잠금 시험(`4b6e9ed8`).

## 0. 무엇을 바꾸는가 — 하나만

기준선은 `POQSettleBatch10`(2026-09-13, 계획 시도 1 회 판): claude-cli / `claude-sonnet-5` · 47 분 · 45 호출 · 점수 80.
이 회차는 **Consolidator 모델 하나만** 바꾼다. 시도 예산(1 회)·`Narrow`·입력 SP·Critic(`z-ai/glm-5.3`)은 그대로다.

- `--plan-only` 에서 계획을 만드는 것은 Actor 가 아니라 **Consolidator** 다 — Actor 는 이 경로에서 불리지 않는다
  (`VerificationPipelineOrchestrator` 의 `_consolidatorService`). 그래서 옵션도 Consolidator 에 건다.
- **청구 단가는 실호출로 확인했다**(`gen-1789294330-2zc0pU455954FzknQmUf`): `discount: 0.5` 표시에도 **$2/$10 per 1M**.
- 저장소 라우팅 기본값은 `Order: ["openai","azure"]` + 폴백 켬(가용성용, 시험이 잠금). 측정 판은 백엔드가 섞이면 안 되므로
  **이 판만** 환경변수로 `["openai"]` + 폴백 끔으로 덮는다(`PLANONLY_OPENROUTER_ONLY_BACKEND=openai`).

## 1. 1 단계 — 앵커 프로브 (첫 생성만, 5 단계)

```
PLANONLY_PROBE_ANCHORS=1 PLANONLY_CONSOLIDATOR_PROVIDER=OpenRouter \
PLANONLY_CONSOLIDATOR_MODEL=openai/gpt-5.6-sol PLANONLY_OPENROUTER_ONLY_BACKEND=openai \
scripts/run-plan-only-job.sh POQSettleBatch10
```

재료는 Batch10 의 목차·골격. 명세서가 SELECT 행을 선언한 단계는 S08·S11·S12·S14·S15 다(Batch10 목차 기준).

| # | 항목 | 합격 | 떨어지면 |
| :-- | :--- | :--- | :--- |
| H1 | 실행 | 종료 코드 0 · 프로브 `REPORT.md` 생성 · 대상 단계 전부 본문 생성 | **멈춘다** — 파이프라인·파싱 결함이다 |
| H2 | 라우팅 | 로그의 요청 본문 전부 `"order":["openai"]`·`"allow_fallbacks":false`, 응답 `provider` 전부 `OpenAI` | **멈춘다** — 판이 섞인 백엔드로 돈다 |
| H3 | 비용 | 로그 `OpenRouter 토큰 사용량` 합산 × $2/$10 **≤ $5** | 멈추고 보고 — 본판 추정($20)이 틀렸다 |
| H4 | 앵커 계약 | 도달률 5/5 · 지어낸 서수 0 · 접어 넣음 0 | **본판 전에 보고** — 모델이 계약을 안 따른다(파이프라인 결함은 아님) |

**예측**: 비용 약 $1.6(Batch10 첫 생성 토큰 × GPT 단가), 출력이 2 배면 약 $2.9. 시간은 모름(claude-cli 프로브 13 분).

**H1·H2 가 둘 다 합격이어야 2 단계(GPT 1 회 판, 약 $20)로 간다.** H4 불합격이면 결과를 보고하고 본판 여부를 사람이 정한다.

### 1 단계 결과 (2026-09-13 19:22:20 ~ 19:35:00 · 12 분 40 초)

실행 워크트리 `8c6a218f` · 종료 코드 0 · 로그 `output/logs-probe-POQSettleBatch10-20260913-192220` ·
보고서 `output/probes/POQSettleBatch10-20260913-192222/REPORT.md`.

| # | 합격선 | 실측 | 판정 |
| :-- | :--- | :--- | :--- |
| H1 | 실행 | 종료 코드 0 · 보고서 생성 · 5 단계 본문 · `[ERR]` 0 · HTTP 실패 0 | **합격** |
| H2 | 라우팅 | 요청 5/5 `{"order":["openai"],"quantizations":["unknown"],"allow_fallbacks":false}` · 응답 `provider` 5/5 `OpenAI` | **합격** |
| H3 | 비용 ≤ $5 | 응답 `usage.cost` 합 **$0.920** | **합격** — 예측 $1.6 보다 적다 |
| H4 | 앵커 계약 | **도달률 5/5 · 지어낸 서수 0 · 접어 넣음 0** (S12 는 서수 6 개 전부) | **합격** |

**청구를 토큰으로 재계산해 맞췄다** — 캐시 없는 입력 $2 · **캐시 쓰기 $2.5** · 캐시 읽기 $0.2 · 출력 $10 으로 5 호출 모두 소수 넷째 자리까지
일치한다. 입력 대부분이 캐시 쓰기(1.25 배)로 청구되지만 호출 2 부터 공유 접두사 7,327 토큰이 읽기로 돌아와 **캐시 표기가 없었을 때($0.923)와
사실상 같다.** 캐시는 이 판에서 이득도 손해도 아니다.

**같은 재료에서 GPT 가 쓰는 토큰이 적다.** claude-cli 첫 생성(Batch10, CLI 스캐폴딩 12,188 토큰 제외) 대비 입력 **0.80 배**,
출력 **0.56 배**(추론 포함). 표본 5 호출이다.

## 2. 2 단계 — GPT 1 회 판 `POQSettleBatch11` (사람 승인 대기)

```
PLANONLY_MAX_L2_ATTEMPTS=0 PLANONLY_CONSOLIDATOR_PROVIDER=OpenRouter \
PLANONLY_CONSOLIDATOR_MODEL=openai/gpt-5.6-sol PLANONLY_OPENROUTER_ONLY_BACKEND=openai \
scripts/run-plan-only-job.sh POQSettleBatch11
```

Batch10 과 다른 것은 Consolidator 모델 하나다(시도 1 회 · `Narrow` · SP 14 편 · Critic `z-ai/glm-5.3` 동일).

**비용 재추정** — Batch10 호출 45 개의 토큰에 1 단계 비율을 곱했다: 비율 그대로 **$10.4** · 출력만 claude 와 같다고 두면 $13.9 ·
토큰 1:1(보수) $18.9. 재생성 횟수가 모델 따라 달라지면 이 범위를 벗어난다.

| # | 항목 | 합격 |
| :-- | :--- | :--- |
| G1 | 실행 | 종료 코드 0 · 번들 내보내기 완료 |
| G2 | 라우팅 | Consolidator 요청 전부 백엔드 고정 · 응답 `provider` 전부 `OpenAI` |
| G3 | 비용 | Consolidator 응답 `usage.cost` 합 **≤ $25** |
| G4 | 시간 | **≤ 90 분** (Batch10 47 분) |
| G5 | 점수 | 채택 점수 **≥ 74** (Batch10 80 — 신호일 뿐, 표본 1) |
| G6 | 앵커 | SELECT 앵커 도달률 **5/5** · 불일치 0 · (종류,서수) 불일치 0 |
| G7 | 게이트 | 새 Job 을 넣은 코퍼스로 전체 시험 실패 0 · 건너뜀 0 |

기록(합격선 아님): 하한 재생성 사유별 횟수(특히 `SQL_CURRENT_RUN_ID`) · 배송 배너 수(Batch10 은 2) · 스윕 Job 별 발화 ·
국면별 시간 · 27.2 만 초과 호출 · 캐시 읽기 비율.
