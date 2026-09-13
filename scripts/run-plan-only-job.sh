#!/usr/bin/env zsh
# 저장된 명세서만으로 새 Job 한 판을 세운다(--plan-only).
#
# regen-job.sh 와 형제지만 겨냥하는 것이 다르다. regen-job.sh 는 **기존 Job 을 같은
# 이름으로 덮어쓴다**. 이 스크립트는 **새 이름으로 한 판을 더 만든다** - 기존 산출물을
# 건드리지 않으므로 스냅샷 가드가 필요 없고, 명세서도 재생성되지 않는다
# (Program.cs:758 - 이 경로는 DB 에 붙지 않고 output/Procedures 를 읽기만 한다).
#
# 쓰임새: 프롬프트 계약을 넣은 뒤 「그 계약이 실제 생성물에 듣는가」를 재는 표본 한 판.
#
#   scripts/run-plan-only-job.sh POQSettleBatch8
#
# 선택 환경변수(비우면 종전과 같다):
#   PLANONLY_MAX_L2_ATTEMPTS=0             계획 시도 1 회
#   PLANONLY_CONSOLIDATOR_PROVIDER=OpenRouter
#   PLANONLY_CONSOLIDATOR_MODEL=openai/gpt-5.6-sol
#   PLANONLY_OPENROUTER_ONLY_BACKEND=openai  그 모델을 이 백엔드 하나에 못박고 폴백을 끈다
#   PLANONLY_PROBE_ANCHORS=1               새 판 대신 기존 Job 을 재료로 앵커 프로브(첫 생성만)
set -euo pipefail

JOB=${1:-}
if [[ -z "$JOB" ]]; then
  echo "사용법: $0 <Job 이름>   (판: 새 이름 · 프로브: 기존 Job)" >&2
  exit 1
fi
PROBE=${PLANONLY_PROBE_ANCHORS:-}

# 공유 체크아웃의 루트. --show-toplevel 을 쓰면 이 스크립트를 실행 워크트리 안에서
# 부른 판이 REPO 를 워크트리로 잡아, 산출물이 실물 output/ 이 아니라 워크트리 안으로
# 샌다(regen-job.sh 가 2026-09-04 에 밟은 자리와 같은 모양). --git-common-dir 은
# 워크트리에서도 공유 .git 을 가리키므로 그 부모가 언제나 공유 체크아웃이다.
REPO=$(cd "$(dirname "$(git rev-parse --git-common-dir)")" && pwd)
if [[ -n "$PROBE" ]]; then
  LOGDIR=$REPO/output/logs-probe-$JOB-$(date +%Y%m%d-%H%M%S)
else
  LOGDIR=$REPO/output/logs-planonly-$JOB
fi
RUNROOT=${PLANONLY_RUNROOT:-$REPO/.worktrees/planonly-run}

# 배치 스텝의 실행 순서. Batch7 의 raw/prompt-context.md 에서 명세서 구획이 이어 붙은
# 순서를 그대로 읽어 왔다(추정이 아니다). --sp 나열 순서가 곧 스텝 순서다
# (PlanOnlyMaterialLoader 의 계약).
SPS=(
  dbo.UP_Util_PG_Client_CMRate_Ins
  dbo.UP_UTIL_SETTLE_INS
  dbo.UP_UTIL_SETTLE_CANCEL_INS
  dbo.UP_UTIL_SETTLE_EXCEPTION_PROC
  dbo.UP_UTIL_SETTLE_COMM_UPD
  dbo.UP_UTIL_SETTLE_EXPECT_PROC
  dbo.UP_UTIL_SETTLE_INS_EXTRA
  dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD
  dbo.UP_UTIL_STAT_PGCOLLECT_INS
  dbo.UP_Util_Settle_Summary
  dbo.UP_Util_Settle_Summary_AcqManual
  dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA
  dbo.UP_UTIL_SETTLE_SUMMARY_ETC
  dbo.UP_UTIL_SETTLE_PROC_ETC
)

# ── 가드 1: 이름이 정말 새것인가
#
# 같은 이름이면 재사용 저널(output/Jobs/<Job>/raw/attempts)이 붙어 이전 판의 답을
# 그대로 돌려줄 수 있다. 그러면 「계약이 듣는가」를 재는 판이 이전 판의 복사본이 되고,
# 초록이 나도 아무것도 증명하지 못한다.
if [[ -n "$PROBE" ]]; then
  # 프로브는 얼어붙은 Job 의 목차·골격을 재료로 쓰므로 반대로 **있어야** 한다.
  if [[ ! -f "$REPO/output/Jobs/$JOB/raw/PlanStructure.md" ]]; then
    echo "중단: 프로브 재료가 없다 - output/Jobs/$JOB/raw/PlanStructure.md" >&2
    exit 1
  fi
elif [[ -d "$REPO/output/Jobs/$JOB" ]]; then
  echo "중단: output/Jobs/$JOB 가 이미 있다. 새 이름을 줘라." >&2
  echo "  기존 Job 을 다시 돌리려는 것이라면 이 스크립트가 아니라 scripts/regen-job.sh 다." >&2
  exit 1
fi

# ── 가드 2: 명세서 재료가 전부 있는가
#
# CLI 도 NotFound 를 보고 멈추지만, 거기까지 가려면 빌드를 기다려야 한다. 여기서
# 먼저 서면 몇 분을 아끼고, 무엇보다 「무엇이 없는가」가 한눈에 나온다.
MISSING=()
for sp in $SPS; do
  [[ -f "$REPO/output/Procedures/$sp/docs/Spec.md" ]] || MISSING+=($sp)
done
if (( ${#MISSING} > 0 )); then
  echo "중단: 명세서가 없는 진입점 ${#MISSING} 개." >&2
  for m in $MISSING; do echo "  - $m" >&2; done
  exit 1
fi

# ── 가드 3: 실행 워크트리가 있고 깨끗한가
#
# 공유 체크아웃에서 돌리지 않는다 - 다른 세션의 빌드와 bin/obj 가 겹치면 어느 커밋의
# 바이너리가 이 산출물을 만들었는지 말할 수 없게 된다.
if [[ ! -d "$RUNROOT" ]]; then
  echo "중단: 실행 워크트리가 없다 ($RUNROOT)." >&2
  echo "  git -C $REPO worktree add --detach .worktrees/planonly-run HEAD" >&2
  exit 1
fi
if [[ -n "$(git -C $RUNROOT status --porcelain)" ]]; then
  echo "중단: 실행 워크트리가 깨끗하지 않다. 커밋 해시가 도는 코드를 서술하지 못한다." >&2
  git -C $RUNROOT status --short >&2
  exit 1
fi

# ── 가드 4: 로컬 설정이 워크트리에 닿는가
#
# appsettings.local.json 은 gitignore 라 새 워크트리에 따라가지 않는다. 없으면 CLI 는
# 저장소 기본값(API provider)으로 조용히 돌아 **기준선과 다른 모델**로 한 판을 태운다.
# 1차 통제군(POQSettleBatch2)이 그렇게 날아갔다.
LOCAL=$RUNROOT/src/ReSet.Cli/appsettings.local.json
if [[ ! -e "$LOCAL" ]]; then
  echo "중단: $LOCAL 이 없다. 공유 체크아웃의 것을 걸어라:" >&2
  echo "  ln -s $REPO/src/ReSet.Cli/appsettings.local.json $LOCAL" >&2
  exit 1
fi

# ── 선택: 계획 시도(채점) 예산 못박기
#
# PLANONLY_MAX_L2_ATTEMPTS=0 이면 계획 시도가 1 회로 끝난다(총 시도 = 1 + 이 값).
# L1 수리 예산(MaxL1RepairAttempts)은 따로라 이 값과 무관하게 남는다.
# 비우면 설정 파일 값(기본 5 → 6 회)을 쓴다. 2026-09-13 판독: 6 회 판의 약 80% 가
# 2 차 이후 시도였고 채택 점수는 76 → 78 이었다.
MAX_L2=${PLANONLY_MAX_L2_ATTEMPTS:-}
if [[ -n "$MAX_L2" && ! "$MAX_L2" =~ '^[0-9]+$' ]]; then
  echo "중단: PLANONLY_MAX_L2_ATTEMPTS 는 0 이상의 정수여야 한다 ('$MAX_L2')." >&2
  exit 1
fi

# ── 선택: Consolidator 모델과 OpenRouter 백엔드 못박기
#
# --plan-only 에서 목차·골격·단계 본문을 만드는 것은 Actor 가 아니라 Consolidator 다
# (VerificationPipelineOrchestrator 의 _consolidatorService). Actor 는 이 경로에서 불리지 않는다.
CONS_PROVIDER=${PLANONLY_CONSOLIDATOR_PROVIDER:-claude-cli}
CONS_MODEL=${PLANONLY_CONSOLIDATOR_MODEL:-claude-sonnet-5}
ONLY_BACKEND=${PLANONLY_OPENROUTER_ONLY_BACKEND:-}
if [[ -n "$PROBE" ]]; then
  MODE_ARGS=(--probe-anchors "$JOB"); MODE_LABEL="앵커 프로브(첫 생성만)"
else
  MODE_ARGS=(--plan-only --job-name "$JOB"); MODE_LABEL="계획 판"
fi
ROUTE_ENV=()
if [[ -n "$ONLY_BACKEND" ]]; then
  if [[ "$CONS_PROVIDER" != "OpenRouter" ]]; then
    echo "중단: PLANONLY_OPENROUTER_ONLY_BACKEND 는 Consolidator 가 OpenRouter 일 때만 뜻이 있다." >&2
    exit 1
  fi
  # 저장소 기본값은 가용성을 위해 폴백을 연다(CliProviderSettingsTests). 측정 판은 백엔드가
  # 섞이면 안 되므로 이 판만 덮는다. 배열 칸은 환경변수로 못 지워 뒤 칸을 공백으로 덮는다 -
  # AppSettings_PerRunEnvironmentOverride_PinsGptSolToOneBackendWithoutFallback 이 잠근 모양이다.
  RB="AiSettings__Providers__OpenRouter__Routing__ByModel__${CONS_MODEL}"
  ROUTE_ENV=("${RB}__Order__0=$ONLY_BACKEND" "${RB}__Order__1= " "${RB}__Order__2= " "${RB}__Order__3= " "${RB}__AllowFallbacks=false")
fi

mkdir -p $LOGDIR
git -C $RUNROOT rev-parse HEAD > $LOGDIR/COMMIT
git -C $RUNROOT log -1 --oneline >> $LOGDIR/COMMIT
print -l $SPS > $LOGDIR/SPS
# 이 회차가 환경변수로 못박은 값. 설정 파일은 공유라 나중에 바뀌어도 이 파일이 참으로 남는다.
print -l \
  "AiSettings__Provider=claude-cli" \
  "AiSettings__ModelName=claude-sonnet-5" \
  "AiSettings__Consolidator__Provider=$CONS_PROVIDER" \
  "AiSettings__Consolidator__ModelName=$CONS_MODEL" \
  "AiSettings__PromptContextScope=Narrow" \
  "AiSettings__MaxL2Attempts=${MAX_L2:-(설정 파일)}" \
  "OpenRouter 백엔드 고정=${ONLY_BACKEND:-(설정 파일)}" \
  "모드=$MODE_LABEL" \
  $ROUTE_ENV > $LOGDIR/RUN-ENV

echo "───────────────────────────────────────────────"
echo " Job:        $JOB  [$MODE_LABEL]"
echo " Consolidator: $CONS_PROVIDER / $CONS_MODEL ${ONLY_BACKEND:+(백엔드 $ONLY_BACKEND 고정 · 폴백 끔)}"
echo " 스텝 재료:   ${#SPS} 편 (순서 = 위 나열)"
echo " 로그:        $LOGDIR"
echo " 커밋:        $(head -1 $LOGDIR/COMMIT)"
echo " 실행 루트:   $RUNROOT"
if [[ -n "$MAX_L2" ]]; then echo " 계획 시도:   $((MAX_L2 + 1)) 회 (못박음)"; else echo " 계획 시도:   설정 파일 값"; fi
echo " 산출 경로:   $REPO/output/Jobs/$JOB"
echo "───────────────────────────────────────────────"

cd $RUNROOT
# 경로 셋을 전부 절대경로로 덮는다 - appsettings.json 의 경로는 cwd 상대라, 안 덮으면
# CLI 가 실행 워크트리 안에 **새 코퍼스를 만들어** 실물은 그대로 둔 채 「돌렸다」고
# 믿게 된다(regen-job.sh 가 2026-09-04 에 밟은 자리).
#
# 모델을 환경변수로 못박는다. 값은 Batch7 의 raw/attempts/run-001/manifest.json 이
# 기록한 ReuseKey 와 같다. 공유 설정 파일이 나중에 바뀌어도 이 회차의 서술이 참으로
# 남는다. Critic 은 그 manifest 에 안 담겨 있어 못박지 않는다 - 지어내는 대신 로컬
# 설정을 그대로 쓰고 실행 로그에서 사후 확인한다.
OutputSettings__Directory=$REPO/output \
LoggingSettings__LogDirectory=$LOGDIR \
AiSettings__Provider=claude-cli \
AiSettings__ModelName=claude-sonnet-5 \
AiSettings__Consolidator__Provider=$CONS_PROVIDER \
AiSettings__Consolidator__ModelName=$CONS_MODEL \
AiSettings__PromptContextScope=Narrow \
env ${MAX_L2:+AiSettings__MaxL2Attempts=$MAX_L2} $ROUTE_ENV \
dotnet run --project src/ReSet.Cli -- \
  $MODE_ARGS \
  --sp ${(j:,:)SPS}
