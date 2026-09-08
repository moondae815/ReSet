# OpenRouter 라우팅 — 이름으로 고르던 것을 성질로 고른다

`appsettings.json`의 OpenRouter 구획이 약 120줄의 주석과 6항목짜리 손표로 불어났고,
그 표가 저장소 다섯 곳에 복제돼 있다. 이 문서는 그 표를 **한 줄로 줄이고**, 모델을
바꿀 때 아무 데도 안 적어도 되게 만든다.

근거는 전부 2026-09-08 실측이다. 문서를 읽고 세운 가설 하나가 실측에서 기각됐고,
그 기각이 이 설계의 모양을 정했다.

## 1. 관측

### 1-1. 표가 다섯 곳에 있다

| 자리 | 무엇 |
|---|---|
| `src/ReSet.Cli/appsettings.json:115-207` | `Routing:Default` + `ByModel` 6항목 + 주석 |
| `src/ReSet.Validator.Cli/appsettings.json` | 위와 통째로 같은 사본 (손으로 맞춘 것) |
| `README.md:298-318` | 같은 표의 축약본 |
| `tests/ReSet.Core.Tests/CliProviderSettingsTests.cs:101` | `PinnedBackendsPerModel` — 같은 표를 다시 적은 것 |
| `docs/architecture/4.5-multi-llm-provider.md:11` | 같은 근거 서사 |

모델 하나를 바꾸면 손댈 자리가 다섯이다. 사용자가 "설정이 맞지 않는다"고 말한 것의
정체가 이것이다.

### 1-2. `ByModel`에 없는 모델은 죽는다

현행 `Default`는 `Order: ["streamlake","novita"]` + `AllowFallbacks: false`다.
`ModelName`을 표에 없는 모델로 바꾸면 그 둘이 그 모델을 서빙하지 않아
404 `No endpoints found`로 즉시 실패한다. **백엔드 고정은 최적화인데 지금은 필수
조건처럼 동작한다.**

### 1-3. 표가 이미 두 자리에서 썩었다

실측으로 확인한 것이다. 주석에 적힌 근거는 2026-08-28 조회값인데 오늘과 다르다.

- **`z-ai/glm-5.3`의 주석은 "서빙하는 곳이 Z.AI 본사 하나뿐"이라고 적혀 있다.**
  오늘 `/endpoints` 응답은 **28곳**(fp8 12곳)이다. 그리고 못박힌 `z-ai`는 fp8 중
  **캐시읽기 최고가 티어**(0.2600 $/M)다.
- **`z-ai/glm-5.2`의 1순위 `sail-research`는 이 모델을 더 이상 서빙하지 않는다.**
  2순위 `novita`가 받고 있어 조용히 돌 뿐이다.
- **`deepseek/deepseek-v4-pro-0813`의 2순위 `deepseek`는 이 계정에서 도달 불가다.**
  `Filter by Guardrails`가 잘라낸다(직접 호출 404 확인, 1순위 `gmicloud`는 200).
  1순위가 막히면 `AllowFallbacks: false`와 맞물려 **Critic이 404로 죽는다** —
  주석이 경계하던 바로 그 사고 모양이 설정 안에 이미 들어 있었다.

## 2. 실측 — 실험 넷

`routing_funnel`을 계량기로 썼다. 도달 불가능한 `order`로 일부러 404를 내면 응답
`metadata.routing_funnel`에 **필터 단계별 잔존 엔드포인트 수**가 실려 온다. 이것이
이 측정의 오라클이며, 설정 파일이 아니라 OpenRouter가 채우므로 비순환이다.

### ① `provider.quantizations` — 채택

`Filter by Quantization: 29 → 13`. 13은 `/endpoints`의 fp8 개수와 정확히 일치한다.
unknown·fp4를 전부 배제하며, **이름으로 지목해도 필터가 이긴다**
(`order:["fireworks"]`(unknown) + `quantizations:["fp8"]` → 404).

즉 표의 존재 이유 절반이었던 "fp8인 곳을 골라 적는다"는 **정책 한 줄로 대체된다.**

### ② `provider.sort` 표기 — 둘 다 유효

객체형 `{"by":"price"}`·문자열형 `"price"` 모두 200. 오타 `"bogus"`는
400 `provider.sort: Invalid input` — 음성 대조가 발화하므로 이 판정은 믿을 수 있다.

### ③ `sort`가 `order`를 대체하는가 — **기각**

공식 문서(`guides/routing/auto-exacto.mdx`)는 `sort: "price"`가 Auto Exacto를 끄고
**sticky routing을 유지한다**고 적는다. 이 계정·이 모델에서는 성립하지 않았다.

`z-ai/glm-5.2`, 약 8,100토큰 접두사, 팔마다 접두사를 갈라 서로 캐시를 데워 주지 못하게 함:

| 팔 | 설정 | 고유 공급자 | 적중 | 호출당 비용 |
|---|---|---|---|---|
| A | provider 없음 | 4회에 3종 | 1/4 | — |
| B′ | `fp8 + sort:"price"` | 8회에 4종 | 5/8 | $0.00386 |
| D′ | `order:["novita"] + fp8` | 6회에 **1종** | 4/6 | **$0.00258 (−33%)** |

`sort`는 후보를 좁히지만 **고정하지 않는다.** → `Order`는 남는다.

### ④ `require_parameters` — 좁힘 0

`reasoning: {effort}` 동반 시 잰 5개 모델(`deepseek-v4-flash-0731`·
`deepseek-v4-pro-0813`·`z-ai/glm-5.2`·`z-ai/glm-5.3`·`tencent/hy4-preview`)
전부에서 배제 0. 모든 백엔드가 `reasoning`을 지원한다.

**계량기 검증(없앴을 때로 판정)**: 같은 계량기에 `logprobs`를 넣으면
`Filter by Parameters: 29 → 16`이 뜬다(16 = `/endpoints`의 logprobs 지원 수).
죽은 계량기가 아니므로 위의 0은 진짜 0이다.

→ 현행 `RequireParameters: null`을 그대로 둔다.

### ⑤ 덤 — 재는 김에 나온 것

- **`supports_implicit_caching: false`인데 캐시가 걸린다.** `z-ai/glm-5.2`의 33곳
  전부 이 값이 false이고 `input_cache_write` 단가는 0곳인데, `cache_control` 없이도
  적중이 났다($0.0797 → $0.0149, −81%). **`/endpoints` 메타데이터를 캐시 가능성의
  오라클로 쓰면 안 된다.**
- **`StreamLake`가 처음 보는 접두사에 적중 6,002·6,012를 보고했다**(서로 다른 접두사
  2회). 적중 수치를 그대로 믿으면 안 되는 백엔드가 있다.
- **워밍업 회차는 일정하지 않다.** `glm-5.2` 두 계열은 3번째 호출부터, `glm-5.3` 두
  계열은 2번째 호출부터 적중했다. "N회 데워야 한다"는 일반화는 성립하지 않으며,
  `StepConcurrency`의 "첫 단계 단독 실행" 전제를 이 근거로 의심해서는 안 된다.

## 3. 설계 — 역할을 셋으로 가른다

깔때기가 답을 줬다. 필터 순서가 `Quantization → … → Fallback`이므로,
**양자화로 좁힌 뒤의 폴백은 이미 fp8 안에서만 일어난다.**

| 항목 | 역할 | 근거 |
|---|---|---|
| `Quantizations` | **품질 하한.** 이름이 아니라 성질로 | ① 29→13 |
| `Order` | **캐시 고착성.** 이것만이 그것을 산다 | ③ −33% |
| `AllowFallbacks: true` | **죽지 않기.** 밖이 이미 fp8뿐이므로 열 수 있다 | ① + 깔때기 순서 |

`AllowFallbacks`를 여는 것이 1-2를 닫는다. `ByModel`에 항목이 없는 모델은 죽지 않고
**캐시 고정만 잃은 채 돈다.** 표가 필수에서 최적화로 강등된다.

## 4. 최종 설정

두 `appsettings.json`이 같은 값을 갖는다.

```jsonc
"OpenRouter": {
  "ApiKey": "", "Endpoint": "https://openrouter.ai/api/v1", "NumCtx": null,
  // 백엔드 라우팅의 근거와 갱신 방법: docs/architecture/4.5-multi-llm-provider.md
  "Routing": {
    "Default": {
      "Quantizations": [ "fp8" ],
      "AllowFallbacks": true
    },
    "ByModel": {
      "z-ai/glm-5.3": { "Order": [ "gmicloud/fp8", "baseten/fp8" ] }
    }
  }
}
```

**주석 약 120줄 → 1줄. `ByModel` 6항목 → 1항목.**

### 4-1. 로스터를 둘로 줄인다 (사람 결정, 2026-09-08)

남기는 것은 `tencent/hy4-preview`(Actor)와 `z-ai/glm-5.3`(Critic)뿐이다.
지우는 것: `z-ai/glm-5.2` · `z-ai/glm-5.3-flash` · `deepseek/deepseek-v4-pro-0813` ·
`deepseek/deepseek-v4-flash-0731`.

### 4-2. `hy4-preview`는 항목을 갖지 않는다

`/endpoints` 응답이 **1건**(`tencent/fp8`)이다. 백엔드가 하나면 `Order`가 살 고착성이
없다. 실측: 새 `Default`만으로 Tencent 2/2로 붙는다. 이 항목이 필요했던 유일한 이유는
"`Default:Order`가 이 모델을 안 서빙해서 404"였고, `Default:Order`를 없애면 사라진다.

### 4-3. `glm-5.3`의 `Order` 근거

fp8 · ctx 1,048,576 · 캐시읽기 단가 순 (실측 2026-09-08):

| tag | 캐시읽기 $/M | 30분 | 1일 |
|---|---|---|---|
| `gmicloud/fp8` | **0.2080** | 99.26 | 98.00 |
| `baseten/fp8` | 0.2100 | **100.00** | **99.95** |
| `akashml/fp8` | 0.2340 | 100.00 | 99.34 |
| `z-ai/fp8` *(현행)* | 0.2600 | 99.88 | 99.79 |

`reka`·`io-net`·`atlas-cloud`는 값이 비슷해도 ctx가 262,144라 뺐다 — 2순위로 떨어지는
순간 받을 수 있는 SP 크기가 같이 준다.

1순위를 `gmicloud`로, 2순위를 `baseten`으로 둔 것은 1순위가 최저가이고 2순위가
1%만 비싼 대신 가동률이 가장 높기 때문이다. 실측 대조(4K 접두사, 3회):

| | 공급자 | 적중 | 3회 비용 |
|---|---|---|---|
| 제안 `[gmicloud/fp8, baseten/fp8]` | GMICloud 3/3 | `[0, 3200, 3200]` | **$0.0053** |
| 현행 `[z-ai]` | Z.AI 3/3 | `[0, 3200, 3200]` | $0.0065 |

제안이 현행보다 **18.5% 싸다**(뒤집어 말하면 현행이 22.6% 비싸다). 캐시읽기 단가
차 −20%가 그대로 나온 값이다.

슬러그는 `/endpoints`의 `tag`(변형 접미사 포함)를 쓴다. base 슬러그도 유효하지만
(실측: `gmicloud`·`gmicloud/fp8` 둘 다 GMICloud로 붙음) 그 제공자의 **모든** 변형에
걸리므로, 어느 변형인지 눈에 보이는 `tag` 형식을 쓴다.

## 5. 코드 변경

| 파일 | 무엇 |
|---|---|
| `ReSet.Core/Services/Clients/OpenRouterRoutingOptions.cs` | `Quantizations` 추가 — `Parse`·`Merge`(항목 단위)·`IsEmpty` |
| `ReSet.Core/Services/Clients/OpenRouterClient.cs` | 요청 `provider`에 `quantizations` 실음 |
| `ReSet.Cli/Program.cs` · `ReSet.Validator.Cli/Program.cs` | `ParseRoutingBlock`이 배열을 읽음 |

`Merge`는 기존 규칙 그대로 항목 단위다 — `ByModel`이 `Order`만 적어도 `Default`의
`Quantizations`·`AllowFallbacks`가 살아남아야 한다. 통째 대체로 바꾸면 모델별 호출에서만
양자화 하한이 조용히 사라진다.

## 6. 테스트 오라클 재설계

**`PinnedBackendsPerModel`(6행 손표)을 폐기한다.** 그것은 설정을 설정의 사본으로
확인하는 절반 순환이고, 모델이 바뀔 때 손댈 다섯째 자리다.

대신 셋:

1. **단위** — `Quantizations`의 `Parse`/`Merge`/요청 직렬화. 오라클은 OpenRouter
   OpenAPI 스키마와 실물 응답이며 설정 파일이 아니다.
2. **모양 불변식** — `Default`가 `Quantizations`를 선언한다 · `Default`가 **`Order`를
   갖지 않는다**(죽은 이름을 남의 모델에 물려주던 자리) · `AllowFallbacks`가 켜져 있다.
3. **역방향** — `ByModel`의 키가 설정이 실제로 참조하는 모델(`ModelName`,
   `Critic:ModelName`, `Consolidator:ModelName`)의 부분집합이다. 낡은 항목이 자동으로
   걸리므로 4-1 같은 정리를 사람이 기억할 필요가 없다.

이러면 **모델을 바꿔도 테스트를 손대지 않는다.** 지금은 손대야 한다.

폐기로 잃는 것을 명시한다: 손표는 "이 백엔드가 좋은 선택인가"를 확인해 주지 않았다
(값을 다시 적었을 뿐이다). 실제로 그 표는 1-3의 썩은 자리 셋을 **하나도 잡지 못했다.**
잃는 것은 없다.

## 7. 기각한 안 넷

- **`sort:"price"`로 `order`를 대체** — ③에서 기각. 문서의 주장이 이 계정에서
  재현되지 않았다. `sort`는 좁히되 고정하지 않는다.
- **`max_price`로 캐시읽기 단가를 겨냥** — OpenAPI 스키마상 `max_price` 키는
  `prompt`·`completion`·`request`·`image`·`audio`뿐이고 `input_cache_read`는 없다.
  이 프로젝트의 정상 상태 비용을 지배하는 축을 선언적으로 겁 수 없다.
- **정책만 적고 `reset openrouter refresh`가 목록을 생성** — 표가 한 줄로 줄었고,
  `Quantizations` + `AllowFallbacks: true` 덕에 모델 교체 시 설정을 안 고쳐도 돈다.
  명령을 만들 값어치가 사라졌다(YAGNI). 조회 방법과 선정 기준은 8절대로 문서에 적는다.
  로스터가 다시 늘면 그때 만든다.
- **`Unlisted` 같은 미등록 모델 특수 규칙** — `AllowFallbacks: true`가 같은 일을
  구조적으로 한다. 규칙을 더할 이유가 없다.

## 8. 문서 이전

`appsettings.json`에서 빠지는 근거 서사는 `docs/architecture/4.5-multi-llm-provider.md`로
간다. 그곳에 이미 같은 내용의 사본이 있으므로 **새 문서를 만들지 않고 그 문단을
재작성한다.** 담을 것:

- 왜 고정하는가 (③의 −33%)
- `Quantizations`가 무엇을 대체했는가 (①)
- 후보 조회: `curl https://openrouter.ai/api/v1/models/<author>/<slug>/endpoints`
  (인증 불필요). 고를 때 보는 칸은 `tag`·`quantization`·`pricing.input_cache_read`·
  `context_length`·`uptime_last_30m`이며, **정상 상태 비용은 `prompt`가 아니라
  `input_cache_read`가 지배한다.**
- `routing_funnel` 판독법 — 도달 불가능한 `order`로 404를 내면 필터별 잔존 수가 보인다.
- ⑤의 함정 둘(메타데이터가 캐시 가능성을 말하지 않는다 · 적중 보고를 믿을 수 없는
  백엔드가 있다).

`README.md:298-318`의 표는 4절 블록으로 교체한다.

## 9. 검증 계획

1. `dotnet test` — 실패 0 · 건너뜀 0 · 경고 0. (코퍼스 심링크 `output/` 확인)
2. **되돌림 검증**: `Quantizations` 전달을 끄면 6절 ①의 단위 검사가 실제로 실패하는가.
   끄고 초록이면 그 검사는 아무것도 안 지키고 있다.
3. **실호출 1건** — `z-ai/glm-5.3`을 새 설정으로 불러 응답 `provider`가
   `GMICloud`인지 확인. 설정이 요청에 실제로 실리는지는 요청 본문 로그가 아니라
   응답 공급자로 판정한다.
4. **미등록 모델 회귀** — `ByModel`에 없는 모델(예 `z-ai/glm-5.2`)을 불러
   404가 아니라 200이 나는지 확인. 1-2를 닫았다는 증거는 이것뿐이다.

## 10. 되돌림 조건

- 3의 실호출이 `GMICloud`가 아닌 곳으로 가면 `Order` 형식(`tag` vs base 슬러그)을
  의심하고 base 슬러그로 되돌린다.
- `AllowFallbacks: true`로 열었는데 fp4·unknown 백엔드가 응답 `provider`에 나타나면
  ①의 전제(폴백이 양자화 필터 뒤에 온다)가 깨진 것이다. 그때는 `false`로 되돌리고
  `ByModel`을 다시 채운다.

## 11. 이 설계가 하지 않는 것

- **`appsettings.json`의 나머지 긴 주석**(`MaxL2Attempts`·`PromptContextScope`·
  `agy-cli` 금지 등)은 건드리지 않는다. 같은 원칙으로 정리할 수 있지만 옮길 자리를
  먼저 만들어야 하고, OpenRouter 구획만으로 파일이 289줄에서 약 160줄이 된다.
- **두 `Program.cs`의 `ReadOpenRouterRouting` 중복 제거**는 하지 않는다.
  `ReSet.Core`에 설정 패키지 의존을 더해야 하는데, 그 회피는 의도된 것이라고
  `OpenRouterRoutingOptions.cs`에 적혀 있다. 별건으로 남긴다.
- **`StepConcurrency`의 캐시 워밍 전제**는 건드리지 않는다. ⑤에서 워밍 회차가
  일정하지 않다는 것만 확인했을 뿐, 그 설계가 틀렸다는 증거는 없다.
