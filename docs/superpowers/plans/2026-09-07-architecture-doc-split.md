# `docs/architecture.md` 분할과 doc-sync 계약 갱신 (A′) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 299,967바이트짜리 `docs/architecture.md`를 허브 하나 + `docs/architecture/` 17개로 가르되, 그 파일 이름을 못박고 있는 **여섯 소비자**를 같은 브랜치에서 함께 고쳐 검사가 조용해지지 않게 한다.

**Architecture:** 절 번호를 파일명 접두사로 못박아(`4.4-verification-pipeline.md`) 기존 `§4.4` 주소 체계를 그대로 살린다. `AGENTS.md`는 예산 여유가 69바이트뿐이라 한 글자도 안 건드리고, 허브 목차가 § → 파일을 해소한다. 이사는 **바이트 동일성**으로 검증하고(재조립본이 원본과 byte-for-byte 같아야 한다), 새 검사는 **일부러 깨뜨려** 살아 있음을 증명한다.

**Tech Stack:** POSIX sh · awk · sed · grep · xUnit(`dotnet test`) · mermaid-cli(`mmdc`, 선택)

**Spec:** `docs/superpowers/specs/2026-09-07-architecture-doc-split-design.md`

## Global Constraints

- **작업 트리:** 격리 워크트리 `.claude/worktrees/docs-architecture-split` (브랜치 `worktree-docs-architecture-split`, 베이스 `ffbf61db`). 코퍼스 재료 **넷**(`output`, `output.bak-2026-08-22`, `output.bak-stage4-control-20260828`, `output.bak-batch1-preregen-20260904`)이 심링크되어 있어야 한다.
- **기준선:** `dotnet test` → 실패 0 · 건너뜀 0 · 통과 3,782.
- **합격 기준은 개수가 아니라 「실패 0 · 건너뜀 0 · 경고 0」이다.** 절대 통과 수는 게이트로 쓰지 않는다.
- **`AGENTS.md` 바이트 변화 0.** 34,931 / 상한 35,000 → 여유 69바이트. 이 파일은 어떤 태스크에서도 수정 대상이 아니다.
- **이번 작업은 「이사」다.** 문장을 고치거나 최신화하지 않는다. 낡은 서술도 그대로 옮긴다.
- **원본 오라클은 git이다.** 비교 기준은 언제나 `ffbf61db`의 원본이지, 작업 중인 파일이 아니다.
- **스크래치패드 경로(`SP`):** `/private/tmp/claude-501/-Users-payletter-git-root-ReSet/095e524d-22af-4ee5-a292-0754fad5ea57/scratchpad`
- **원본 절 경계(줄번호, `ffbf61db` 기준):** §2.2=42, §3=204, §4=464, §4.1=466, §4.2=481, §4.3=487, §4.4=500, §4.5=775, §4.6=789, §4.7=834, §4.8=838, §4.9=845, §4.10=856, §4.11=866, §4.12=877, §4.13=925, §4.14=956, §5=1022, EOF=1075.
- **`git stash`를 쓰지 않는다.** stash 스택은 다른 워크트리·세션과 공유된다.

---

## 파일 구조

**새로 만드는 것**

- `scripts/doc-link-check.sh` — 깊이 무관 상대 링크 검증기. 문서가 몇 개로 갈리든 따라간다. **영구 자산.**
- `docs/architecture/` — 분할 파일 17개.
- 추출 스크립트는 **일회용**이라 저장소에 넣지 않는다(스크래치패드에 둔다).

**고치는 것**

- `docs/architecture.md` — 허브(제목·서문·§1·§2.1 + 목차 + `synced-through`), 8,000바이트 이하.
- `tests/ReSet.Core.Tests/documentation-budget-baseline.txt` — 허브 등록.
- `tests/ReSet.Core.Tests/DocumentationBudgetTests.cs:66` — `Routing` 상수의 거처 문자열.
- `.claude/skills/reset-doc-sync/SKILL.md` — 편집 지점 열둘.
- `scripts/doc-audit.sh:72,78` — `architecture.md` 고정 grep 둘.

**절대 안 건드리는 것**

- `AGENTS.md`
- `docs/superpowers/plans/*`·`specs/*`·`audit-reports/*`의 과거 줄번호 인용 — 역사 기록이다.

---

## Task 1: 링크 검증기 도입과 분할 전 기준선 채집

분할하기 **전에** 검사를 세우고, 그 검사가 실제로 고발하는지 먼저 증명한다. 순서가 뒤바뀌면
"분할 후 BROKEN 0"이 「링크가 성하다」인지 「검사가 아무것도 안 본다」인지 영영 못 가른다.

**Files:**

- Create: `scripts/doc-link-check.sh`
- Create: `$SP/before/` (기준선 채집물, 커밋하지 않음)

**Interfaces:**

- Produces: `scripts/doc-link-check.sh` — 인자 없음. 마지막 줄에 `링크 검사 완료: N개 확인, M개 깨짐`을 낸다. 종료 코드는 0=전부 성함, 1=깨진 링크 있음, 2=검사 대상 링크가 0개(검사가 죽은 것).
- Produces: `$SP/before/link-count.txt`, `$SP/before/symbols.txt`, `$SP/before/reverse-search.txt`, `$SP/before/mermaid-count.txt`

- [ ] **Step 1: 검증기를 쓴다**

`scripts/doc-link-check.sh`:

```sh
#!/usr/bin/env sh
#
# 문서의 상대 링크가 실제 파일을 가리키는지 확인한다.
#
# 이 검사의 함정은 "발화 0"이 두 가지를 뜻한다는 것이다 — 링크가 다 성하거나,
# 검사가 아무것도 안 보고 있거나. 그래서 마지막 줄에 **확인한 링크 수**를 함께 낸다.
# 종전 검사는 `docs/architecture.md`와 `](../src/`를 동시에 못박아, 문서가 갈리면
# 0개를 검사하고 조용히 통과했다.
#
# 경로 해석은 **각 파일의 dirname 기준**이다. 깊이를 가정하지 않으므로 문서가
# docs/ 아래 몇 겹으로 들어가도 따라간다.
set -eu

ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"

FILES="README.md AGENTS.md docs/architecture.md"
for f in docs/architecture/*.md; do
  [ -e "$f" ] && FILES="$FILES $f"
done

pairs=$(mktemp)
trap 'rm -f "$pairs"' EXIT

for f in $FILES; do
  [ -e "$f" ] || continue
  grep -oE '\]\([^)]+\)' "$f" 2>/dev/null |
    sed -E 's/^\]\(//; s/\)$//' |
    while IFS= read -r p; do
      case "$p" in
        http://*|https://*|mailto:*|'#'*) continue ;;
      esac
      target=${p%%#*}
      [ -z "$target" ] && continue
      printf '%s\t%s\n' "$f" "$target"
    done >> "$pairs"
done

total=$(wc -l < "$pairs" | tr -d ' ')
broken=0

while IFS="$(printf '\t')" read -r f target; do
  dir=$(dirname "$f")
  if [ ! -e "$dir/$target" ]; then
    echo "BROKEN $f -> $target"
    broken=$((broken + 1))
  fi
done < "$pairs"

echo "링크 검사 완료: ${total}개 확인, ${broken}개 깨짐"
[ "$broken" -eq 0 ]
```

- [ ] **Step 2: 실행 권한을 주고 지금(분할 전) 트리에 돌린다**

```bash
chmod +x scripts/doc-link-check.sh
./scripts/doc-link-check.sh
```

기대: `0개 깨짐`으로 끝나고, **확인한 링크 수가 190보다 크다**(README·AGENTS의 링크가 더해진다).
확인 수가 0이면 검사가 죽은 것이다 — 다음 단계로 가지 말 것.

- [ ] **Step 3: 일부러 깨뜨려 검사가 고발하는지 본다 (없앴을 때로 판정)**

```bash
sed -i '' 's#](\.\./src/ReSet.Cli/Program\.cs)#](../src/ReSet.Cli/NoSuchFile.cs)#' docs/architecture.md
./scripts/doc-link-check.sh; echo "종료코드=$?"
```

기대: `BROKEN docs/architecture.md -> ../src/ReSet.Cli/NoSuchFile.cs` 한 줄과 `1개 깨짐`, 종료코드 1.
**안 뱉으면 검사가 죽은 것이다.** 되돌린다:

```bash
git checkout -- docs/architecture.md
./scripts/doc-link-check.sh
```

- [ ] **Step 4: 분할 전 기준선을 채집한다**

```bash
SP=/private/tmp/claude-501/-Users-payletter-git-root-ReSet/095e524d-22af-4ee5-a292-0754fad5ea57/scratchpad
mkdir -p "$SP/before"

./scripts/doc-link-check.sh | tail -1 > "$SP/before/link-count.txt"
grep -c '^```mermaid' docs/architecture.md > "$SP/before/mermaid-count.txt"
```

역검색 기준선 — 스킬 1-5가 훑는 **줄 수**를 분할 전후로 비교하기 위한 것이다.
심볼 목록은 최근 30커밋의 소스 diff에서 뽑는다. 어떤 목록이든 좋다, **전후가 같기만 하면 된다.**

```bash
git log --format=%H -n 30 -- src/ | tail -1 > "$SP/before/base-sha.txt"
BASE=$(cat "$SP/before/base-sha.txt")
git diff "$BASE"..HEAD -- src/ \
  | grep -E "^[+-]" | grep -v "^[+-][+-]" \
  | grep -oE '\b[A-Z][A-Za-z0-9]{3,}\b' | sort -u > "$SP/before/symbols.txt"

grep -nFf "$SP/before/symbols.txt" README.md AGENTS.md docs/architecture.md \
  | wc -l > "$SP/before/reverse-search.txt"

cat "$SP/before/link-count.txt" "$SP/before/mermaid-count.txt" "$SP/before/reverse-search.txt"
```

기대: mermaid `9`. 역검색 줄 수는 목록에 달렸으니 **적어 두기만** 한다.
Task 7이 같은 `symbols.txt`로 분할 후를 재어 **줄 수가 줄지 않았는지** 본다.

- [ ] **Step 5: 커밋**

```bash
git add scripts/doc-link-check.sh
git commit -F - <<'MSG'
feat: 깊이 무관 문서 링크 검증기를 세운다

종전 검사는 docs/architecture.md 와 ](../src/ 를 동시에 못박아, 문서가
갈리면 0개를 검사하고 조용히 통과한다. 확인한 링크 수를 함께 내어
발화 0 이 「성하다」인지 「안 본다」인지 가른다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_014cP5KM1SsNcfjBknH1NPwQ
MSG
```

---

## Task 2: 이사 — 17개 파일로 추출하고 바이트 동일성을 증명한다

**링크 깊이는 아직 안 고친다.** 먼저 원문 그대로 갈라 **byte-for-byte 동일**을 증명하고,
링크 교정은 Task 3에서 따로 한다. 둘을 한 번에 하면 diff에서 「이사」와 「수정」이 안 갈린다.

**Files:**

- Create: `docs/architecture/*.md` (17개)
- Create: `$SP/split.sh` (일회용, 커밋하지 않음)
- Modify: 없음 (허브는 Task 3에서 만든다)

**Interfaces:**

- Consumes: Task 1의 `scripts/doc-link-check.sh`
- Produces: `docs/architecture/` 17개 파일. 각 파일은 **정확히 2줄 머리말**(HTML 주석 + 빈 줄)로 시작하고, 3번째 줄부터가 원문 그대로다. Task 2 Step 4와 Task 3 Step 3이 `tail -n +3`으로 그 2줄을 벗기므로 줄 수를 바꾸면 검사가 틀린다.

- [ ] **Step 1: 추출 스크립트를 쓴다**

`$SP/split.sh`:

```sh
#!/usr/bin/env sh
# docs/architecture.md 를 절 단위로 가른다. 일회용.
#
# 머리말은 정확히 2줄(주석 + 빈 줄)이다. 바이트 동일성 검사가 그 2줄을 벗겨
# 원문과 대조하므로, 줄 수를 바꾸면 검사가 틀린다.
set -eu
SRC=docs/architecture.md
OUT=docs/architecture
mkdir -p "$OUT"

emit() {   # emit <시작행> <끝행> <파일명> <절표기>
  start=$1; end=$2; name=$3; sec=$4
  {
    printf '<!-- 아키텍처 정의서 %s — 허브: [docs/architecture.md](../architecture.md) -->\n' "$sec"
    printf '\n'
    sed -n "${start},${end}p" "$SRC"
  } > "$OUT/$name"
}

emit   42  203 2.2-module-catalog.md           '§2.2'
emit  204  463 3-execution-lifecycle.md        '§3'
emit  466  480 4.1-recursive-dependency.md     '§4.1'
emit  481  486 4.2-ms-description.md           '§4.2'
emit  487  499 4.3-tsql-ast.md                 '§4.3'
emit  500  774 4.4-verification-pipeline.md    '§4.4'
emit  775  788 4.5-multi-llm-provider.md       '§4.5'
emit  789  833 4.6-validator-engine.md         '§4.6'
emit  834  837 4.7-sandbox-seeding.md          '§4.7'
emit  838  844 4.8-incremental-cache.md        '§4.8'
emit  845  855 4.9-prompt-engineering.md       '§4.9'
emit  856  865 4.10-policy-extraction.md       '§4.10'
emit  866  876 4.11-instruction-bundling.md    '§4.11'
emit  877  924 4.12-step-floor-materials.md    '§4.12'
emit  925  955 4.13-prompt-cache-breakpoint.md '§4.13'
emit  956 1021 4.14-prd-derivation.md          '§4.14'
emit 1022 1075 5-secondary-features.md         '§5'

echo "추출 완료: $(ls "$OUT" | wc -l | tr -d ' ')개 파일"
```

**464–465행(`## 4.` 그룹 헤딩과 그 빈 줄)은 어디에도 안 간다.** 허브 목차가 §4 그룹을 대신한다.
그 두 줄이 정확히 71바이트이고, 그래서 이사 본문이 297,421이 된다.

- [ ] **Step 2: 돌린다**

```bash
SP=/private/tmp/claude-501/-Users-payletter-git-root-ReSet/095e524d-22af-4ee5-a292-0754fad5ea57/scratchpad
chmod +x "$SP/split.sh"
"$SP/split.sh"
ls docs/architecture/
```

기대: `추출 완료: 17개 파일`.

- [ ] **Step 3: 파일별 바이트를 설계서와 대조한다**

```bash
for f in docs/architecture/*.md; do
  printf "%8s  %s\n" "$(tail -n +3 "$f" | wc -c | tr -d ' ')" "$(basename "$f")"
done | sort -rn
```

기대(머리말 2줄 제외한 본문):

```
   92682  2.2-module-catalog.md
   52925  4.4-verification-pipeline.md
   43037  4.12-step-floor-materials.md
   20299  5-secondary-features.md
   18951  3-execution-lifecycle.md
   12420  4.5-multi-llm-provider.md
    7844  4.8-incremental-cache.md
    7813  4.1-recursive-dependency.md
    7618  4.3-tsql-ast.md
    7287  4.9-prompt-engineering.md
    6529  4.14-prd-derivation.md
    5937  4.6-validator-engine.md
    4515  4.11-instruction-bundling.md
    4392  4.10-policy-extraction.md
    2208  4.13-prompt-cache-breakpoint.md
    2119  4.2-ms-description.md
     845  4.7-sandbox-seeding.md
```

- [ ] **Step 4: 바이트 동일성을 증명한다 (이 태스크의 진짜 게이트)**

개수 일치는 약하다. **재조립본이 원본과 byte-for-byte 같아야 한다.**
비교 기준은 작업 파일이 아니라 **git의 원본**이다(비순환 오라클).

```bash
SP=/private/tmp/claude-501/-Users-payletter-git-root-ReSet/095e524d-22af-4ee5-a292-0754fad5ea57/scratchpad

git show ffbf61db:docs/architecture.md | tail -n +42 | sed '423,424d' > "$SP/expected.txt"

: > "$SP/actual.txt"
for f in 2.2-module-catalog 3-execution-lifecycle \
         4.1-recursive-dependency 4.2-ms-description 4.3-tsql-ast \
         4.4-verification-pipeline 4.5-multi-llm-provider 4.6-validator-engine \
         4.7-sandbox-seeding 4.8-incremental-cache 4.9-prompt-engineering \
         4.10-policy-extraction 4.11-instruction-bundling 4.12-step-floor-materials \
         4.13-prompt-cache-breakpoint 4.14-prd-derivation 5-secondary-features; do
  tail -n +3 "docs/architecture/$f.md" >> "$SP/actual.txt"
done

wc -c "$SP/expected.txt" "$SP/actual.txt"
diff "$SP/expected.txt" "$SP/actual.txt" && echo "바이트 동일 ✔"
```

`sed '423,424d'`는 42행 기준 상대 423~424행 — 원본 464~465행(`## 4.` 헤딩과 그 빈 줄)을 뺀다.

기대: 둘 다 **297,421**바이트이고 `diff`가 아무것도 안 뱉는다.
**한 줄이라도 다르면 다음 태스크로 가지 않는다.**

- [ ] **Step 5: 커밋 (이사만)**

```bash
git add docs/architecture/
git commit -F - <<'MSG'
docs: architecture.md 를 절 단위 17개 파일로 옮긴다 (내용 무변경)

원문 그대로의 이사다. 재조립본이 ffbf61db 의 원본과 byte-for-byte
같음을 확인했다(297,421바이트). 링크 깊이 교정과 허브 목차는 다음
커밋에서 따로 한다 — 한 커밋에 섞으면 diff 에서 이사와 수정이 안 갈린다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_014cP5KM1SsNcfjBknH1NPwQ
MSG
```

---

## Task 3: 링크 깊이 교정과 허브 재작성

**Files:**

- Modify: `docs/architecture/*.md` (상대 링크 깊이)
- Modify: `docs/architecture.md` (허브로 축소 + 목차)

**Interfaces:**

- Consumes: Task 2의 17개 파일, Task 1의 `scripts/doc-link-check.sh`
- Produces: 허브 `docs/architecture.md` ≤ 8,000바이트, `<!-- synced-through: 7ab3d10c -->` 보존

- [ ] **Step 1: 교정 전 링크 수를 센다**

```bash
grep -ho '](\.\./src/[^)]*)' docs/architecture/*.md | wc -l
```

> **⚠ 정정: 실측은 190이 아니라 213(190 + `../tests` 링크 23개 — 계획 당시 grep이 `../src`만 봤다)이었다. 근거: 커밋 `066bfe11`.**

기대: **190**. 다르면 Task 2가 뭔가 흘린 것이다 — 멈추고 원인을 찾는다.

- [ ] **Step 2: 깊이를 한 겹 더한다**

```bash
sed -i '' 's#](\.\./src/#](../../src/#g' docs/architecture/*.md
grep -ho '](\.\./\.\./src/[^)]*)' docs/architecture/*.md | wc -l
grep -ho '](\.\./src/[^)]*)' docs/architecture/*.md | wc -l
```

> **⚠ 정정: 첫 수는 190이 아니라 213이었다. 근거: 커밋 `066bfe11`.**

기대: 첫 수 **190**, 둘째 수 **0**.

- [ ] **Step 3: 증가분이 정확히 570바이트인지 본다**

> **⚠ 정정: 링크는 190개가 아니라 213개, 증가분은 570바이트가 아니라 639바이트였다. 근거: 커밋 `066bfe11`.**

링크 190개 × 3바이트(`../`)다. 이 검산이 맞으면 sed가 의도한 것만 건드렸다.

```bash
SP=/private/tmp/claude-501/-Users-payletter-git-root-ReSet/095e524d-22af-4ee5-a292-0754fad5ea57/scratchpad
: > "$SP/actual2.txt"
for f in 2.2-module-catalog 3-execution-lifecycle \
         4.1-recursive-dependency 4.2-ms-description 4.3-tsql-ast \
         4.4-verification-pipeline 4.5-multi-llm-provider 4.6-validator-engine \
         4.7-sandbox-seeding 4.8-incremental-cache 4.9-prompt-engineering \
         4.10-policy-extraction 4.11-instruction-bundling 4.12-step-floor-materials \
         4.13-prompt-cache-breakpoint 4.14-prd-derivation 5-secondary-features; do
  tail -n +3 "docs/architecture/$f.md" >> "$SP/actual2.txt"
done
echo "증가분: $(( $(wc -c < "$SP/actual2.txt") - 297421 ))"
```

기대: `570`.

- [ ] **Step 4: 허브를 쓴다**

원본 1–41행을 그대로 두고, `### 2.2.` 자리부터 목차로 바꾼다.

```bash
git show ffbf61db:docs/architecture.md | head -41 > docs/architecture.md
```

이어서 아래 내용을 `docs/architecture.md` 끝에 덧붙인다.

```markdown
## 절 목차 (Section Index)

각 절은 독립 파일이다. **절 번호가 주소다** — `AGENTS.md`와 코드 주석이 `§4.4`처럼
절 번호로 부르므로, 파일을 옮기거나 이름을 바꿀 때 번호 접두사를 지우지 말 것.

| 절 | 내용 | 파일 |
| :--- | :--- | :--- |
| §2.2 | 핵심 모듈 및 클래스 목록 | [2.2-module-catalog.md](./architecture/2.2-module-catalog.md) |
| §3 | 전체 실행 라이프사이클 및 데이터 흐름 (§3.1–3.4) | [3-execution-lifecycle.md](./architecture/3-execution-lifecycle.md) |
| §4.1 | DFS 기반 재귀적 의존성 수집 및 Soft Fail (§4.1.1 포함) | [4.1-recursive-dependency.md](./architecture/4.1-recursive-dependency.md) |
| §4.2 | MS_Description 확장 속성 맵핑 및 AI 보완 | [4.2-ms-description.md](./architecture/4.2-ms-description.md) |
| §4.3 | T-SQL AST 정적 분석 고도화 (ScriptDom) | [4.3-tsql-ast.md](./architecture/4.3-tsql-ast.md) |
| §4.4 | 3단계 신뢰성 검증 파이프라인 | [4.4-verification-pipeline.md](./architecture/4.4-verification-pipeline.md) |
| §4.5 | 다중 AI 공급자(Multi-LLM Provider) 추상화 | [4.5-multi-llm-provider.md](./architecture/4.5-multi-llm-provider.md) |
| §4.6 | 소스코드 정합성 검증 엔진 (Validator) | [4.6-validator-engine.md](./architecture/4.6-validator-engine.md) |
| §4.7 | 관계지향 모의 데이터 적재 및 수명주기 격리 | [4.7-sandbox-seeding.md](./architecture/4.7-sandbox-seeding.md) |
| §4.8 | SHA-256 해시 기반 로컬 증분 캐싱 | [4.8-incremental-cache.md](./architecture/4.8-incremental-cache.md) |
| §4.9 | 하이브리드 영문화 프롬프트 및 환각 차단 | [4.9-prompt-engineering.md](./architecture/4.9-prompt-engineering.md) |
| §4.10 | 정산 정책 도출 (Policy Extraction) | [4.10-policy-extraction.md](./architecture/4.10-policy-extraction.md) |
| §4.11 | 지시서 번들 분할과 회차 단위 코드 생성 | [4.11-instruction-bundling.md](./architecture/4.11-instruction-bundling.md) |
| §4.12 | 단계 하한 검사 대조 기준의 결정론적 보강 | [4.12-step-floor-materials.md](./architecture/4.12-step-floor-materials.md) |
| §4.13 | Claude 프롬프트 캐시 중단점 | [4.13-prompt-cache-breakpoint.md](./architecture/4.13-prompt-cache-breakpoint.md) |
| §4.14 | 명세서 기반 요구사항 도출 (PRD Derivation) | [4.14-prd-derivation.md](./architecture/4.14-prd-derivation.md) |
| §5 | TUI/CLI 부가 기능 및 복구 파이프라인 (§5.1–5.8) | [5-secondary-features.md](./architecture/5-secondary-features.md) |

<!-- synced-through: 7ab3d10c -->
```

그 뒤 크기를 잰다.

```bash
wc -c docs/architecture.md
```

기대: **8,000바이트 이하**.

`synced-through` 값은 원본에 있던 것을 그대로 옮긴 것이다. **이사는 동기화가 아니므로 올리지 않는다** —
"검토했으나 변경 불필요"와 "동기화됨"은 다르다.

- [ ] **Step 5: 목차가 절을 빠짐없이 가리키는지 기계로 본다**

```bash
grep -c '^| §' docs/architecture.md
ls docs/architecture/*.md | wc -l
```

기대: 둘 다 **17**.

목차가 가리키는 파일이 실제로 있는지, 반대로 파일이 목차에 다 실렸는지 양방향으로 본다.

```bash
grep -oE '\]\(\./architecture/[^)]+\)' docs/architecture.md \
  | sed -E 's#^\]\(\./architecture/##; s#\)$##' | sort > /tmp/toc.txt
ls docs/architecture/ | sort > /tmp/files.txt
diff /tmp/toc.txt /tmp/files.txt && echo "목차 ↔ 파일 일치 ✔"
```

기대: `diff`가 아무것도 안 뱉는다. 한쪽만 있는 이름이 나오면 그 절은 **주소가 없는 미아**다.

- [ ] **Step 6: 링크 검사를 돌린다**

```bash
./scripts/doc-link-check.sh
```

기대: `0개 깨짐`, 그리고 확인한 링크 수가 Task 1의 기준선보다 **크거나 같다**(허브 목차 17개가 늘었다).

- [ ] **Step 7: 커밋**

> **⚠ 정정: 아래 커밋 메시지 템플릿의 「190개」·「570바이트」는 실측과 다르다 — 실제 커밋
> `066bfe11`의 메시지는 213개(190 + `../tests` 23개)·639바이트를 싣는다. 이 템플릿을
> 그대로 실행하면 거짓 수치가 커밋 메시지로 박제된다. 근거: 커밋 `066bfe11`.**

```bash
git add docs/architecture.md docs/architecture/
git commit -F - <<'MSG'
docs: 분할 파일의 링크 깊이를 고치고 허브를 목차로 바꾼다

링크 190개가 ../src → ../../src 로 한 겹 깊어져 정확히 570바이트 늘었다.
허브는 제목·서문·§1·§2.1 과 절 목차만 남는다. 절 번호가 주소이므로
AGENTS.md 는 한 글자도 안 건드린다 — 예산 여유가 69바이트뿐이다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_014cP5KM1SsNcfjBknH1NPwQ
MSG
```

---

## Task 4: 예산 게이트를 허브까지 넓힌다

허브가 다시 부풀면 분할이 무의미해진다. **사람의 절제가 아니라 테스트가 막게 한다.**

**Files:**

- Modify: `tests/ReSet.Core.Tests/documentation-budget-baseline.txt`
- Modify: `tests/ReSet.Core.Tests/DocumentationBudgetTests.cs:66`

**Interfaces:**

- Consumes: Task 3의 허브
- Produces: baseline에 `docs/architecture.md = 8000` 한 줄

- [ ] **Step 1: 지금 허브 크기를 잰다**

```bash
wc -c docs/architecture.md
```

- [ ] **Step 2: baseline을 고친다**

같은 파일의 주석 *"architecture.md와 README.md는 자동 로드되지 않으므로 예산이 없다"* 가
이제 거짓이 되므로 함께 고친다.

기존:

```
# architecture.md와 README.md는 자동 로드되지 않으므로 예산이 없다.

AGENTS.md = 35000
```

새로:

```
# README.md는 자동 로드되지 않으므로 예산이 없다.
#
# docs/architecture.md는 자동 로드되지는 않지만 예산이 있다. 이 파일은 2026-09-07에
# 299,967바이트에서 허브로 줄었고(절 본문은 docs/architecture/ 17개로 갔다), 상한이
# 없으면 같은 자리로 되돌아간다 — AGENTS.md가 108KB까지 갔던 경로가 그것이다.
# 절 본문을 허브에 되쓰지 말고 docs/architecture/4.x-*.md로 보내라.

AGENTS.md = 35000
docs/architecture.md = 8000
```

- [ ] **Step 3: 테스트를 돌려 통과를 확인한다**

```bash
dotnet test --filter DocumentationBudget 2>&1 | tail -3
```

기대: 실패 0 · 건너뜀 0.

- [ ] **Step 4: 상한이 실제로 무는지 되돌려 확인한다**

```bash
head -c 9000 /dev/zero | tr '\0' 'x' >> docs/architecture.md
dotnet test --filter DocumentationBudget 2>&1 | tail -6
git checkout -- docs/architecture.md
dotnet test --filter DocumentationBudget 2>&1 | tail -3
```

기대: 가운데 실행이 **실패**하며 `docs/architecture.md: 상한 8,000 바이트, 실제 …`를 낸다.
안 실패하면 등록이 안 먹은 것이다 — baseline 파싱 형식을 확인한다.

- [ ] **Step 5: `Routing` 상수의 거처 문자열을 고친다**

`tests/ReSet.Core.Tests/DocumentationBudgetTests.cs:66`

기존:

```csharp
        "  여러 파일을 함께 봐야  → docs/architecture.md §4.x로 옮기십시오\n" +
```

새로:

```csharp
        "  여러 파일을 함께 봐야  → docs/architecture/4.x-*.md 로 옮기십시오\n" +
```

- [ ] **Step 6: 전체 테스트**

```bash
dotnet test 2>&1 | tail -3
```

기대: 실패 0 · 건너뜀 0.

- [ ] **Step 7: 커밋**

```bash
git add tests/ReSet.Core.Tests/documentation-budget-baseline.txt tests/ReSet.Core.Tests/DocumentationBudgetTests.cs
git commit -F - <<'MSG'
test: 허브에 8KB 예산을 걸고 거처 안내를 분할 경로로 고친다

상한이 없으면 허브는 왔던 자리로 되돌아간다 — AGENTS.md 가 108KB 까지
갔던 경로가 그것이다. 9,000바이트를 덧붙여 실제로 무는 것을 확인했다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_014cP5KM1SsNcfjBknH1NPwQ
MSG
```

---

## Task 5: `reset-doc-sync` 스킬 개정

**Files:**

- Modify: `.claude/skills/reset-doc-sync/SKILL.md` (편집 지점 열둘). `.agents/skills/reset-doc-sync`는 심링크라 따로 고칠 사본이 없다.

**Interfaces:**

- Consumes: Task 3의 파일 배치, Task 1의 `scripts/doc-link-check.sh`
- Produces: 세 문서 고정이 사라진 스킬

각 편집은 **정확한 원문 → 새 문장**이다. 줄번호는 편집하면 밀리므로 **원문 문자열로 찾는다.**

- [ ] **Step 1: 4행 — 대상 문서 서술**

원문: `  ReSet 프로젝트의 핵심 문서 3종(README.md, AGENTS.md, docs/architecture.md)을 소스 코드와 동기화한다.`

새로: `  ReSet 프로젝트의 핵심 문서(README.md, AGENTS.md, docs/architecture.md 허브와 docs/architecture/ 절 파일들)를 소스 코드와 동기화한다.`

- [ ] **Step 2: 20행 — 담당 정보 표를 두 행으로 가른다**

원문:

```
| `docs/architecture.md` | 내부 모듈 구조, 클래스 목록(2.2 테이블), 데이터 흐름, 핵심 메커니즘 | 새 클래스/서비스 추가, 리팩토링, 알고리즘 변경 |
```

새로:

```
| `docs/architecture.md` (허브) | 제목·서문·§1 개요·§2.1 레이어링과 **절 목차**. 8,000바이트 예산이 걸려 있다 | 절이 늘거나 파일명이 바뀔 때만 |
| `docs/architecture/*.md` | 내부 모듈 구조, 클래스 목록(`2.2-module-catalog.md`), 데이터 흐름, 핵심 메커니즘 | 새 클래스/서비스 추가, 리팩토링, 알고리즘 변경 |
```

- [ ] **Step 3: 31행 — 크기 실측 커맨드와 낡은 수치**

원문: `` `LC_ALL=C wc -c README.md AGENTS.md docs/architecture.md`로 직접 재측정한다). ``

새로: `` `LC_ALL=C wc -c README.md AGENTS.md docs/architecture.md docs/architecture/*.md`로 직접 재측정한다). ``

같은 문단의 `README 약 60KB, AGENTS 약 45KB, architecture 약 140KB`도 오늘 값으로 고친다 —
**그 수가 두 배로 틀어져 있었던 것이 이 분할의 방아쇠였다.**

새로: `README 약 77KB, AGENTS 약 35KB, architecture 허브 약 8KB + 절 파일 합 약 298KB`

- [ ] **Step 4: 44행·53행 — BASE 산정에 넷째 문서를 넣는다 (버그 ③)**

원문 44행:

```
BASE=$(grep -ho 'synced-through: [0-9a-f]\{7,40\}' README.md AGENTS.md docs/architecture.md \
```

새로:

```
BASE=$(grep -ho 'synced-through: [0-9a-f]\{7,40\}' README.md AGENTS.md docs/architecture.md docs/output-artifacts.md \
```

원문 53행:

```
BASE=$(git merge-base $(for f in README.md AGENTS.md docs/architecture.md; do
```

새로:

```
BASE=$(git merge-base $(for f in README.md AGENTS.md docs/architecture.md docs/output-artifacts.md; do
```

두 블록 아래에 한 문단을 잇는다:

```
**`docs/architecture/` 절 파일에는 `synced-through`를 두지 않는다.** 동기화 지점은
「문서 묶음」의 속성이지 파일의 속성이 아니다 — 17개를 매번 갱신하게 만들면 빠뜨리고,
빠뜨린 파일은 다음 회차의 스캔 범위 밖으로 나간다. 원장은 위 **넷**이 대표한다.
(`docs/output-artifacts.md`는 2026-09-07까지 주석은 박혀 있는데 이 계산에서 빠져 있었다.)
```

- [ ] **Step 5: 75행·82행·94행·127행 — 스캔 대상을 넓힌다**

네 곳 모두 `README.md AGENTS.md docs/architecture.md`를
`README.md AGENTS.md docs/architecture.md docs/architecture/*.md`로 바꾼다.

- 75행: `grep -n "^#\{1,3\} " …` (목차 확보)
- 82행: `grep -rn "NewClassName\|NewSettingKey" …`
- 94행: `grep -nFf /tmp/changed-symbols.txt …` (**1-5 역검색**)
- 127행: 부정형 서술 점검 blocked의 대상 목록

**94행이 치명적인 자리다.** 1-5 역검색은 "기존 서술이 **거짓이 된 곳**"을 찾는 유일한 장치이고,
분할 후 이 줄이 안 넓어지면 산문 298KB 중 8KB만 훑고 **발화 0**을 뱉는다.
그 블록 아래에 한 문단을 덧붙인다:

```
**대상 목록에 `docs/architecture/*.md`가 빠지면 이 검색은 허브만 훑고 발화 0을 낸다.**
발화 0이 「거짓이 된 서술이 없다」인지 「안 봤다」인지 가르려면 훑은 파일 수를 함께 세라.
```

- [ ] **Step 6: 148행 — 3-0 거처 판정 표**

원문: `` | 여러 파일을 함께 봐야 안다 | `docs/architecture.md §4.x` | 라우팅 표 한 줄 | ``

새로: `` | 여러 파일을 함께 봐야 안다 | `docs/architecture/4.x-*.md` (§ 번호가 파일명이다) | 라우팅 표 한 줄 | ``

- [ ] **Step 7: 166행 — 작성 원칙 제목과 항목 셋**

원문 제목: `**docs/architecture.md 작성 원칙**`
새로: `**docs/architecture/ 작성 원칙**`

원문: `` - `### 2.2. 핵심 모듈 및 클래스 목록` 테이블에 새 클래스/인터페이스 행 추가 (기존 열 구성과 `<br/>` 사용 형식 유지) ``
새로: `` - `docs/architecture/2.2-module-catalog.md`의 테이블에 새 클래스/인터페이스 행 추가 (기존 열 구성과 `<br/>` 사용 형식 유지) ``

원문: `` - 링크는 이 문서 기준 상대 경로: `[ClassName](../src/...)` ``
새로: `` - 링크는 **그 파일 기준** 상대 경로다. 절 파일은 `docs/architecture/` 안에 있으므로 `[ClassName](../../src/...)`이고, 허브는 `[ClassName](../src/...)`이다. 깊이를 헷갈리면 `scripts/doc-link-check.sh`가 잡는다. ``

원문: `` - `## 4. 핵심 아키텍처 메커니즘` 섹션은 알고리즘/패턴이 실제로 바뀔 때만 수정 ``
새로: `` - `4.x-*.md` 절 파일은 알고리즘/패턴이 실제로 바뀔 때만 수정. **새 절을 만들면 허브 목차에 행을 더한다** — 안 더하면 그 절은 주소가 없는 미아가 된다. ``

- [ ] **Step 8: 227–233행 — 링크 검증을 스크립트로 대체한다**

원문:

```
# 링크 유효성: architecture.md의 상대 경로
grep -o '](\.\./src/[^)]*)' docs/architecture.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "docs/$p" ] || echo "BROKEN architecture.md: $p"; done

# 링크 유효성: AGENTS.md / README.md의 레포 루트 기준 경로
grep -ho '](\./[^)]*)' AGENTS.md README.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "$p" ] || echo "BROKEN: $p"; done
```

새로:

```
# 링크 유효성 — 파일별 dirname 기준이라 문서가 몇 겹으로 갈리든 따라간다.
# 종전 검사는 `docs/architecture.md`와 `](../src/`를 동시에 못박아, 문서가 갈리면
# 0개를 검사하고 조용히 통과했다. 이 스크립트는 **확인한 링크 수**를 함께 낸다 —
# "0개 깨짐"이 「성하다」인지 「안 봤다」인지 그 수로 가른다.
./scripts/doc-link-check.sh
```

- [ ] **Step 9: 246행 — synced-through 확인**

원문: `grep -n 'synced-through' README.md AGENTS.md docs/architecture.md`

새로: `grep -n 'synced-through' README.md AGENTS.md docs/architecture.md docs/output-artifacts.md`

- [ ] **Step 10: 253행 — mermaid 추출을 파일별로**

원문:

````
awk '/^```mermaid/{f=1;next}/^```$/{f=0}f' docs/architecture.md > /tmp/d.mmd
mmdc -i /tmp/d.mmd -o /tmp/d.svg && echo "MERMAID OK"
````

새로:

````
for f in docs/architecture.md docs/architecture/*.md; do
  awk '/^```mermaid/{f=1;next}/^```$/{f=0}f' "$f" > /tmp/d.mmd
  [ -s /tmp/d.mmd ] || continue
  mmdc -i /tmp/d.mmd -o /tmp/d.svg && echo "MERMAID OK: $f"
done
````

한 파일에 블록이 여럿이면 이어 붙인 결과가 하나의 다이어그램이 아니라 렌더가 실패한다 —
그때는 **수정한 절만 잘라** 검사한다(원래 주석이 말하던 것과 같다).

- [ ] **Step 11: 세 문서 고정이 남았는지 훑는다**

```bash
grep -n 'README.md AGENTS.md docs/architecture.md' .claude/skills/reset-doc-sync/SKILL.md
```

기대: 걸리는 줄마다 뒤에 `docs/architecture/*.md` 또는 `docs/output-artifacts.md`가 붙어 있다.
**맨몸으로 셋만 있는 줄은 0개**여야 한다.

```bash
grep -nE 'README\.md AGENTS\.md docs/architecture\.md( |$)' .claude/skills/reset-doc-sync/SKILL.md \
  | grep -v 'docs/architecture/\*\.md' | grep -v 'docs/output-artifacts.md' || echo "맨몸 고정 없음 ✔"
```

- [ ] **Step 12: 커밋**

> **⚠ 정정: 아래 커밋 메시지 템플릿의 「링크 190개」는 실측과 다르다 — 실제로는 213개
> (190 + `../tests` 23개)다. 이 템플릿을 그대로 실행하면 거짓 수치가 커밋 메시지로
> 박제된다. 근거: 커밋 `066bfe11`.**

```bash
git add .claude/skills/reset-doc-sync/SKILL.md
git commit -F - <<'MSG'
docs: doc-sync 스킬의 세 문서 고정을 풀고 원장 누락을 닫는다

역검색(1-5)과 링크 검증이 파일 이름 셋에 못박혀 있어, 분할하면 산문
298KB 중 8KB만 훑고 링크 190개를 0개 검사하며 발화 0으로 통과한다.
BASE 산정에서 output-artifacts.md 가 빠져 있던 것도 함께 닫는다 —
주석은 박혀 있는데 기준점 계산이 안 보고 있었다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_014cP5KM1SsNcfjBknH1NPwQ
MSG
```

---

## Task 6: `scripts/doc-audit.sh` 확장 — 설계서가 못 센 여섯 번째 소비자

**설계서(2026-09-07)는 소비자를 스킬 다섯 + 테스트 상수 하나로 셌다. 실제로는 이 스크립트가 하나 더 있다.**
분할 후 이 도구는 `architecture.md`에서 근거를 못 찾아 **모든 행을 `근거없음(이동필요)`으로 뒤집는다.**
그 스크립트 자신의 머리 주석이 경고하는 *"위험한 방향의 오류 — 지우면 안 되는 서술을 지우게 만든다"* 가 바로 이것이다.

**Files:**

- Modify: `scripts/doc-audit.sh` (72행·78행의 grep 대상, 머리 주석)

**Interfaces:**

- Consumes: Task 3의 파일 배치
- Produces: 허브와 절 파일을 함께 보는 `doc-audit.sh`

- [ ] **Step 1: 분할 전 근거 바이트를 오라클로 떠 둔다 (비순환 기준)**

`doc-audit.sh`의 판정은 `grep -F "$path" docs/architecture.md`의 **바이트 수** 하나로 갈린다.
그 수를 분할 전 커밋에서 직접 재어 둔다. 스크립트 전체를 돌릴 필요가 없다.

```bash
SP=/private/tmp/claude-501/-Users-payletter-git-root-ReSet/095e524d-22af-4ee5-a292-0754fad5ea57/scratchpad
mkdir -p "$SP/audit-oracle"
git show ffbf61db:docs/architecture.md > "$SP/audit-oracle/architecture-before.md"

for p in src/ReSet.Cli/Program.cs \
         src/ReSet.Core/Services/AiService.cs \
         src/ReSet.Core/Services/CacheManager.cs \
         src/ReSet.Core/Services/MechanicalValidator.cs \
         src/ReSet.Validator.Core/Services/DataComparisonService.cs; do
  printf "%s\t%s\n" "$p" \
    "$(LC_ALL=C grep -F -h "$p" "$SP/audit-oracle/architecture-before.md" | LC_ALL=C wc -c | tr -d ' ')"
done > "$SP/audit-oracle/before.tsv"
cat "$SP/audit-oracle/before.tsv"
```

- [ ] **Step 2: 72행·78행을 고친다**

원문 72행:

```sh
    arch=$(LC_ALL=C grep -F -h "$path" docs/architecture.md 2>/dev/null | LC_ALL=C wc -c | tr -d ' ')
```

새로:

```sh
    arch=$(LC_ALL=C grep -F -h "$path" docs/architecture.md docs/architecture/*.md 2>/dev/null | LC_ALL=C wc -c | tr -d ' ')
```

원문 78행:

```sh
    arch=$(LC_ALL=C grep -h "$sym" docs/architecture.md 2>/dev/null | LC_ALL=C wc -c | tr -d ' ')
```

새로:

```sh
    arch=$(LC_ALL=C grep -h "$sym" docs/architecture.md docs/architecture/*.md 2>/dev/null | LC_ALL=C wc -c | tr -d ' ')
```

머리 주석의 사용례(`#   ./scripts/doc-audit.sh 20 133 …`) 아래에 한 문단을 잇는다:

```
# 2026-09-07부터 architecture.md는 허브와 docs/architecture/ 절 파일로 갈렸다.
# 두 자리를 함께 grep 하지 않으면 모든 행이 "근거없음(이동필요)"으로 뒤집혀,
# 이 파일 머리가 경고하는 위험한 방향의 오류를 그대로 낸다 — 지우면 안 되는
# 서술을 지우게 만드는 쪽이다.
```

- [ ] **Step 3: 분할 후 근거 바이트가 보존됐는지 본다**

```bash
SP=/private/tmp/claude-501/-Users-payletter-git-root-ReSet/095e524d-22af-4ee5-a292-0754fad5ea57/scratchpad
for p in src/ReSet.Cli/Program.cs \
         src/ReSet.Core/Services/AiService.cs \
         src/ReSet.Core/Services/CacheManager.cs \
         src/ReSet.Core/Services/MechanicalValidator.cs \
         src/ReSet.Validator.Core/Services/DataComparisonService.cs; do
  printf "%s\t%s\n" "$p" \
    "$(LC_ALL=C grep -F -h "$p" docs/architecture.md docs/architecture/*.md 2>/dev/null | LC_ALL=C wc -c | tr -d ' ')"
done > "$SP/audit-oracle/after.tsv"
diff "$SP/audit-oracle/before.tsv" "$SP/audit-oracle/after.tsv" && echo "근거 바이트 보존 ✔"
```

기대: **차이 없음.** 링크 깊이가 `../src` → `../../src`로 바뀌었지만 `grep -F "$path"`가 찾는 것은
`src/ReSet.Cli/Program.cs`라는 **부분 문자열**이라 접두사 변화에 걸리지 않는다.
차이가 나면 그 자리를 그대로 보고한다 — 조용히 넘기지 않는다.

- [ ] **Step 4: 실제로 돌려 본다**

```bash
./scripts/doc-audit.sh 20 40 | tail -25
```

기대: 마지막 줄에 `감사 완료: N행 처리`가 있고, 판정 칸이 전부 `근거없음(이동필요)`으로
뒤집혀 있지 **않다**.

- [ ] **Step 5: 커밋**

```bash
git add scripts/doc-audit.sh
git commit -F - <<'MSG'
fix: doc-audit.sh 가 허브와 절 파일을 함께 본다

분할 후 이 도구는 architecture.md 에서 근거를 못 찾아 모든 행을
근거없음(이동필요)으로 뒤집는다. 이 파일 머리가 경고하는 위험한
방향의 오류 — 지우면 안 되는 서술을 지우게 만드는 쪽 — 이 그것이다.
분할 전 커밋에서 뜬 근거 바이트와 대조해 보존을 확인했다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_014cP5KM1SsNcfjBknH1NPwQ
MSG
```

---

## Task 7: 최종 게이트 — 없앴을 때로 판정한다

**Files:** 없음(검증만). 실패가 나오면 그 자리에서 고치고 재검증한다.

- [ ] **Step 1: `AGENTS.md`가 안 바뀌었는지 확인한다 (합격 기준 ①)**

```bash
git diff --stat ffbf61db..HEAD -- AGENTS.md
wc -c AGENTS.md
```

기대: `git diff --stat`이 **아무것도 안 낸다**. `wc -c`는 **34931**.

- [ ] **Step 2: 링크 검사 — 그리고 그것이 살아 있는지 (합격 기준 ③)**

```bash
./scripts/doc-link-check.sh
```

기대: `0개 깨짐`.

```bash
sed -i '' 's#](\.\./\.\./src/ReSet.Cli/Program\.cs)#](../../src/ReSet.Cli/NoSuchFile.cs)#' docs/architecture/2.2-module-catalog.md
./scripts/doc-link-check.sh; echo "종료코드=$?"
git checkout -- docs/architecture/2.2-module-catalog.md
./scripts/doc-link-check.sh
```

기대: 가운데 실행이 **정확히 1줄**의 BROKEN과 종료코드 1을 낸다. 그 뒤 다시 `0개 깨짐`.

- [ ] **Step 3: 역검색이 좁아지지 않았는지 (R2)**

Task 1이 떠 둔 **같은 심볼 목록**으로 잰다.

```bash
SP=/private/tmp/claude-501/-Users-payletter-git-root-ReSet/095e524d-22af-4ee5-a292-0754fad5ea57/scratchpad
echo -n "분할 전: "; cat "$SP/before/reverse-search.txt"
echo -n "분할 후: "; grep -nFf "$SP/before/symbols.txt" README.md AGENTS.md docs/architecture.md docs/architecture/*.md | wc -l
```

기대: **분할 후 ≥ 분할 전.** 줄면 스캔이 좁아진 것이다 — Task 5 Step 5로 돌아간다.

- [ ] **Step 4: 허브 예산과 목차 (합격 기준 ④⑤)**

```bash
wc -c docs/architecture.md
grep -c '^| §' docs/architecture.md
ls docs/architecture/*.md | wc -l
grep -n 'docs/architecture.md' tests/ReSet.Core.Tests/documentation-budget-baseline.txt
```

기대: 8,000 이하 · 목차 행 **17** · 파일 **17** · baseline에 등록된 줄이 있다.

- [ ] **Step 5: mermaid (합격 기준 ⑦)**

```bash
total=0
for f in docs/architecture.md docs/architecture/*.md; do
  n=$(grep -c '^```mermaid' "$f" || true)
  [ "$n" -gt 0 ] && echo "$n  $f"
  total=$((total + n))
done
echo "합계 $total (기대 9)"
command -v mmdc >/dev/null && echo "mmdc 있음" || echo "mmdc 없음 — 렌더 검사 건너뜀(보고에 남길 것)"
```

`mmdc`가 없으면 **없다는 사실을 보고에 남긴다** — 건너뛰고 침묵하지 않는다.

- [ ] **Step 6: 전체 테스트 (합격 기준 ②)**

```bash
dotnet test 2>&1 | tail -3
```

기대: **실패 0 · 건너뜀 0**. 건너뜀이 0이 아니면 코퍼스 심링크 넷을 먼저 확인한다.

- [ ] **Step 7: 최종 보고**

다음을 한 표로 낸다. **못 잰 것은 "못 쟀다"고 적는다.**

| 합격 기준 | 기대 | 실측 |
| :--- | :--- | :--- |
| ① AGENTS.md 바이트 변화 | 0 (34,931 유지) | |
| ② dotnet test | 실패 0 · 건너뜀 0 | |
| ③ 링크 검사 / 파괴 시험 | 0개 깨짐 / 정확히 1줄 | |
| ④ 목차 ↔ 파일 | 17 = 17, diff 없음 | |
| ⑤ 허브 크기 | ≤ 8,000 · baseline 등록 | |
| ⑥ 이사 바이트 보존 | 297,421 (+570 링크) — **⚠ 실측 정정: +639(213 링크). 근거: 커밋 `066bfe11`.** | |
| ⑦ mermaid | 9블록 | |
| R2 역검색 | 분할 후 ≥ 분할 전 | |

---

## 발견 — 이 계획의 범위 밖 (사람 결정 필요)

계획을 쓰다 **분할과 무관하게 이미 낡은 인용 셋**을 찾았다. 고치지 않고 보고만 한다 —
고치려면 "그 주장이 지금 어느 절에 사는가"를 정해야 하고, 그것은 이번 작업이 명시적으로 제외한 **내용 수정**이다.

- `src/ReSet.Core/Services/MechanicalValidator.cs:1122`
- `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs:1956`
- `tests/ReSet.Core.Tests/LegacyReturnCodeBindingTests.cs:285`

셋 다 *"`docs/architecture.md:433-434`가 `UPDATE bsj SET … FROM batch.BatchStepJournal bsj` 형태를
이 저장소의 표준 T-SQL 관용으로 명시한다"* 고 적는다. **그런데 오늘 433–434행은 §3.4의 mermaid 노드
(검증기 TUI 메뉴)다.** 그 주장을 담은 문장을 `architecture.md` 어디에서도 찾지 못했다 —
§4.3(495–496행)이 `UpdateSpecification`의 AST 추출을 다루지만 "표준 관용"이라는 선언은 아니다.

분할이 이것을 만들지 않았다. 다만 분할 뒤에는 그 줄번호가 **존재조차 하지 않게** 되므로
낡음이 눈에 띄는 시점이 앞당겨진다. 별도 과제로 여는 것을 권한다.

과거 `plans/`·`specs/`·`audit-reports/`의 줄번호 인용은 **역사 기록이므로 건드리지 않는다.**
