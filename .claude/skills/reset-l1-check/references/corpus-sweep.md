# 실제 코퍼스 전수 스윕

단위 테스트로는 두 부류가 보이지 않는다 — **검사가 한 번도 안 도는 것**과
**실제 명세서에만 나는 거짓 양성**. 둘 다 스위트가 초록인 채로 지나간다.
그래서 새 L1 검사는 합성 픽스처 말고 실제 산출물 전수로 한 번 더 확인한다.

## 먼저 — 버전 왜곡을 확인하라

**코퍼스는 지금 검사하는 코드와 같은 버전이 만든 것이어야 한다.** 아니면 결과가
전부 노이즈다. 산출물은 캐시 포맷 버전마다 표 모양이 달라지므로, 옛 버전이 만든
명세서를 새 코드의 기대값으로 대조하면 정상 산출물이 무더기로 결함이 된다.

```bash
python3 -c "
import json,io
from collections import Counter
idx=json.load(io.open('output/.sp_cache_index.json',encoding='utf-8-sig'))
ent=idx.get('Entries') or idx
print(dict(sorted(Counter(v.get('FormatVersion') for v in ent.values()).items())))
"
grep -n 'CurrentCacheFormatVersion = ' src/ReSet.Core/Services/CacheManager.cs
```

분포가 한 값이고 그 값이 코드의 `CurrentCacheFormatVersion`과 같아야 한다.
갈려 있으면 스윕 결과에서 **그 차이가 만든 오류 종류를 먼저 걸러내고** 읽어라.

> 실측(2026-08-23): 다른 세션이 같은 `output/`에 버전 11로 재생성하는 중에 버전 10
> 코드로 스윕을 돌렸더니 `SetPredicateMismatch`가 **105건** 나왔다. 전부 세대 차이였다.
> 같은 실행에서 우리가 만든 검사들은 정상이었다(`MachineTableShapeBroken` 2,
> `InsertMappingTableNameMismatch` 1 — 전부 진짜 양성).

## 하네스

`output/`은 gitignore라 워크트리에 없다. 메인 저장소 절대 경로를 쓴다. 같은 이유로 코퍼스를
읽는 단위 테스트(`AxisAGoldenCaseTests` 등)는 워크트리에서 **건너뜀**으로 표시된다 — 재료
`output/` 하나를 걸면 전부 돈다(`.git/info/exclude`에 `output` 등록됨).

```bash
ln -s <main>/output output
```

**2026-09-07 이전에는 `output.bak-2026-08-22` 도 함께 걸어야 했다.** 사람이 과거 판 코퍼스를
지우기로 결정하면서 그것과 `CoverageMapGoldenTests` 의 과거 판 대조 요구 둘을 함께 폐기했다 —
지금 그 링크를 걸려 하면 없는 디렉터리를 가리킨다.
경위: `docs/audit-reports/2026-09-07-과거판-코퍼스-폐기.md`. 정본은 AGENTS.md 의 워크트리 코퍼스 절.
스크래치 디렉터리에 만들고 검증이 끝나면 지운다.

`sweep.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="/Users/payletter/git-root/ReSet/src/ReSet.Core/ReSet.Core.csproj" />
  </ItemGroup>
</Project>
```

`ImplicitUsings`를 빼면 `Console`·`Path`·`List`가 전부 미해결로 떨어진다.

`Program.cs` — **판독을 둘 낸다.** 왜 둘인지는 바로 아래 절이 근거다.

```csharp
using System.Text.Json;
using ReSet.Core.Models;
using ReSet.Core.Services;

var root = args.Length > 0 ? args[0] : "/Users/payletter/git-root/ReSet/output";
var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

var items = new List<(string Name, string Spec, SpDefinition Def)>();
int loadFail = 0;
foreach (var meta in Directory.EnumerateFiles(root, "metadata.json", SearchOption.AllDirectories)
                              .OrderBy(x => x, StringComparer.Ordinal))
{
    if (!meta.Replace('\\', '/').Contains("/raw/")) continue;
    var spec = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(meta)!, "..", "docs", "Spec.md"));
    if (!File.Exists(spec)) continue;
    try
    {
        var def = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(meta), opts)!;
        items.Add((Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(meta))!), spec, def));
    }
    catch { loadFail++; }
}

void Run(string label, Func<int, SpDefinition> pickExpectationsFor)
{
    var tally = new SortedDictionary<string, int>();
    int softPass = 0, nullExp = 0, fired = 0;
    var perObject = new List<(string Obj, string Type, string Msg)>();

    for (var i = 0; i < items.Count; i++)
    {
        var exp = SpecExpectations.From(pickExpectationsFor(i));
        if (exp == null) nullExp++;

        var markdown = File.ReadAllText(items[i].Spec);
        var result = new MechanicalValidator().Validate(markdown, exp);

        // catch-all 발동 서명: 소프트 페일 경로만 CleansedMarkdown 을 원문 참조 그대로 둔다.
        if (ReferenceEquals(result.CleansedMarkdown, markdown)) softPass++;
        if (result.DetailedErrors.Count > 0) fired++;

        foreach (var e in result.DetailedErrors)
        {
            var t = e.Type.ToString();
            tally[t] = tally.TryGetValue(t, out var n) ? n + 1 : 1;
            perObject.Add((items[i].Name, t, e.Message.Replace("\n", " ")));
        }
    }

    Console.WriteLine($"=== {label} · 쌍 {items.Count} · 로드실패 {loadFail} · nullExp {nullExp} "
                      + $"· catch-all발동 {softPass} · 발화편수 {fired}");
    Console.WriteLine($"    총 발화 {tally.Values.Sum()} · 종류 {tally.Count}");
    foreach (var kv in tally) Console.WriteLine($"      {kv.Key,-42} {kv.Value}");
    if (Environment.GetEnvironmentVariable("SWEEP_DETAIL") == "1")
    {
        foreach (var (o, t, m) in perObject.OrderBy(x => x.Type).ThenBy(x => x.Obj))
            Console.WriteLine($"      [{t}] {o}: {(m.Length > 130 ? m[..130] : m)}");
    }
}

// [정] 명세서 ↔ 자기 metadata. 배송본은 이미 L1 을 통과했으니 기대값은 0 이다.
Run("정  (자기 metadata)", i => items[i].Def);
Console.WriteLine();
// [교차] 명세서 ↔ 이웃 metadata. 양성 대조군 - 아래 「왜 교차 짝인가」를 보라.
Run("교차(이웃 metadata)", i => items[(i + 1) % items.Count].Def);
```

## ★ 왜 「교차 짝」이 필요한가 — 「차분 0」만으로는 거짓 초록이 된다

배송본은 **이미 L1 을 통과한 문서**라 정 짝(자기 metadata)의 기대값이 0 이다. 그래서
수정 전후 차분만 보면 **내 수정이 검사를 하나도 안 남기고 죽여도 0 → 0 이라 통과한다.**
「검사가 한 번도 안 돈다」는 이 문서가 첫 줄에서 경고한 바로 그 부류인데, 차분 판독이
그것에 눈이 먼다.

교차 짝은 명세서 `i` 를 **이웃 객체 `(i+1)` 의 metadata** 로 검증한다. 기대와 실물이
전면적으로 어긋나므로 검사 대부분이 발화하고, 그 수가 양성 대조군이 된다.

```
2026-09-10 실측 (코퍼스 31 편 · 캐시 세대 {21: 30, 19: 1})
  정  (자기 metadata)   0 발화 ·  0 종류 · 발화편수  0
  교차(이웃 metadata)   1915 발화 · 19 종류 · 발화편수 31
```

**BASE 와 HEAD 에서 이 수가 자릿수까지 같아야** 「검사를 하나도 안 죽였다」가 성립한다.
`MechanicalValidator` 의 검사 호출 44 자리를 `SafeCheck` 로 감싼 판(감사 [4])에서 이
대조군이 그 역할을 했다 — 판독은
`docs/audit-reports/2026-09-10-L1-예외격리-판독.md` §4.

**한계를 같이 적는다 — 교차 짝은 「배선」을 안 잰다.** 새 검사가 `Validate` 에서 아예
안 불려도 교차 발화 수는 **다른 검사들이 채운다.** 배선은 별도의 자로 재라(그 검사가
도는 자리 수를 직접 세는 시험). 그 모양은 `CheckGuardPolicyTests` 와 `CheckIsolationTests`
에 있다 — **검사를 하나 더하면 그 수가 함께 올라가야 하고, 안 올리면 빨개진다.**
(메서드 이름은 그 수를 담고 있어 검사가 늘 때마다 바뀐다. 클래스로 찾아라.)
두 자를 겹쳐 써야 「안 돌았다」와 「돌았는데 침묵했다」가 갈린다.

> 2026-09-10 에 이 자리를 실측으로 밟았다 — `ValidateConsolidated` 의 감싸기 아홉 자리를
> 통째로 걷어내도 **빨개지는 시험이 0** 이었다. 발화를 재는 자만 있고 배선을 재는 자가
> 없었기 때문이다.

`metadata.json`의 최상위 키가 `SpDefinition`과 그대로 맞으므로 직접 역직렬화된다.
BOM이 있으니 `PropertyNameCaseInsensitive`와 함께 `File.ReadAllText`로 읽는다.

## 차분으로 읽어라

절대 건수보다 **수정 전후 비교**가 판정을 만든다. 검사를 넣기 전 커밋과 넣은 뒤를
각각 돌려 어느 건이 새로 생겼는지 본다.

```bash
git archive <BASE_SHA> src/ReSet.Core | tar -x -C <임시경로>
```

읽는 법:

- 겨냥한 진짜 결함이 **0건**이면 검사가 자기 존재 이유를 놓친 것이다. 재료 필터가
  너무 좁거나(예: 이름만 보는 모호성 제거) `From`이 null을 돌려주는 경우다.
- 새로 생긴 건 중 **명세서가 실제로 틀리지 않은 것**은 전부 거짓 양성이다. 하나라도
  있으면 병합하지 마라 — 재생성 트리거라 재시도 소진으로 이어진다.
- **다른 검사 종류의 건수가 변했다면** 재료 확장이 옆 검사에 번진 것이다. 의도한 것인지
  확인하고, 아니면 좁혀라.
- **정 짝 차분이 `0 → 0` 이면 그것만으로는 아무것도 증명하지 않는다.** 위 「왜 교차
  짝인가」를 보라 — 검사를 전부 죽여도 같은 값이 나온다. 교차 짝 수를 함께 적어라.

## 보고에 적을 것

숫자로 적는다. "확인했다"는 근거가 아니다.

```
코퍼스 N쌍 · 로드 실패 0 · null expectations 0
  정  짝: 발화 X건 · 종류 Y
  교차 짝: 발화 X건 · 종류 Y   ← BASE와 자릿수까지 같은가 (양성 대조군)
  <새 검사>: 진짜 양성 X건(객체명 나열) · 거짓 양성 0건
  다른 검사 카운트: BASE와 동일 / 달라졌다면 무엇이 왜
  배선: 그 검사가 도는 자리 수를 세는 시험이 있는가 (교차 짝은 이것을 안 잰다)
```

## ★ 조용히 `0` 이 되는 자리 넷 — 전부 실측이다

정본은 이 문서의 `EnumerateFiles(root, "metadata.json", SearchOption.AllDirectories)` 다.
아래 넷은 2026-09-10 에 세 세션이 밟았고 **전부 stdout 에 `0`(또는 빈 줄)을 찍는다.**
공통 모양은 하나다 — **오류가 stderr 로만 가고, 부재를 관측한 자리와 결론이 사는 자리가
다르다.** 그래서 「없다」로 읽힌다.

**(1) 얕은 글롭이 External 을 못 본다.**
```
ls   output/*/*/docs/Spec.md                       →  24 편
find output -name Spec.md -not -path "*/logs/*"    →  31 편
```
빠지는 일곱은 `output/External/<db>/Functions/<객체>/docs/Spec.md` 로 마디가 둘 더
깊다. 코퍼스의 **23%** 이고, 그 침묵은 「해당 없음 0 건」과 구분되지 않는다.

**(2) zsh 는 따옴표 없는 변수를 단어 분할하지 않는다.**
```zsh
SPECS=$(find output -name Spec.md -not -path "*/logs/*")   # 31 줄
grep -l "…" $SPECS | wc -l
#   stdout → 0
#   stderr → warning: output/…/Spec.md
output/…/Spec.md  (경로 31 개가 파일명 하나)
```
경로가 개행으로 이어 붙은 **파일명 하나**가 되어 아무것도 못 읽는다. 경고는 stderr 로만
나가므로 stdout 의 `0` 을 그대로 읽으면 「없다」가 된다 — 부재를 관측한 자리와 결론이
사는 자리가 다른 그 모양이다.

**답**: `find … -print0 | xargs -0 grep -l …` (같은 조건에서 8 편이 나온다.)

**(3) `find` 는 심링크인 시작 디렉터리를 안 따라가고도 종료 코드 `0` 을 낸다.**

워크트리는 `output/` 을 메인 저장소로 심링크한다. 그 상태에서:

```
$ find output -name Spec.md      →  0 편   (종료 코드 0)
$ find -L output -name Spec.md   → 31 편
$ find output/ -name Spec.md     → 31 편   ← 뒤 슬래시만 붙여도 된다
```

BSD `find`(이 저장소의 개발 플랫폼, `Darwin`)의 기본 동작이다. **진짜 데이터가 있어도
「0 편」이 성공으로 찍힌다** — `-L` 도 슬래시도 없이 쓰면 워크트리에서만 조용히 0 이 되고,
메인 저장소에서 돌릴 때는 멀쩡하므로 재현이 안 된다고 착각하기 쉽다.

`scripts/promote-l1-attempts.sh` 가 이 자리에서 「승격한 객체 0 개」를 조용히 성공으로
찍었고(`f7657844` 에서 고침), `PromoteL1AttemptsScriptTests` 가 그 자리를 잠근다.

**답**: `find -L <심링크일 수 있는 경로>` 또는 경로 뒤에 `/`.

**(4) zsh 는 `$var:...` 의 `:` 뒤 글자를 **수정자**로 먹는다.**

```zsh
$ r=main
$ echo $r:src/ReSet.Core/Services/CacheManager.cs
mainvices/CacheManager.cs          ← :s/ReSet.Core/Ser 치환이 돌았다
$ echo ${r}:src/ReSet.Core/Services/CacheManager.cs
main:src/ReSet.Core/Services/CacheManager.cs

$ p=docs/x; echo $p:h
docs                               ← :s 만이 아니다. :h(head) 도 :t 도 먹는다

$ bash -c 'r=main; echo $r:src/…'
main:src/…                         ← bash 는 안 걸린다. zsh 만이다
```

`git show "$rev:path"` 처럼 **`:` 로 리비전과 경로를 잇는 모양이 전부 이 자리**다.
그리고 `git show` 는 오류를 **stderr 로만** 낸다:

```
out=$(git show "$r:src/…/CacheManager.cs" 2>/dev/null | grep -o "…")
#   out=[]        ← 빈 줄
#   중괄호로 고치면 out=[CurrentCacheFormatVersion = 21]
```

2026-09-10 에 브랜치 셋의 캐시 버전을 대조하다 밟았다. **빈 줄을 그대로 읽었으면
「두 브랜치 다 값이 없다」가 됐을 것이다** — (3) 과 같은 가족이고, `2>/dev/null` 이
그 침묵을 완성한다.

**답**: `"${var}:path"`. 그리고 **`2>/dev/null` 을 습관으로 붙이지 마라** — 부재를
증거로 쓸 자리에서는 stderr 를 봐야 한다.

**그리고 이 창에서 잰 것 전부를 다시 재라.** 자가 틀린 것을 발견하면 촉발한 수만
고치고 끝내지 마라 — 2026-09-10 에 한 세션이 「배송본 31 편으로 실측」이라 적은 값이
실제로는 24 편이었고, 되재고 나서야 유출 편수가 7 → 8 로 바뀌었다(나머지 둘은 불변).
