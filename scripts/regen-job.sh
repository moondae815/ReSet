#!/bin/zsh
# 축 B 로드맵 5 — 단계 번들 재생성 하네스.
#
# 사용법:  ./scripts/regen-job.sh <JobName>          예: POQSettleBatch1
#
# ─────────────────────────────────────────────────────────────────────────────
# [무엇을 하는 일인가]
#
#   축 B 는 `Spec.md` ↔ 단계 지시서 대조다. 그런데 지금 그 둘은 **다른 세대**다 —
#   단계 번들이 2026-08-12~08-24 이고 명세서는 2026-08-28(캐시 17 전건 재생성)이다.
#   그래서 감사도 스윕도 「이행 결함」과 「세대 차이」를 가르지 못한다
#   (2026-09-04 스윕 보고서가 자기 실행 조건에 그렇게 적는다).
#
#   이 하네스는 그 오염을 없앤다. **축 B 결함을 닫는 것이 목적이 아니라,
#   축 B 판정이 성립하는 상태를 만드는 것이 목적이다.** 실제 셈은 재감사(로드맵 6)가 한다.
#
# ─────────────────────────────────────────────────────────────────────────────
# [기대치를 미리 낮춰 둔다 — 실측이 정한 상한]
#
#   2026-08-24 감사는 POQSettleBatch1 에서 축 B 결함 **46 건**(🔴 2 · 🟠 7 · 🟡 16 · ⚪ 21)을
#   찾았다. 같은 산출물에 현행 기계 검사를 걸면 발화는 **12 건**이다(A 2 · B 1 · E 3 · 미분류 6).
#
#   **재생성이 닫을 수 있는 것은 기계가 보는 자리다.** 루프는 하한 검사가 잡는 것을 다시
#   만들고, 나머지는 Critic 이 확률적으로 잡거나 놓친다. 실측 예: 감사의 S11 🟠(조인 키
#   누락)는 검사 B 가 `UPDATE 9 · YMD, UseState` 로 정확히 같은 자리를 짚는다. 반면 🔴 S07
#   (18 개 갱신 중 10 개의 SET 산식이 주석으로 대체됨 — 실물은 UPDATE 8 + 주석 자리표시자 17)은
#   검사 B·C 목록에 **없다**. 검사 A(문장 개수)가 그것을 잡는지는 이 실행의 로그가 답한다.
#
# ─────────────────────────────────────────────────────────────────────────────
# [★ 이 회차는 모델이 바뀐다 — 사람이 그렇게 정했다 (2026-09-04)]
#
#   감사 대상은 `codex-cli (gpt-5.6-terra)` 가 만들었다(산출물 헤더에 그렇게 적혀 있다).
#   이 회차는 **현행 기본값**으로 돈다 — Actor·Consolidator 가 `claude-cli/claude-sonnet-5` 다.
#
#   **그러므로 축 B 차이를 규칙·도구 변화로만 귀속할 수 없다.** 이 저장소는 그 혼입으로
#   이미 한 번 오귀속했다(검사 D 18 → 0 을 캐시 17 승격 탓으로 읽었는데 실제로는 모델 교체였다).
#
#   **판독은 두 층으로 갈라서 하라** — 그때 쓴 방법 그대로다:
#     · 기계 확정 층 (L1 이 등호로 강제하는 자리) → 규칙·도구 변화가 여기 나타난다.
#       모델이 바뀌어도 이 층은 모델 재량이 아니다.
#     · 재량 층 (모델이 자유롭게 쓰는 산문·서술) → 모델 교체 효과가 여기 나타난다.
#   그리고 **검사별 총 발화량을 전후로 나란히 놓는 표**를 반드시 만들어라. 좌표 차분과
#   침묵 분모는 검사 B·C 안에서만 도는 자라, 검사 D 가 꺼진 것을 둘 다 못 봤다.
#
# ─────────────────────────────────────────────────────────────────────────────
# [★ 새 혼입 하나 — 2026-09-04 정화기 수정]
#
#   `CleanseMermaidCode` 가 flowchart 전용 화살표 보정을 모든 mermaid 블록에 걸어
#   sequenceDiagram 의 유효한 `-->>` 를 `--->` 로 부수던 것을 이 날 고쳤다(`ad90c004`).
#   **그래서 재생성 뒤 mermaid 관련 L1 발화가 줄어드는 것은 재생성 효과가 아니라 도구 수정
#   효과다.** 전후 비교에서 그 감소를 재생성 공로로 읽지 마라.
#
# ─────────────────────────────────────────────────────────────────────────────
# [스냅샷이 없으면 돌지 않는다]
#
#   `InstructionBundleWriter` 는 이번 회차 산출이 모자라면 `steps/`·`verification/` 을
#   **디렉터리째 지운다**(의도된 동작). POQSettleBatch1 의 번들에는 사본이 없다 —
#   `output.bak-2026-08-22/Jobs` 에는 Proc 21 편만 있다. 스냅샷 없이 돌리면 축 B 46 건의
#   기준선이 영영 사라지고 재감사가 비교할 「전」을 잃는다.
#
# ─────────────────────────────────────────────────────────────────────────────

set -e
REPO=/Users/payletter/git-root/ReSet
JOB=$1

if [[ -z "$JOB" ]]; then
  echo "사용법: $0 <JobName>   (예: POQSettleBatch1)" >&2
  exit 1
fi

# 스냅샷은 **이름이 아니라 내용으로** 찾는다. 이름 규약(output.bak-<작업>-<날짜>)은 날짜가
# 들어가 재사용이 안 되고, 이름을 유도하면 대소문자 하나로 조용히 빗나간다(실제로 한 번 났다).
# REGEN_SNAPSHOT 으로 직접 지정할 수도 있다.
if [[ -n "$REGEN_SNAPSHOT" ]]; then
  SNAP=$REGEN_SNAPSHOT
else
  SNAP=""
  for d in $REPO/output.bak-*preregen*(N/); do
    [[ -d "$d/Jobs/$JOB" ]] && SNAP=${d%/} && break
  done
fi
LOGDIR=$REPO/output/logs-regen-$JOB
# 실행 워크트리. 공유 체크아웃에서 돌리지 않는다 - 다른 세션의 빌드와 bin/obj 가 겹치면
# 어느 커밋의 바이너리가 이 산출물을 만들었는지 말할 수 없게 된다.
RUNROOT=${REGEN_RUNROOT:-$REPO/.worktrees/regen-run}

# ── 가드 1: 스냅샷이 있는가
if [[ -z "$SNAP" || ! -d "$SNAP/Jobs/$JOB" ]]; then
  echo "중단: $JOB 의 재생성 전 스냅샷을 찾지 못했다." >&2
  echo "  output.bak-*preregen*/Jobs/$JOB 를 찾았으나 없다. 뜨고 다시 돌려라:" >&2
  D=$REPO/output.bak-$JOB-preregen-$(date +%Y%m%d)
  echo "    mkdir -p $D/Jobs && cp -a $REPO/output/Jobs/$JOB $D/Jobs/" >&2
  echo "  산출물만이 아니라 **소비 명세서의 지문**도 함께 남겨라(MANIFEST.md) - 없으면" >&2
  echo "  재생성 뒤 「입력이 그때와 같았나」에 답할 수 없고 가드 3 이 돌지 않는다." >&2
  exit 1
fi

# ── 가드 2: 실행 워크트리가 있고 깨끗한가
if [[ ! -d "$RUNROOT" ]]; then
  echo "중단: 실행 워크트리가 없다 ($RUNROOT)." >&2
  echo "  git -C $REPO worktree add --detach .worktrees/regen-run main" >&2
  exit 1
fi
if [[ -n "$(git -C $RUNROOT status --porcelain)" ]]; then
  echo "중단: 실행 워크트리가 깨끗하지 않다. 커밋 해시가 도는 코드를 서술하지 못한다." >&2
  git -C $RUNROOT status --short >&2
  exit 1
fi

# ── 가드 3: 입력(명세서)이 스냅샷 시점과 같은가
#
# 축 B 는 명세서 ↔ 지시서 대조다. 명세서가 그 사이에 바뀌었다면 재생성 뒤의 차이를
# 지시서 변화로만 귀속할 수 없다 - 이 회차가 이미 모델 교체라는 혼입을 하나 안고
# 가므로 여기서 하나를 더 들이면 판독이 성립하지 않는다.
if [[ -f "$SNAP/MANIFEST.md" ]]; then
  DRIFT=0
  CHECKED=0
  while IFS= read -r line; do
    name=$(echo "$line" | sed -n 's/^| `\([^`]*\)` | \([0-9a-f]\{32\}\) |$/\1/p')
    want=$(echo "$line" | sed -n 's/^| `\([^`]*\)` | \([0-9a-f]\{32\}\) |$/\2/p')
    [[ -z "$name" || "$name" == *.md ]] && continue
    p=$REPO/output/Procedures/$name/docs/Spec.md
    if [[ ! -f "$p" ]]; then echo "  ★ 입력 소실: $name" >&2; DRIFT=1; continue; fi
    got=$(md5 -q "$p")
    CHECKED=$((CHECKED+1))
    if [[ "$got" != "$want" ]]; then echo "  ★ 입력 변경: $name" >&2; DRIFT=1; fi
  done < "$SNAP/MANIFEST.md"

  # 「대조 항목 0 개」와 「대조해서 깨끗함」을 결과로 구별한다. MANIFEST 형식이 바뀌면
  # 위 sed 가 아무것도 못 읽고 DRIFT 가 0 인 채로 통과하는데, 그것은 입력이 같다는 뜻이
  # 아니라 **가드가 안 돌았다**는 뜻이다. 이 저장소가 반복해서 당한 형태라 여기서 끊는다
  # (MechanicalValidator.ValidateBatchStep 의 같은 규칙 참고).
  if (( CHECKED == 0 )); then
    echo "중단: MANIFEST 에서 명세서 지문을 하나도 읽지 못했다 - 가드 3 이 돌지 않았다." >&2
    echo "  MANIFEST 형식이 바뀌었는지 보라: $SNAP/MANIFEST.md" >&2
    exit 1
  fi
  echo "입력 지문 대조: 명세서 $CHECKED 개 (드리프트 $DRIFT)"
  if [[ $DRIFT -ne 0 ]]; then
    if [[ "$REGEN_ALLOW_INPUT_DRIFT" == "1" ]]; then
      echo "경고: 입력이 스냅샷 시점과 다르다. REGEN_ALLOW_INPUT_DRIFT=1 이므로 계속한다." >&2
    else
      echo "중단: 소비 명세서가 스냅샷 시점과 다르다 - 축 B 차이를 지시서 변화로 귀속할 수 없다." >&2
      echo "  의도한 것이면 REGEN_ALLOW_INPUT_DRIFT=1 로 다시 돌리고, 그 사실을 판독에 적어라." >&2
      exit 1
    fi
  fi
fi

# ── 가드 4: 로그가 이어 붙지 않는가
#
# [zsh 에서 compgen 을 쓰지 마라 - 이 가드가 죽어 있었다]
# `compgen` 은 bash 내장이라 zsh 에는 없다. `if compgen ...` 은 "command not found" 로
# 거짓이 되어 **가드가 조용히 통과한다**(에러 한 줄은 stderr 로 흘러 눈에 안 띈다).
# 통제군 하네스들(run-repeat-batch5*.sh)이 같은 관용구를 쓰고 있어 그쪽 가드도 죽어 있다 -
# 판마다 새 디렉터리를 써서 물리지 않았을 뿐이다. zsh 글롭 한정자 (N) 로 대신한다.
existing_logs=($LOGDIR/*.log(N))
if (( ${#existing_logs} > 0 )); then
  echo "중단: $LOGDIR 에 로그가 이미 있다. 이어 붙으면 회차 경계를 가를 수 없다." >&2
  echo "  실패한 회차의 잔재라면 지우고 다시 돌려라:  rm -rf $LOGDIR" >&2
  exit 1
fi

# ── 가드 5: 덮어쓴 절대경로가 실제로 그 자리를 가리키는가
#
# 가드 3 이 입력 "내용"을 보는 것과 달리 이쪽은 입력 "자리"를 본다. 경로 하나를 덮는 것을
# 빠뜨리면 CLI 가 엉뚱한 루트에 새 코퍼스를 만들고, 그것은 조용한 성공으로 보인다.
if [[ ! -f "$REPO/output/offline_snapshot.json" ]]; then
  echo "중단: 오프라인 스냅샷이 없다 ($REPO/output/offline_snapshot.json)." >&2
  exit 1
fi
if [[ ! -d "$REPO/output/Jobs/$JOB" ]]; then
  echo "중단: 재생성 대상이 실물 코퍼스에 없다 ($REPO/output/Jobs/$JOB)." >&2
  exit 1
fi

mkdir -p $LOGDIR
{
  git -C $RUNROOT rev-parse HEAD
  echo "detached@$RUNROOT"
  date -Iseconds
  echo "job=$JOB"
  echo "snapshot=$SNAP"
  echo "models=actor:claude-cli/claude-sonnet-5 critic:codex-cli/gpt-5.6-terra consolidator:claude-cli/claude-sonnet-5"
  echo "audited-artifact-was=codex-cli/gpt-5.6-terra  # 모델이 바뀐다 - 위 머리말의 두 층 판독을 볼 것"
} > $LOGDIR/COMMIT

echo "───────────────────────────────────────────────"
echo " 재생성:      $JOB"
echo " 스냅샷:      $SNAP"
echo " 로그:        $LOGDIR"
echo " 커밋:        $(head -1 $LOGDIR/COMMIT)"
echo " 실행 루트:   $RUNROOT"
echo "───────────────────────────────────────────────"
echo " 메뉴에서 '2. 통합 배치 마이그레이션 설계' 를 고르고 Job 이름에 $JOB 을 그대로 입력한다."
echo " '1. 개별 SP 역공학 분석' 을 고르면 명세서가 재생성되어 입력이 그 자리에서 바뀐다."
echo "───────────────────────────────────────────────"

cd $RUNROOT
# [경로 셋을 전부 절대경로로 덮는 이유 - 첫 실행이 여기서 죽었다 (2026-09-04)]
# appsettings.json 의 경로 셋(OutputSettings:Directory · OfflineSnapshotPath ·
# LoggingSettings:LogDirectory)은 **cwd 상대**(`./output/...`)다. 이 스크립트는 위에서
# $RUNROOT 로 cd 하므로, 덮지 않으면 실행 워크트리 안의 없는 `output/` 을 본다.
# 로그만 덮었던 첫 판은 "오프라인 스냅샷 파일을 찾을 수 없습니다" 로 즉시 죽었다 -
# 그나마 그것이 다행이었다. 조용히 통과했다면 CLI 가 `.worktrees/regen-run/output/` 에
# **새 코퍼스를 만들어** 실물은 그대로 둔 채 「재생성했다」고 믿게 됐을 것이다.
#
# 모델을 환경변수로 못박는다. 경로만 격리하면 appsettings.local.json 에서 샌다 -
# 공유 설정을 물려받아 기준선과 다른 모델로 돈 사고가 실제로 있었다(1차 통제군 POQSettleBatch2).
# 값 자체는 현행 기본과 같지만, 명시해야 나중에 그 파일이 바뀌어도 이 회차의 서술이 참으로 남는다.
OutputSettings__Directory=$REPO/output \
DatabaseSettings__OfflineSnapshotPath=$REPO/output/offline_snapshot.json \
LoggingSettings__LogDirectory=$LOGDIR \
AiSettings__Provider=claude-cli \
AiSettings__ModelName=claude-sonnet-5 \
AiSettings__Critic__Provider=codex-cli \
AiSettings__Critic__ModelName=gpt-5.6-terra \
AiSettings__Consolidator__Provider=claude-cli \
AiSettings__Consolidator__ModelName=claude-sonnet-5 \
dotnet run --project src/ReSet.Cli
