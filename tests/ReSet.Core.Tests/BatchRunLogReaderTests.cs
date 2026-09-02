using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchRunLogReaderTests
    {
        // POQSettleBatch4(2026-08-29) 실측 로그의 형태를 그대로 축약한 픽스처.
        // 회차별 점수 78 -> 76 -> (L1 실패) -> (L1 실패) -> 84 -> 74 를 재현한다.
        //
        // [2026-08-30 수정 - Fix Round 1] 실제 로그는 앵커 바로 뒤에 "리뷰 응답 수신
        // 완료" 앵커와 다른 타임스탬프를 가진 `[DBG] [AI 응답 내용]:` 줄이 낀 뒤에야
        // JSON 본문이 시작하고, 같은 점수 블록이 `[추출된 JSON 내용]`으로 한 번 더
        // 실린다(회차당 두 번). 이전 픽스처는 이 형태를 생략해서, 9개 테스트가 전부
        // 통과하는 동안 실물 로그에서는 리더가 0% 작동했다 - 리뷰가 이 사고를 잡았다.
        private const string Batch4Shape = """
            2026-08-29 14:15:34.321 +09:00 [INF] POQSettleBatch4 - AI 통합 배치 전환 계획 수립 중 [[1차 분석]]...
            2026-08-29 14:17:26.447 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 238908, 캐시 읽기: 0, 출력: 7544, 추론: 미보고
            2026-08-29 14:50:19.405 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 2802
            2026-08-29 14:50:19.406 +09:00 [DBG] [AI 응답 내용]:
            {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S02"],
              "ScoreAccuracy": 6,
              "ScoreCrud": 9,
              "ScoreInterface": 7,
              "ScoreException": 7,
              "ScoreReadability": 10
            }
            2026-08-29 14:50:19.410 +09:00 [DBG] [추출된 JSON 내용]: {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S02"],
              "ScoreAccuracy": 6,
              "ScoreCrud": 9,
              "ScoreInterface": 7,
              "ScoreException": 7,
              "ScoreReadability": 10
            }
            2026-08-29 14:50:19.552 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 1/6): 결함
            2026-08-29 14:51:46.828 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 251376, 캐시 읽기: 8518, 출력: 11160, 추론: 미보고
            2026-08-29 15:06:38.299 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 1482
            2026-08-29 15:06:38.299 +09:00 [DBG] [AI 응답 내용]:
            ```json
            {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S06"],
              "ScoreAccuracy": 6,
              "ScoreCrud": 8,
              "ScoreInterface": 6,
              "ScoreException": 9,
              "ScoreReadability": 9
            }
            ```
            2026-08-29 15:06:38.299 +09:00 [DBG] [추출된 JSON 내용]: {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S06"],
              "ScoreAccuracy": 6,
              "ScoreCrud": 8,
              "ScoreInterface": 6,
              "ScoreException": 9,
              "ScoreReadability": 9
            }
            2026-08-29 15:06:38.490 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 2/6): 결함
            2026-08-29 15:06:38.491 +09:00 [INF] POQSettleBatch4 - 재시도가 점수를 개선하지 못해 목차를 다시 설계합니다...
            2026-08-29 15:30:27.712 +09:00 [WRN] [POQSettleBatch4] L1 기계 검증 오류 발견 (시도 3/6):
            2026-08-29 15:51:52.668 +09:00 [WRN] [POQSettleBatch4] L1 기계 검증 오류 발견 (시도 4/6):
            2026-08-29 16:24:53.100 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 3000
            2026-08-29 16:24:53.101 +09:00 [DBG] [AI 응답 내용]:
            {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S09"],
              "ScoreAccuracy": 8,
              "ScoreCrud": 9,
              "ScoreInterface": 9,
              "ScoreException": 7,
              "ScoreReadability": 9
            }
            2026-08-29 16:24:53.105 +09:00 [DBG] [추출된 JSON 내용]: {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S09"],
              "ScoreAccuracy": 8,
              "ScoreCrud": 9,
              "ScoreInterface": 9,
              "ScoreException": 7,
              "ScoreReadability": 9
            }
            2026-08-29 16:24:53.134 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 5/6): 결함
            2026-08-29 16:38:45.600 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 2500
            2026-08-29 16:38:45.601 +09:00 [DBG] [AI 응답 내용]:
            {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S11"],
              "ScoreAccuracy": 7,
              "ScoreCrud": 8,
              "ScoreInterface": 7,
              "ScoreException": 6,
              "ScoreReadability": 9
            }
            2026-08-29 16:38:45.605 +09:00 [DBG] [추출된 JSON 내용]: {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S11"],
              "ScoreAccuracy": 7,
              "ScoreCrud": 8,
              "ScoreInterface": 7,
              "ScoreException": 6,
              "ScoreReadability": 9
            }
            2026-08-29 16:38:45.609 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 6/6): 결함
            2026-08-29 16:39:00.199 +09:00 [INF] POQSettleBatch4 - 배치 모드로 인해 통합 계획서가 자동으로 최종 승인되었습니다.
            """;

        [Fact]
        public void Read_ExtractsTrajectoryInOrder()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            Assert.Equal(new[] { 78, 76, 84, 74 },
                metrics.Trajectory.Select(a => a.NormalizedScore).ToArray());
        }

        // 이 계획 전체가 이 값을 0으로 만드는 것을 목표로 한다.
        [Fact]
        public void Read_CountsMonotonicityViolations()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            // 76 < 78, 74 < 84. 84 > 78 은 갱신이므로 위반이 아니다.
            Assert.Equal(2, metrics.MonotonicityViolations);
        }

        // [Fix Round 3 - Important 4] 이름과 뜻이 바뀌었다. "L1 소진 회차"는 로그에
        // "L1 기계 검증 오류 발견" 줄이 몇 번 찍혔는지가 아니라 "채점을 받지 못한
        // 회차가 몇 개인가"다. §3-3 이후로는 L1 위반이 귀속·수리되면 같은 줄이
        // 찍히면서도 채점 예산을 먹지 않는다(Read_AttributedAndRepairedL1Violation_
        // DoesNotCountAsUnscored가 그 경우를 고정한다) - 그래서 줄 수를 세면 안 되고,
        // TotalAttempts - Trajectory.Count(채점된 회차 수)를 써야 한다. Batch4
        // 기준선은 두 계산이 우연히 같은 값(2)을 내는데, 그 규칙이 바뀌던 시절
        // (L1 실패 = 곧 전량 재생성 = 곧 회차 소모)의 유물일 뿐이다.
        [Fact]
        public void Read_CountsUnscoredAttempts()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            Assert.Equal(2, metrics.UnscoredAttempts);
            Assert.Equal(6, metrics.TotalAttempts);
        }

        // Important 4가 지목한 회귀 시나리오: §3-3 이후 L1 위반이 귀속·수리되면
        // "L1 기계 검증 오류 발견 (시도 N/M)" 줄이 찍히지만, 같은 회차가 결국
        // 채점까지 도달한다. 줄 수로 세면(옛 규칙) 이 경우도 1회 소진으로 잘못
        // 세지만, 새 규칙(TotalAttempts - Trajectory.Count)은 회차가 실제로
        // 채점됐으므로 0을 낸다.
        [Fact]
        public void Read_AttributedAndRepairedL1Violation_DoesNotCountAsUnscored()
        {
            var log = """
                2026-08-29 10:00:00.000 +09:00 [WRN] [Job] L1 기계 검증 오류 발견 (시도 1/6):
                2026-08-29 10:01:00.000 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료
                2026-08-29 10:01:00.001 +09:00 [DBG] [AI 응답 내용]:
                {
                  "ScoreAccuracy": 8,
                  "ScoreCrud": 8,
                  "ScoreInterface": 8,
                  "ScoreException": 8,
                  "ScoreReadability": 8
                }
                """;

            var metrics = BatchRunLogReader.Read(log);

            Assert.Equal(1, metrics.TotalAttempts);
            Assert.Single(metrics.Trajectory);
            Assert.Equal(0, metrics.UnscoredAttempts);
        }

        // 조용한 0. `MaxL2Attempts: "unlimited"`이면 로그가 "(시도 3/검증 완료까지)"를
        // 찍는다(ConsoleUserInteraction.cs:97). 분모가 숫자가 아니라고 회차 번호(분자)
        // 파싱까지 통째로 실패하면 TotalAttempts가 조용히 0이 되어 "성공"처럼 읽힌다 -
        // "못 읽었다"와 "0이다"는 다른 사실이어야 한다.
        [Fact]
        public void Read_UnlimitedMaxAttempts_StillParsesAttemptNumber()
        {
            var log = "2026-08-29 10:00:00.000 +09:00 [WRN] [Job] L1 기계 검증 오류 발견 (시도 3/검증 완료까지):";

            var metrics = BatchRunLogReader.Read(log);

            Assert.Equal(3, metrics.TotalAttempts);
        }

        [Fact]
        public void Read_SumsTokenUsage()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            Assert.Equal(238908 + 251376, metrics.CacheWriteTokens);
            Assert.Equal(0 + 8518, metrics.CacheReadTokens);
            Assert.Equal(7544 + 11160, metrics.OutputTokens);
        }

        [Fact]
        public void Read_MeasuresWallClockFromFirstToLastTimestamp()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            // 14:15:34 -> 16:39:00
            Assert.NotNull(metrics.WallClock);
            Assert.Equal(2, metrics.WallClock!.Value.Hours);
            Assert.Equal(23, metrics.WallClock.Value.Minutes);
        }

        // [Fix Round 3 - Minor 5] 옛 RedraftLine은 L2 정체 재설계("재시도가 점수를
        // 개선하지 못해...")와 L3 사용자 요청 재설계("사용자가 문서 구조 변경을
        // 요청하여...")를 같은 필드로 뭉갰다 - 루프 사건과 사람의 행동이 다른데도.
        // Batch4Shape에는 L2 정체 재설계만 있다.
        [Fact]
        public void Read_DetectsLoopStagnationRedraft()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            Assert.True(metrics.LoopStagnationRedrafted);
            Assert.False(metrics.UserRequestedRedrafted);
        }

        // 두 사건이 실제로 갈리는지 직접 고정한다 - 문구가 다르면 서로 다른 필드에만
        // 잡혀야 한다.
        [Fact]
        public void Read_DistinguishesLoopStagnationRedraftFromUserRequestedRedraft()
        {
            var loopOnly = "2026-08-29 10:00:00.000 +09:00 [INF] Job - 재시도가 점수를 개선하지 못해 목차를 다시 설계합니다...";
            var userOnly = "2026-08-29 10:00:00.000 +09:00 [INF] Job - 사용자가 문서 구조 변경을 요청하여 목차를 다시 설계합니다...";

            var loopMetrics = BatchRunLogReader.Read(loopOnly);
            var userMetrics = BatchRunLogReader.Read(userOnly);

            Assert.True(loopMetrics.LoopStagnationRedrafted);
            Assert.False(loopMetrics.UserRequestedRedrafted);
            Assert.False(userMetrics.LoopStagnationRedrafted);
            Assert.True(userMetrics.UserRequestedRedrafted);
        }

        // 실물 로그는 같은 점수 블록을 [AI 응답 내용]과 [추출된 JSON 내용]으로 두 번
        // 싣는다. 회차당 궤적 항목은 하나여야 한다 - 둘 다 채택하면 궤적 길이가
        // 두 배가 되어 단조성 위반 셈이 통째로 어긋난다.
        [Fact]
        public void Read_DuplicateJsonBlockPerResponse_CountsOnce()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            Assert.Equal(4, metrics.Trajectory.Count);
        }

        // 명세서 본문에도 숫자가 있다. 리뷰 응답 앵커 밖의 값을 점수로 읽으면
        // 궤적이 오염된다 - 이 계획을 쓰는 동안 실제로 그 사고가 났다.
        [Fact]
        public void Read_IgnoresScoreLikeNumbersOutsideReviewAnchor()
        {
            var polluted = """
                2026-08-29 10:00:00.000 +09:00 [INF] 명세서 본문
                  "ScoreAccuracy": 10,
                  "ScoreCrud": 10,
                  "ScoreInterface": 10,
                  "ScoreException": 10,
                  "ScoreReadability": 10
                """ + "\n" + Batch4Shape;

            var metrics = BatchRunLogReader.Read(polluted);

            Assert.Equal(4, metrics.Trajectory.Count);
        }

        // 실측 로그 210번째 줄 부근: few-shot 프롬프트 템플릿이 실제 응답과 똑같은
        // "ScoreAccuracy": 10 ... "ScoreReadability": 10 5-튜플을 싣는다. 이 텍스트
        // 앞에는 어떤 타임스탬프도, 앵커도 없다 - "앵커를 보기 전에는 절대 collecting
        // 상태에 들어가지 않는다"가 우연이 아니라 구조적 성질임을 이 실물 형태로 고정한다.
        [Fact]
        public void Read_IgnoresFewShotTemplateWithoutAnyPrecedingAnchor()
        {
            var template = """
                [Output Format]
                Output ONLY the final JSON payload. Do not include markdown block markers (```json) or conversational text. Output raw JSON:
                {
                  "HasDefects": true or false (boolean),
                  "FeedbackComment": "Detailed correction instructions if defects are found. Return empty string if HasDefects is false.",
                  "DefectiveSteps": ["S08", "S10"],
                  "ScoreAccuracy": 10,
                  "ScoreCrud": 10,
                  "ScoreInterface": 10,
                  "ScoreException": 10,
                  "ScoreReadability": 10
                }
                """;

            var metrics = BatchRunLogReader.Read(template);

            Assert.Empty(metrics.Trajectory);
        }

        // 값이 0~10을 벗어나면 그 블록은 점수가 아니다(타임스탬프·오류코드가 섞인 것).
        // 채택하지 않고 버린다 - 거짓 궤적보다 짧은 궤적이 낫다.
        [Fact]
        public void Read_RejectsOutOfRangeScoreBlock()
        {
            var corrupt = """
                2026-08-29 10:00:00.000 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료
                  "ScoreAccuracy": 4,
                  "ScoreCrud": 2026,
                  "ScoreInterface": 10,
                  "ScoreException": 10,
                  "ScoreReadability": 10
                """;

            var metrics = BatchRunLogReader.Read(corrupt);

            Assert.Empty(metrics.Trajectory);
        }

        // "동점은 위반이 아니다"라는 CountMonotonicityViolations 주석이 실제로 지켜지는지
        // 고정한다. 엄격 부등호(<)를 (<=)로 잘못 고치면 이 테스트가 잡는다.
        [Fact]
        public void Read_TiedScore_IsNotAViolation()
        {
            var tied = """
                2026-08-29 10:00:00.000 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료
                2026-08-29 10:00:00.001 +09:00 [DBG] [AI 응답 내용]:
                {
                  "ScoreAccuracy": 8,
                  "ScoreCrud": 8,
                  "ScoreInterface": 8,
                  "ScoreException": 8,
                  "ScoreReadability": 8
                }
                2026-08-29 10:05:00.000 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료
                2026-08-29 10:05:00.001 +09:00 [DBG] [AI 응답 내용]:
                {
                  "ScoreAccuracy": 8,
                  "ScoreCrud": 8,
                  "ScoreInterface": 8,
                  "ScoreException": 8,
                  "ScoreReadability": 8
                }
                """;

            var metrics = BatchRunLogReader.Read(tied);

            Assert.Equal(new[] { 80, 80 }, metrics.Trajectory.Select(a => a.NormalizedScore).ToArray());
            Assert.Equal(0, metrics.MonotonicityViolations);
        }

        // 재료가 없으면 0이 아니라 "없음"이다. 0으로 채우면 "캐시를 안 썼다"는
        // 거짓 측정값이 되어 나중에 판정 근거로 쓰인다.
        [Fact]
        public void Read_EmptyLog_ReportsNoWallClock()
        {
            var metrics = BatchRunLogReader.Read(string.Empty);

            Assert.Null(metrics.WallClock);
            Assert.Empty(metrics.Trajectory);
            Assert.Equal(0, metrics.TotalAttempts);
        }

        // 기준선 검증. [Fix Round 2] 전체 로그(output.bak-stage4-control-20260828/)를
        // 직접 읽는 버전은 지웠다 - 그 트리는 문서화된 워크트리 코퍼스 관례(재료
        // 둘: output/·output.bak-2026-08-22/)에 없는 세 번째 재료라, 그 관례만 지킨
        // 워크트리·클린 클론·CI에서는 항상 Assert.True(File.Exists(...))가 실패해
        // 게이트(실패 0)에 닿을 수 없었다. 대신 그 실물 로그에서 스크립트로 잘라낸
        // 발췌(Fixtures/POQSettleBatch4RunExcerpt.log, 손으로 고치지 않음)를
        // 저장소에 커밋해 쓴다 - SchemaClaimGateRegressionTests 등이 이미 쓰는
        // "output/ 실물 발췌를 Fixtures/에 커밋한다" 관례를 그대로 따른다.
        // 이 방식은 output/이 아예 없는 환경에서도, 심링크 없이도 통과한다.
        [Fact]
        public void Read_Batch4Excerpt_MatchesRecomputedOracle()
        {
            var path = Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "POQSettleBatch4RunExcerpt.log");
            var metrics = BatchRunLogReader.Read(File.ReadAllText(path));

            // 궤적과 단조성 위반은 발췌에서도 원본과 동일해야 한다 - 이 계획 전체가
            // 검증하려는 핵심 오라클이다.
            Assert.Equal(new[] { 78, 76, 84, 74 }, metrics.Trajectory.Select(a => a.NormalizedScore).ToArray());
            Assert.Equal(2, metrics.MonotonicityViolations);
            Assert.Equal(2, metrics.UnscoredAttempts);
            Assert.Equal(6, metrics.TotalAttempts);
            Assert.True(metrics.LoopStagnationRedrafted);
            Assert.False(metrics.UserRequestedRedrafted);

            // 토큰 합계·벽시계는 발췌에 실제로 담긴 줄에서 나오는 값이다 - 전체 로그의
            // 합계가 아니다. 발췌는 토큰 사용량 줄 3개(238908+10069+255039 캐시 쓰기,
            // 0+0+8518 캐시 읽기, 7544+9176+21192 출력)와 원본 파일의 첫·마지막
            // 타임스탬프 줄을 그대로 담는다.
            Assert.Equal(238_908 + 10_069 + 255_039, metrics.CacheWriteTokens);
            Assert.Equal(0 + 0 + 8_518, metrics.CacheReadTokens);
            Assert.Equal(7_544 + 9_176 + 21_192, metrics.OutputTokens);
            Assert.NotNull(metrics.WallClock);
            Assert.Equal(new TimeSpan(2, 23, 26), metrics.WallClock!.Value);
        }

        // 창 경계. 실측(코퍼스 output.bak-* 전수 639개 로그): 앵커에서 5축 완성까지의
        // 최대 거리는 11줄이다. 상한 15는 그 위 4줄의 여유다 - 정확히 15줄째에서
        // 완성되는 블록은 채택돼야 한다. 이 여유가 방금 실측한 최대치보다 큰지,
        // 다음 사람이 상수를 고칠 때 이 값으로 확인할 수 있다.
        [Fact]
        public void Read_ScoreCompletingAtWindowBoundary_IsAccepted()
        {
            var lines = new List<string> { "2026-08-29 10:00:00.000 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료" };
            for (var i = 0; i < 10; i++) lines.Add($"필러 줄 {i}");
            lines.Add("\"ScoreAccuracy\": 8,");
            lines.Add("\"ScoreCrud\": 8,");
            lines.Add("\"ScoreInterface\": 8,");
            lines.Add("\"ScoreException\": 8,");
            lines.Add("\"ScoreReadability\": 8"); // 앵커로부터 15번째 줄

            var metrics = BatchRunLogReader.Read(string.Join("\n", lines));

            Assert.Single(metrics.Trajectory);
        }

        // 16번째 줄에서 완성되면 창을 넘겨 버려진다 - 거짓 궤적보다 빈 궤적이 낫다는
        // 정책이 창 경계에서도 지켜지는지 고정한다.
        [Fact]
        public void Read_ScoreCompletingPastWindowBoundary_IsDiscarded()
        {
            var lines = new List<string> { "2026-08-29 10:00:00.000 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료" };
            for (var i = 0; i < 11; i++) lines.Add($"필러 줄 {i}");
            lines.Add("\"ScoreAccuracy\": 8,");
            lines.Add("\"ScoreCrud\": 8,");
            lines.Add("\"ScoreInterface\": 8,");
            lines.Add("\"ScoreException\": 8,");
            lines.Add("\"ScoreReadability\": 8"); // 앵커로부터 16번째 줄

            var metrics = BatchRunLogReader.Read(string.Join("\n", lines));

            Assert.Empty(metrics.Trajectory);
        }

        // ── 2026-09-02 A2: POQSettleBatch5 대조 실행이 드러낸 조용한 결함 둘 ──
        //
        // 둘 다 이 판에서는 서로 상쇄돼 Trajectory.Count가 우연히 맞았다(그래서
        // UnscoredAttempts만 옳았다). 다음 판엔 상쇄되지 않는다.

        /// <summary>
        /// 다섯 축이 한 줄에 실린 JSON. 실물이다 - `output.bak-stage4-control-20260828/
        /// logs-batch5-verify/reset-20260831.log:2286`이 6차(84점)를 이 형태로 실었고,
        /// 옛 리더는 그 회차를 통째로 잃었다.
        ///
        /// 원인은 `ScoreLine.Match(line)`이 줄당 **첫 매치만** 취하는 것이다. 한 줄에
        /// 다섯 축이 다 있으면 Accuracy 하나만 담기고, 다음 줄(`[추출된 JSON 내용]`
        /// 중복)에서 또 Accuracy만 담겨 덮어쓴다 - 다섯이 영영 안 모여 창을 넘고 버려진다.
        /// 클래스 주석이 「JSON이 한 줄로 덤프되는 지금 형식」을 전제한다고 적어 둔 것과
        /// 정규식이 요구하는 것이 정반대였다. 639편 실측이 pretty-print만 봤다.
        /// </summary>
        [Fact]
        public void Read_WhenAllFiveAxesAreOnOneLine_ShouldStillScoreTheAttempt()
        {
            const string log = """
                2026-08-31 00:00:37.011 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1497
                2026-08-31 00:00:37.011 +09:00 [DBG] [AI 응답 내용]:
                {"HasDefects":true,"FeedbackComment":"결함","DefectiveSteps":["S16"],"ScoreAccuracy":8,"ScoreCrud":9,"ScoreInterface":9,"ScoreException":6,"ScoreReadability":10}
                2026-08-31 00:00:37.011 +09:00 [DBG] [추출된 JSON 내용]: {"HasDefects":true,"FeedbackComment":"결함","DefectiveSteps":["S16"],"ScoreAccuracy":8,"ScoreCrud":9,"ScoreInterface":9,"ScoreException":6,"ScoreReadability":10}
                2026-08-31 00:00:37.125 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 6/6): 결함
                """;

            var m = BatchRunLogReader.Read(log);

            var only = Assert.Single(m.Trajectory);
            Assert.Equal(42, only.TotalScore);
            Assert.Equal(84, only.NormalizedScore);
        }

        /// <summary>
        /// 파이프라인이 파싱에 실패해 **버린** 응답에서 점수를 건지면 안 된다.
        ///
        /// 실물이다 - `reset-20260830.log:32539`의 응답은 원문에 8·9·9·6·9(82점)를
        /// 실었지만 `FeedbackComment`가 잘려 JSON이 깨졌고, `ParseReviewResult`가
        /// 예외를 받아 **다섯 축을 전부 0으로** 돌렸다. 파이프라인 자신이 같은 로그
        /// 32577행에 「3차 시도(**0**/100)가 최고 후보(1차, 78/100)를 넘지 못해」라고
        /// 적었는데, 옛 리더는 그 회차를 82점으로 궤적에 실었다.
        ///
        /// 그래서 다섯 축이 모여도 즉시 확정하지 않는다 - 다음 앵커(또는 로그 끝)까지
        /// 파싱 실패 줄이 안 나온 것을 보고 확정한다. 실패 줄은 응답 본문 **뒤에**,
        /// 다음 앵커 **앞에** 오므로 짝이 정확하다.
        /// </summary>
        [Fact]
        public void Read_WhenThePipelineDiscardedTheResponse_ShouldNotScoreThatAttempt()
        {
            const string log = """
                2026-08-30 23:13:29.121 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1468
                2026-08-30 23:13:29.121 +09:00 [DBG] [AI 응답 내용]:
                {
                  "HasDefects": true,
                  "FeedbackComment": "결함",
                  "DefectiveSteps": ["S16"],
                  "ScoreAccuracy": 8,
                  "ScoreCrud": 9,
                  "ScoreInterface": 9,
                  "ScoreException": 6,
                  "ScoreReadability": 9
                }
                2026-08-30 23:13:29.124 +09:00 [ERR] JSON 검토 보고서 파싱 중 오류 발생 (POQSettleBatch5)
                System.Text.Json.JsonReaderException: '0xEC' is invalid after a value. Expected either ',', '}', or ']'. LineNumber: 2 | BytePositionInLine: 473.
                2026-08-30 23:13:29.267 +09:00 [INF] POQSettleBatch5 - 3차 시도(0/100)가 최고 후보(1차, 78/100)를 넘지 못해 최고 후보 상태로 되돌립니다.
                2026-08-30 23:13:29.300 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 3/6): 결함
                """;

            var m = BatchRunLogReader.Read(log);

            Assert.Empty(m.Trajectory);

            // 그 회차는 「채점을 못 받은 회차」다 - 정의상 소진으로 세어야 한다.
            Assert.Equal(3, m.TotalAttempts);
            Assert.Equal(3, m.UnscoredAttempts);
        }

        /// <summary>
        /// 실패는 그 회차 하나만 버린다. 앞뒤 회차는 살아 있어야 한다 - 실패 줄
        /// 하나가 궤적 전체를 지우면 「거짓 궤적보다 짧은 궤적이 낫다」가 아니라
        /// 그냥 재료를 잃는 것이다.
        /// </summary>
        [Fact]
        public void Read_WhenOneOfThreeResponsesFailedToParse_ShouldDiscardOnlyThatOne()
        {
            const string log = """
                2026-08-30 22:42:51.128 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1999
                2026-08-30 22:42:51.128 +09:00 [DBG] [AI 응답 내용]:
                {"HasDefects":true,"ScoreAccuracy":8,"ScoreCrud":8,"ScoreInterface":8,"ScoreException":7,"ScoreReadability":8}
                2026-08-30 22:42:51.500 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 1/6): 결함
                2026-08-30 23:13:29.121 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1468
                2026-08-30 23:13:29.121 +09:00 [DBG] [AI 응답 내용]:
                {"HasDefects":true,"ScoreAccuracy":1,"ScoreCrud":1,"ScoreInterface":1,"ScoreException":1,"ScoreReadability":1}
                2026-08-30 23:13:29.124 +09:00 [ERR] JSON 검토 보고서 파싱 중 오류 발생 (POQSettleBatch5)
                2026-08-30 23:13:29.300 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 2/6): 결함
                2026-08-30 23:30:57.479 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1311
                2026-08-30 23:30:57.479 +09:00 [DBG] [AI 응답 내용]:
                {"HasDefects":true,"ScoreAccuracy":9,"ScoreCrud":9,"ScoreInterface":10,"ScoreException":7,"ScoreReadability":10}
                2026-08-30 23:30:57.675 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 3/6): 결함
                """;

            var m = BatchRunLogReader.Read(log);

            Assert.Equal(new[] { 78, 90 }, m.Trajectory.Select(a => a.NormalizedScore).ToArray());
            Assert.Equal(3, m.TotalAttempts);
            Assert.Equal(1, m.UnscoredAttempts);
        }

        /// <summary>
        /// 실패 줄이 **다음** 앵커 뒤에 오면 그것은 그 다음 회차의 실패다 - 이미
        /// 확정된 앞 회차를 소급해 지우면 안 된다.
        /// </summary>
        [Fact]
        public void Read_ParseFailureAfterALaterAnchor_ShouldNotRetroactivelyDiscardTheEarlierAttempt()
        {
            const string log = """
                2026-08-30 22:42:51.128 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1999
                2026-08-30 22:42:51.128 +09:00 [DBG] [AI 응답 내용]:
                {"HasDefects":true,"ScoreAccuracy":8,"ScoreCrud":8,"ScoreInterface":8,"ScoreException":7,"ScoreReadability":8}
                2026-08-30 22:42:51.500 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 1/6): 결함
                2026-08-30 23:13:29.121 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 0
                2026-08-30 23:13:29.124 +09:00 [ERR] JSON 검토 보고서 파싱 중 오류 발생 (POQSettleBatch5)
                2026-08-30 23:13:29.300 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 2/6): 결함
                """;

            var m = BatchRunLogReader.Read(log);

            var only = Assert.Single(m.Trajectory);
            Assert.Equal(78, only.NormalizedScore);
        }


        // ── 2026-09-02 A3: 지표 둘을 §3-1·§3-9가 실제로 약속한 것으로 재정의한다 ──

        /// <summary>
        /// §3-1이 약속한 것은 「점수가 내려간 회차마다 되돌림이 있었는가」다.
        /// <c>MonotonicityViolations</c>(원점수 궤적의 하락 회차 수)는 롤백이
        /// 완벽해도 0이 되지 않으므로 그 약속을 재지 못한다 - 롤백은 문서 상태를
        /// 되돌릴 뿐 다음 회차의 Critic 점수를 올려 주지 않는다.
        /// </summary>
        [Fact]
        public void Read_WhenEveryRegressionHasARollbackLine_ReportsNoUnrolledBackRegression()
        {
            const string log = """
                2026-08-30 22:42:51.128 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1
                {"ScoreAccuracy":8,"ScoreCrud":8,"ScoreInterface":8,"ScoreException":7,"ScoreReadability":8}
                2026-08-30 22:42:51.500 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 1/6): 결함
                2026-08-30 22:59:41.351 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1
                {"ScoreAccuracy":9,"ScoreCrud":9,"ScoreInterface":5,"ScoreException":6,"ScoreReadability":9}
                2026-08-30 22:59:41.534 +09:00 [INF] POQSettleBatch5 - 2차 시도(76/100)가 최고 후보(1차, 78/100)를 넘지 못해 최고 후보 상태로 되돌립니다.
                2026-08-30 22:59:41.600 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 2/6): 결함
                2026-08-31 00:00:37.127 +09:00 [ERR] POQSettleBatch5 - [[L2 AI 리뷰]] 최종 보완 실패. 가장 높은 점수를 받은 1차 시도(78/100)를 채택합니다.
                """;

            var m = BatchRunLogReader.Read(log);

            Assert.Equal(new[] { 78, 76 }, m.Trajectory.Select(a => a.NormalizedScore).ToArray());
            Assert.Equal(0, m.UnrolledBackRegressions);
            Assert.Equal(78, m.AdoptedScore);
            Assert.True(m.AdoptedIsBest);
        }

        /// <summary>
        /// 되돌림 없이 하락한 회차가 있으면 그것이 §3-1의 구현 결함이다 - 이 지표는
        /// 그때만 0이 아니어야 한다.
        /// </summary>
        [Fact]
        public void Read_WhenARegressionHasNoRollbackLine_ReportsIt()
        {
            const string log = """
                2026-08-30 22:42:51.128 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1
                {"ScoreAccuracy":8,"ScoreCrud":8,"ScoreInterface":8,"ScoreException":7,"ScoreReadability":8}
                2026-08-30 22:42:51.500 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 1/6): 결함
                2026-08-30 22:59:41.351 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1
                {"ScoreAccuracy":9,"ScoreCrud":9,"ScoreInterface":5,"ScoreException":6,"ScoreReadability":9}
                2026-08-30 22:59:41.600 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 2/6): 결함
                """;

            var m = BatchRunLogReader.Read(log);

            Assert.Equal(1, m.UnrolledBackRegressions);
        }

        /// <summary>
        /// 채점받지 못한 회차(파싱 실패로 0점 처리된 회차)도 되돌림 줄을 남긴다.
        /// 궤적에는 그 회차가 없으므로 되돌림 줄이 하나 남는데, 그것을 결함으로
        /// 세면 안 된다 - 남는 방향은 무해하고, 모자라는 방향만 결함이다.
        /// </summary>
        [Fact]
        public void Read_ExtraRollbackLineForADiscardedAttempt_IsNotCountedAsAViolation()
        {
            const string log = """
                2026-08-30 22:42:51.128 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1
                {"ScoreAccuracy":8,"ScoreCrud":8,"ScoreInterface":8,"ScoreException":7,"ScoreReadability":8}
                2026-08-30 23:13:29.121 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1
                {"ScoreAccuracy":9,"ScoreCrud":9,"ScoreInterface":9,"ScoreException":6,"ScoreReadability":9}
                2026-08-30 23:13:29.124 +09:00 [ERR] JSON 검토 보고서 파싱 중 오류 발생 (POQSettleBatch5)
                2026-08-30 23:13:29.267 +09:00 [INF] POQSettleBatch5 - 2차 시도(0/100)가 최고 후보(1차, 78/100)를 넘지 못해 최고 후보 상태로 되돌립니다.
                2026-08-30 23:20:32.075 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1
                {"ScoreAccuracy":8,"ScoreCrud":9,"ScoreInterface":6,"ScoreException":5,"ScoreReadability":9}
                2026-08-30 23:20:32.212 +09:00 [INF] POQSettleBatch5 - 3차 시도(74/100)가 최고 후보(1차, 78/100)를 넘지 못해 최고 후보 상태로 되돌립니다.
                """;

            var m = BatchRunLogReader.Read(log);

            Assert.Equal(new[] { 78, 74 }, m.Trajectory.Select(a => a.NormalizedScore).ToArray());
            Assert.Equal(0, m.UnrolledBackRegressions);
        }

        /// <summary>
        /// 게이트를 통과해 끝난 판에는 「가장 높은 점수를 받은 …를 채택합니다」 줄이
        /// 없다 - 그때 채택되는 것은 마지막 회차다. 그 마지막이 최고점이 아닐 수
        /// 있고, 이 검사는 그 자리에서 발화해야 한다.
        /// </summary>
        [Fact]
        public void Read_OnThePassPath_AdoptsTheLastAttemptAndCanReportItIsNotTheBest()
        {
            const string log = """
                2026-08-30 22:42:51.128 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1
                {"ScoreAccuracy":9,"ScoreCrud":9,"ScoreInterface":10,"ScoreException":7,"ScoreReadability":10}
                2026-08-30 22:42:51.500 +09:00 [WRN] [POQSettleBatch5] L2 AI 리뷰 결함 발견 (시도 1/6): 결함
                2026-08-30 22:59:41.351 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1
                {"ScoreAccuracy":9,"ScoreCrud":8,"ScoreInterface":9,"ScoreException":7,"ScoreReadability":9}
                2026-08-30 22:59:41.534 +09:00 [INF] [POQSettleBatch5] L1/L2 자동 검증 모두 통과!
                """;

            var m = BatchRunLogReader.Read(log);

            Assert.Equal(new[] { 90, 84 }, m.Trajectory.Select(a => a.NormalizedScore).ToArray());
            Assert.Equal(84, m.AdoptedScore);
            Assert.False(m.AdoptedIsBest);
        }

        /// <summary>
        /// 채택 줄도 통과 줄도 없으면 「모른다」다 - 0이나 false로 채우면 없는
        /// 판정이 생긴다.
        /// </summary>
        [Fact]
        public void Read_WithNoAdoptionOrPassLine_ReportsAdoptedScoreAsUnknown()
        {
            const string log = """
                2026-08-30 22:42:51.128 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 1
                {"ScoreAccuracy":8,"ScoreCrud":8,"ScoreInterface":8,"ScoreException":7,"ScoreReadability":8}
                """;

            var m = BatchRunLogReader.Read(log);

            Assert.Null(m.AdoptedScore);
            Assert.Null(m.AdoptedIsBest);
        }

        /// <summary>
        /// §3-9(입력 축소)가 실제로 좁힌 것은 <b>단계 섹션 호출</b>이다. 「호출당
        /// 캐시 쓰기」를 전체 호출로 나누면 개선이 성공할수록(회차가 줄어 분모가
        /// 작아질수록) 지표가 나빠진다 - 대조 실행에서 실제로 그랬다. 그래서
        /// 호출 종류별로 가른다.
        ///
        /// 귀속은 <b>바로 앞의 요청 줄</b>이 정한다. 요청 줄은 한 번만 쓰인다 -
        /// 요청이 실패해 사용량 줄이 안 나오면, 다음 사용량 줄이 낡은 종류를
        /// 조용히 빨아들이는 것을 막는다.
        /// </summary>
        [Fact]
        public void Read_AttributesTokenUsageToThePrecedingRequestKind()
        {
            const string log = """
                2026-08-30 22:02:35.575 +09:00 [INF] AI 배치 계획 브레인스토밍 요청 전송 - JobName: POQSettleBatch5, TargetLanguage: C#, Effort: high
                2026-08-30 22:07:46.606 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 271982, 캐시 읽기: 0, 출력: 28716, 추론: 미보고
                2026-08-30 22:09:24.249 +09:00 [INF] AI 배치 계획 골격 생성 요청 전송 - JobName: POQSettleBatch5, 단계 수: 20개
                2026-08-30 22:09:30.000 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 300000, 캐시 읽기: 10, 출력: 100, 추론: 미보고
                2026-08-30 22:12:05.076 +09:00 [INF] AI 배치 단계 섹션 생성 요청 전송 - JobName: POQSettleBatch5, Step: S01, 재시도 피드백: false
                2026-08-30 22:12:40.000 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 90000, 캐시 읽기: 20, 출력: 200, 추론: 미보고
                2026-08-30 22:13:05.076 +09:00 [INF] AI 배치 단계 섹션 생성 요청 전송 - JobName: POQSettleBatch5, Step: S02, 재시도 피드백: false
                2026-08-30 22:13:40.000 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 92910, 캐시 읽기: 30, 출력: 300, 추론: 미보고
                """;

            var m = BatchRunLogReader.Read(log);

            var steps = Assert.Single(m.CallsByKind, g => g.Kind.Contains("단계 섹션"));
            Assert.Equal(2, steps.Calls);
            Assert.Equal(182910, steps.CacheWriteTokens);
            Assert.Equal(91455, steps.CacheWritePerCall);

            // 전체로 나누면 지표가 이 값을 훨씬 넘는다 - 그것이 §7-7이 적은 함정이다.
            Assert.Equal(754892 / 4, m.CacheWriteTokens / 4);
            Assert.Equal(4, m.CallsByKind.Sum(g => g.Calls));
        }

        /// <summary>
        /// 요청 줄 없이 나온 사용량은 「모름」으로 모은다. 마지막 종류에 붙이면
        /// 어느 호출이 얼마를 썼는지가 조용히 틀린다.
        /// </summary>
        [Fact]
        public void Read_UsageWithoutItsOwnRequestLine_IsAttributedToUnknown()
        {
            const string log = """
                2026-08-30 22:12:05.076 +09:00 [INF] AI 배치 단계 섹션 생성 요청 전송 - JobName: POQSettleBatch5, Step: S01, 재시도 피드백: false
                2026-08-30 22:12:40.000 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 90000, 캐시 읽기: 20, 출력: 200, 추론: 미보고
                2026-08-30 22:13:40.000 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 5, 캐시 읽기: 0, 출력: 1, 추론: 미보고
                """;

            var m = BatchRunLogReader.Read(log);

            var steps = Assert.Single(m.CallsByKind, g => g.Kind.Contains("단계 섹션"));
            Assert.Equal(1, steps.Calls);
            Assert.Equal(90000, steps.CacheWriteTokens);

            var unknown = Assert.Single(m.CallsByKind, g => !g.Kind.Contains("단계 섹션"));
            Assert.Equal(1, unknown.Calls);
            Assert.Equal(5, unknown.CacheWriteTokens);
        }


        /// <summary>
        /// 단계 섹션 호출은 <b>병렬로 돈다</b> - 요청 줄이 자기 사용량 줄 바로 앞에
        /// 오지 않는다. 실측(`reset-20260830.log:22:25:51`)에서 S18 재시도 요청 바로
        /// 뒤의 사용량이 실제로는 S20의 것이었다. 그래서 짝은 요청이 아니라
        /// <b>바로 뒤의 응답 줄</b>이 정한다 - 사용량은 클라이언트가 찍고 응답은
        /// 서비스가 곧바로 찍으므로 둘은 제어 흐름으로 붙어 있다(실측 68건 중 66건이
        /// 정확히 다음 줄이고, 나머지 둘은 응답 줄 자체가 없는 갈래다).
        /// </summary>
        [Fact]
        public void Read_WhenCallsAreInterleaved_AttributesUsageByTheFollowingResponseLine()
        {
            const string log = """
                2026-08-30 22:09:24.249 +09:00 [INF] AI 배치 계획 골격 생성 요청 전송 - JobName: POQSettleBatch5, 단계 수: 20개
                2026-08-30 22:09:30.000 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 300000, 캐시 읽기: 0, 출력: 100, 추론: 미보고
                2026-08-30 22:09:30.001 +09:00 [INF] AI 배치 계획 골격 생성 응답 수신 완료 - JobName: POQSettleBatch5, 응답 길이: 100
                2026-08-30 22:25:11.000 +09:00 [INF] AI 배치 단계 섹션 생성 요청 전송 - JobName: POQSettleBatch5, Step: S18, 재시도 피드백: false
                2026-08-30 22:25:11.100 +09:00 [INF] AI 배치 단계 섹션 생성 요청 전송 - JobName: POQSettleBatch5, Step: S20, 재시도 피드백: false
                2026-08-30 22:25:51.526 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 90000, 캐시 읽기: 8802, 출력: 16137, 추론: 미보고
                2026-08-30 22:25:51.526 +09:00 [INF] AI 배치 단계 섹션 생성 응답 수신 완료 - JobName: POQSettleBatch5, Step: S18, 응답 길이: 8750
                2026-08-30 22:25:57.689 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 92910, 캐시 읽기: 8802, 출력: 5846, 추론: 미보고
                2026-08-30 22:25:57.690 +09:00 [INF] AI 배치 단계 섹션 생성 응답 수신 완료 - JobName: POQSettleBatch5, Step: S20, 응답 길이: 4480
                """;

            var m = BatchRunLogReader.Read(log);

            var steps = Assert.Single(m.CallsByKind, g => g.Kind.Contains("단계 섹션"));
            Assert.Equal(2, steps.Calls);
            Assert.Equal(91455, steps.CacheWritePerCall);

            var skeleton = Assert.Single(m.CallsByKind, g => g.Kind.Contains("골격"));
            Assert.Equal(1, skeleton.Calls);

            // 귀속되지 않은 호출이 있으면 안 된다 - 실측에서 요청 줄로 짝지었을 때
            // 68건 중 10건(673,030 토큰)이 「요청 줄 없음」으로 샜다.
            Assert.Equal(3, m.CallsByKind.Sum(g => g.Calls));
            Assert.Equal(2, m.CallsByKind.Count);
        }

        /// <summary>
        /// 응답 줄이 없는 갈래(브레인스토밍·목차 수립)는 요청 줄로 짝짓는다. 그
        /// 둘은 순차 실행이라 요청 바로 뒤가 자기 사용량이다.
        /// </summary>
        [Fact]
        public void Read_WhenNoResponseLineFollows_FallsBackToTheRequestLine()
        {
            const string log = """
                2026-08-30 22:02:35.575 +09:00 [INF] AI 배치 계획 브레인스토밍 요청 전송 - JobName: POQSettleBatch5, TargetLanguage: C#, Effort: high
                2026-08-30 22:07:46.606 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 271982, 캐시 읽기: 0, 출력: 28716, 추론: 미보고
                2026-08-30 22:09:24.231 +09:00 [INF] AI 배치 계획 목차 수립 요청 전송 - JobName: POQSettleBatch5
                2026-08-30 22:09:24.240 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 8696, 캐시 읽기: 0, 출력: 10800, 추론: 미보고
                """;

            var m = BatchRunLogReader.Read(log);

            Assert.Equal(2, m.CallsByKind.Count);
            Assert.Equal(271982, Assert.Single(m.CallsByKind, g => g.Kind.Contains("브레인스토밍")).CacheWriteTokens);
            Assert.Equal(8696, Assert.Single(m.CallsByKind, g => g.Kind.Contains("목차")).CacheWriteTokens);
        }

    }
}
