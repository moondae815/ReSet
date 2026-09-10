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

`Program.cs`:

```csharp
using System.Text.Json;
using ReSet.Core.Models;
using ReSet.Core.Services;

var root = args.Length > 0 ? args[0] : "/Users/payletter/git-root/ReSet/output";
var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var tally = new SortedDictionary<string, int>();
var perObject = new List<(string Obj, string Type, string Msg)>();
int pairs = 0, loadFail = 0, nullExp = 0;

foreach (var meta in Directory.EnumerateFiles(root, "metadata.json", SearchOption.AllDirectories))
{
    if (!meta.Replace('\\','/').Contains("/raw/")) continue;
    var spec = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(meta)!, "..", "docs", "Spec.md"));
    if (!File.Exists(spec)) continue;
    pairs++;

    SpDefinition def;
    try { def = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(meta), opts)!; }
    catch { loadFail++; continue; }

    var exp = SpecExpectations.From(def);
    if (exp == null) nullExp++;

    var result = new MechanicalValidator().Validate(File.ReadAllText(spec), exp);
    var name = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(meta))!);
    foreach (var e in result.DetailedErrors)
    {
        var t = e.Type.ToString();
        tally[t] = tally.TryGetValue(t, out var n) ? n + 1 : 1;
        perObject.Add((name, t, e.Message.Replace("\n", " ")));
    }
}

Console.WriteLine($"쌍 {pairs} · 로드 실패 {loadFail} · null expectations {nullExp}");
Console.WriteLine("--- ErrorType 집계 ---");
foreach (var kv in tally) Console.WriteLine($"  {kv.Key,-38} {kv.Value}");
Console.WriteLine("--- 건별 ---");
foreach (var (o, t, m) in perObject.OrderBy(x => x.Type).ThenBy(x => x.Obj))
    Console.WriteLine($"  [{t}] {o}: {(m.Length > 130 ? m[..130] : m)}");
```

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

## 보고에 적을 것

숫자로 적는다. "확인했다"는 근거가 아니다.

```
코퍼스 N쌍 · 로드 실패 0 · null expectations 0
  <새 검사>: 진짜 양성 X건(객체명 나열) · 거짓 양성 0건
  다른 검사 카운트: BASE와 동일 / 달라졌다면 무엇이 왜
```

## ★ 손으로 글롭을 치지 마라 — 두 가지로 조용히 0 이 된다

정본은 이 문서의 `EnumerateFiles(root, "metadata.json", SearchOption.AllDirectories)` 다.
아래 둘은 2026-09-10 에 두 세션이 각각 밟았고 **둘 다 stdout 에 `0` 을 찍는다.**

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

**그리고 이 창에서 잰 것 전부를 다시 재라.** 자가 틀린 것을 발견하면 촉발한 수만
고치고 끝내지 마라 — 2026-09-10 에 한 세션이 「배송본 31 편으로 실측」이라 적은 값이
실제로는 24 편이었고, 되재고 나서야 유출 편수가 7 → 8 로 바뀌었다(나머지 둘은 불변).
