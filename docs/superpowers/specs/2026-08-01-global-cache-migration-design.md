# Global Cache Migration & Deduplication Design

## 1. 개요 (Overview)
ReSet 프로젝트의 재귀(Recursive) 분석 기능 수행 시, 동일한 UDF(혹은 하위 SP)를 참조하는 서로 다른 루트 SP를 각각 분석할 때마다 UDF가 재분석되는 문제를 해결하기 위한 구조 개선 설계서입니다.

## 2. 문제 원인 (Problem)
현재 아키텍처는 루트 SP 단위로 완전히 격리된 출력 폴더(예: `output/dbo.RootSP_1`, `output/dbo.RootSP_2`)를 생성합니다. `CacheManager`는 해당 격리 폴더 내의 `.sp_cache_index.json`만을 참조하므로, `RootSP_2`를 분석할 때 `RootSP_1`에서 이미 분석한 하위 모듈의 캐시 인덱스를 찾지 못하고 다시 AI를 호출합니다.

## 3. 해결 방안 (Solution Approach)
**"글로벌 캐시(Global Cache) 도입 및 파일 복사(File Copy) 기반 재사용 + 기존 데이터 자동 마이그레이션"** (선택된 1안)

격리된 출력 폴더의 장점(독립 번들 생성 용이성)은 그대로 유지하면서, AI 호출 횟수와 실행 시간을 획기적으로 단축합니다.

### 3.1. 캐시 모델(CacheEntry) 확장
기존 캐시 데이터 구조에 원본 파일이 생성된 경로를 추적하기 위한 필드를 추가합니다.
- `CacheEntry.OriginalSpecPath` 필드 (또는 상대/절대 경로 추적을 위한 유사 필드) 추가.

### 3.2. CacheManager 글로벌화 (Global Cache)
- `CacheManager`가 인덱스를 읽고 쓸 때, 기존 `outputPaths.OutputRoot` 대신 최상위 폴더(예: `output/.sp_cache_index.json` 혹은 `.reset/global_cache.json`)를 바라보도록 변경합니다.
- 특정 코드 객체(Key)와 해시(CompositeHash)가 일치하는 글로벌 캐시 엔트리가 존재하면, 해당 엔트리의 `OriginalSpecPath`에 위치한 `Spec.md` 문서를 현재 분석 중인 루트 SP의 출력 폴더(`outputPaths.OutputRoot`)의 적절한 위치로 **복사(Copy)**합니다.
- 복사 성공 시 AI 파이프라인 수행을 건너뜁니다 (Cache Hit).

### 3.3. 기존 데이터 자동 마이그레이션 (Auto-Migration)
이미 각 격리 폴더에 생성되어 있는 과거의 산출물도 100% 재사용할 수 있도록, 초기화 시점에 흩어진 캐시를 모아 글로벌 캐시로 병합하는 로직을 추가합니다.
1. `CacheManager` 초기화 시 또는 마이그레이션 커맨드 실행 시, 하위 디렉터리(`output/*/.sp_cache_index.json`)를 스캔합니다.
2. 발견된 로컬 캐시 엔트리들을 글로벌 캐시에 엎어칩니다. (이 때, 해당 폴더 경로를 기반으로 `OriginalSpecPath`를 역산출하여 갱신합니다.)

## 4. 고려 사항 (Trade-offs & Constraints)
- **파일 중복**: 디스크에 동일한 내용의 `Spec.md` 파일이 여러 번 복사되어 저장되지만 텍스트 파일이므로 용량 부담은 미미합니다.
- **원본 손실 방지**: 캐시 엔트리가 가리키는 `OriginalSpecPath` 원본 파일이 사용자에 의해 삭제된 경우 파일 복사에 실패하게 됩니다. 이 경우 안전한 Soft Fail 처리를 통해 **다시 AI를 호출하여 생성(Cache Miss 처리)**하도록 예외 처리를 꼼꼼히 구성해야 합니다.
