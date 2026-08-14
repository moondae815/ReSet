#!/usr/bin/env sh
#
# AGENTS.md의 한 구간을 훑어, 각 줄이 서술하는 내용의 근거가 이미 어디에 있는지 찾는다.
#
# 삭제 대장을 손으로 쓰면 72개 항목에서 반드시 틀린다. "이 줄은 중복이니 지워도
# 된다"는 주장은 근거 위치를 댈 수 있을 때만 참이고, 그 확인은 기계가 해야 한다.
#
# 대조는 클래스 이름이 아니라 마크다운 링크의 경로로 한다. src/ 안에는 같은
# basename을 가진 파일이 둘 있다(Program.cs, ConsoleUserInteraction.cs가 각각
# ReSet.Cli와 ReSet.Validator.Cli에 있다). 이름만으로 근거를 찾으면 서로 다른
# 두 AGENTS.md 줄이 같은 근거 더미(합산된 바이트)를 나눠 가져 한쪽이 실제보다
# 부풀려진 "중복" 판정을 받을 수 있다 — 이것이 위험한 방향의 오류다(지우면
# 안 되는 서술을 지우게 만든다). 경로로 찾으면 각 줄은 자신의 파일만 본다.
#
#   ./scripts/doc-audit.sh 20 133     §2 카탈로그 구간을 감사한다
#
# 출력: 줄번호 | 바이트 | 대상식별자 | architecture.md 분량 | 코드주석 분량 | 판정
# 마지막 줄에 "감사 완료: N행 처리"를 낸다. set -eu 아래에서 파이프에 붙은 while은
# 도중에 실패해도 표를 조용히 잘라낼 수 있어, 이 표시가 없으면 잘린 결과를 완전한
# 결과로 착각하기 쉽다.
#
# 판정을 읽는 법 — 이 도구가 증명하는 것과 증명하지 못하는 것:
#   - 근소한 차이(대략 10% 이내로 기준을 못 넘김)는 "근거없음"이 아니라 "확인 필요"에
#     가깝다. 바이트 수 비교는 문턱 근처에서 부정확해서, 사실상 동일한 서술이 양쪽에
#     있는데도 근거없음으로 나올 수 있다.
#   - architecture.md가 여러 클래스를 하나의 링크 레이블로 묶어 서술하면(예: "Clients
#     (Claude, OpenAi, Ollama) Tests"), 개별 파일 경로 대조는 그 서술을 찾지 못해
#     근거없음을 낸다. 근거가 실제로 없는 게 아니라 이 스크립트가 못 찾는 것이다.
#   - 이 스크립트는 "중복"을 증명한다. "근거없음"을 증명하지 않는다 — 근거없음이
#     뜻하는 것은 "이 스크립트가 찾지 못했다"이지 "근거가 존재하지 않는다"가 아니며,
#     최종 판단은 사람이 한다.
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

  # 그 줄이 다루는 대상: 첫 번째 [Xxx.cs](경로) 링크의 경로 부분.
  link=$(printf '%s' "$body" | grep -oE '\[[A-Za-z][A-Za-z0-9]*\.cs\]\([^)]*\)' | head -1)

  if [ -n "$link" ]; then
    path=$(printf '%s' "$link" | sed -E 's/^\[[^]]*\]\(([^)]*)\)$/\1/' | sed 's#^\./##')
    sym=$(printf '%s' "$path" | sed 's#.*/##; s/\.cs$//')
    fallback=""
  else
    # 링크에 경로가 없을 때만(형식 이탈) 이름 기반의 예전 판정으로 물러난다.
    # 이 경로는 다른 basename과 섞일 수 있으므로 판정에 "(이름판정)" 표시를 남긴다.
    sym=$(printf '%s' "$body" | grep -oE '\[[A-Za-z][A-Za-z0-9]+\.cs\]' | head -1 |
          tr -d '[].' | sed 's/cs$//')
    path=""
    fallback="1"
  fi

  [ -z "$sym" ] && { printf '%-6s %-7s %-34s %-9s %-9s %s\n' "$ln" "$bytes" "-" "-" "-" "산문(수동판정)"; continue; }

  if [ -n "$path" ] && [ -e "$path" ]; then
    arch=$(LC_ALL=C grep -F -h "$path" docs/architecture.md 2>/dev/null | LC_ALL=C wc -c | tr -d ' ')
    doc=$(awk '/\/\/\//{c+=length($0)} END{print c+0}' "$path")
  else
    # path가 비었거나(fallback) 존재하지 않는 파일을 가리키면(오타·이동 등) 이름
    # 기반으로 물러난다. 이 갈래도 "(이름판정)" 표시를 남긴다.
    fallback="1"
    arch=$(LC_ALL=C grep -h "$sym" docs/architecture.md 2>/dev/null | LC_ALL=C wc -c | tr -d ' ')
    f=$(find src tests -name "$sym.cs" | head -1)
    if [ -n "$f" ]; then
      doc=$(awk '/\/\/\//{c+=length($0)} END{print c+0}' "$f")
    else
      doc=0
    fi
  fi

  suffix=""
  [ -n "$fallback" ] && suffix="(이름판정)"

  if [ "$arch" -ge "$bytes" ]; then
    verdict="중복:architecture.md$suffix"
  elif [ "$doc" -ge "$bytes" ]; then
    verdict="중복:$sym.cs <summary>$suffix"
  else
    verdict="근거없음(이동필요)$suffix"
  fi

  printf '%-6s %-7s %-34s %-9s %-9s %s\n' "$ln" "$bytes" "$sym" "$arch" "$doc" "$verdict"
done

rows=$(awk -v s="$START" -v e="$END" 'NR>=s && NR<=e && $0 != "" { c++ } END { print c+0 }' AGENTS.md)
echo "감사 완료: ${rows}행 처리"
