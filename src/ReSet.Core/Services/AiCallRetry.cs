using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도 계획. 상수가 아니라 값인 이유는 생성 호출이 나중에 다른 계획으로 같은
    /// 헬퍼를 쓰기 때문이다(설계 §3의 "인프라는 공용" 결정).
    /// </summary>
    public readonly record struct RetryPlan(int MaxTries, TimeSpan MinDelay, TimeSpan MaxDelay)
    {
        /// <summary>
        /// GenerateStepSectionWithFloorRetryAsync가 이미 쓰는 값을 그대로 쓴다.
        /// 그 자리의 주석이 근거다 - "단계당 1회로 하드 캡해 폭주를 막는다",
        /// "무작위 지연이 상관된 폭풍을 흩트러진 재시도로 바꾼다".
        /// </summary>
        public static readonly RetryPlan Default =
            new(2, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500));

        /// <summary>테스트용. 횟수와 분기만 보고 실제로 기다리지 않는다.</summary>
        public static readonly RetryPlan NoDelay =
            new(2, TimeSpan.Zero, TimeSpan.Zero);
    }

    /// <summary>
    /// 재시도를 다 쓰고도 실패했다.
    ///
    /// OperationCanceledException을 상속하지 <b>않는다.</b> 이것이 이 설계의 핵심
    /// 이음매다 - 호출부 55곳이 when (ex is not OperationCanceledException)으로 거르므로,
    /// 타임아웃을 그 형식 그대로 올려보내면 또 놓치고 "사용자 취소"로 둔갑한다.
    /// </summary>
    public sealed class AiCallFailedException : Exception
    {
        public int Attempts { get; }

        /// <summary>왜 포기했나. 호출부가 쿼터 소진을 실패와 가르는 근거다(설계 §4-2).</summary>
        public AiRetryVerdict Verdict { get; }

        public AiCallFailedException(string message, Exception inner, int attempts, AiRetryVerdict verdict)
            : base(message, inner)
        {
            Attempts = attempts;
            Verdict = verdict;
        }
    }

    /// <summary>
    /// AI 호출 하나를 계획대로 재시도한다. 재시도 전체를 하나의 Task로 돌려주므로
    /// 호출부는 WrapWithProgress를 그대로 쓸 수 있다.
    ///
    /// 이 재시도는 MaxL2Attempts를 소모하지 않는다. 그 예산은 Actor-Critic 문서 레벨의
    /// 것이고, 여기는 호출 하나가 일시적으로 실패한 것을 메우는 국소 보수다.
    /// </summary>
    public static class AiCallRetry
    {
        public static Task<T> ExecuteAsync<T>(
            Func<Task<T>> factory,
            CancellationToken cancellationToken,
            RetryPlan? plan = null) =>
            ExecuteAsync(_ => factory(), cancellationToken, plan);

        /// <summary>
        /// 같은 재시도에 <b>호출 하나의 벽시계 상한</b>을 얹는다. 상한이 있으면 사용자 토큰과 연결한 토큰을
        /// 팩토리에 넘기고 <paramref name="deadline"/> 뒤에 그것만 취소한다.
        ///
        /// [왜 이 자리인가 - 2026-09-16] 상한이 끊으면 <c>OperationCanceledException</c> 이 오는데
        /// <b>사용자 토큰은 취소되지 않았다</b>. <see cref="AiRetryPolicy.Classify"/> 가 그 구분을 이미 토큰으로
        /// 하므로(그 자리 주석: 「구분은 우리가 넘긴 토큰이다」) 판정이 <see cref="AiRetryVerdict.Transient"/> 가 되어
        /// 계획대로 한 번 더 부르고, 다 쓰면 <see cref="AiCallFailedException"/>(비 OCE)으로 올라가 호출부 55 곳의
        /// <c>when (ex is not OperationCanceledException)</c> 이 잡는다 - 판이 죽지 않고 배너로 산다.
        ///
        /// [실측이 정한 값 - 응답 574 건] 본문을 낸 최장 호출 24.9 분(명세서 생성) · 리뷰 중 최장 정상 11.8 분 ·
        /// 10 분 초과 8/493. 통합 계획서 리뷰 한 건이 52.9 분을 태우고 본문 0 자를 냈다(B17).
        /// 그래서 리뷰에만 20 분을 건다 - 이제까지 성공한 호출은 하나도 자르지 않는 값이다.
        /// 판독: docs/audit-reports/2026-09-16-AI호출-벽시계-상한-사전선언.md
        /// </summary>
        /// <param name="deadline">호출 하나의 상한. null 이면 연결 토큰을 만들지 않고 종전 경로 그대로다.</param>
        public static async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken,
            RetryPlan? plan = null,
            TimeSpan? deadline = null)
        {
            var effectivePlan = plan ?? RetryPlan.Default;

            // 0·음수는 1로 절상한다. 상한은 두지 않는다 - VerificationPipelineOrchestrator의
            // _stepConcurrency = Math.Max(1, stepConcurrency)와 같은 결정이다. "0회 시도"를
            // 그대로 두면 루프가 한 번도 안 돌아 lastFailure가 null로 남고, 아래
            // Log.Error(..., lastFailure!.Message)에서 진짜 NullReferenceException이 난다.
            // 최소 1회는 실제로 불러본다.
            var effectiveMaxTries = Math.Max(1, effectivePlan.MaxTries);
            Exception? lastFailure = null;

            for (var attempt = 1; attempt <= effectiveMaxTries; attempt++)
            {
                // 상한이 없으면 링크를 만들지 않는다 - 넘어온 토큰이 그대로 팩토리에 간다.
                using var deadlineScope = deadline is { } window
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : null;
                deadlineScope?.CancelAfter(deadline!.Value);

                try
                {
                    return await factory(deadlineScope?.Token ?? cancellationToken);
                }
                catch (Exception ex)
                {
                    var verdict = AiRetryPolicy.Classify(ex, cancellationToken);

                    // 상한이 끊었다면 그 사실을 로그에 남긴다 - 「일시적 실패」로만 적으면 판독에서 원인을 못 찾는다.
                    if (verdict == AiRetryVerdict.Transient
                        && ex is OperationCanceledException
                        && deadlineScope is { IsCancellationRequested: true })
                    {
                        Log.Warning(
                            "[벽시계 상한] AI 호출이 {Minutes:F0}분을 넘겨 끊었습니다 - 시도 {Attempt}/{MaxTries}. 끊긴 호출도 생성 토큰만큼 청구됩니다.",
                            deadline!.Value.TotalMinutes, attempt, effectiveMaxTries);
                    }

                    // 취소를 삼키면 실패로 위장한 정상 반환이 되어 취소 사실이 사라진다.
                    if (verdict == AiRetryVerdict.Cancelled)
                    {
                        throw;
                    }

                    // 같은 입력에 같은 응답이 온다. 돈만 태우므로 즉시 올려보낸다.
                    if (verdict == AiRetryVerdict.Fatal)
                    {
                        throw;
                    }

                    // 지금은 안 된다 - 재시도해도 같다. 다만 사유를 실어 올려보낸다.
                    if (verdict == AiRetryVerdict.Exhausted)
                    {
                        throw new AiCallFailedException(
                            "AI 호출이 쿼터 소진으로 중단됐습니다.", ex, attempt, AiRetryVerdict.Exhausted);
                    }

                    lastFailure = ex;

                    if (attempt < effectiveMaxTries)
                    {
                        Log.Warning(
                            "[재시도] AI 호출이 일시적으로 실패했습니다 - 시도 {Attempt}/{MaxTries}, 사유: {Reason}",
                            attempt, effectiveMaxTries, ex.Message);

                        await DelayAsync(effectivePlan, cancellationToken);
                    }
                }
            }

            Log.Error(
                "[재시도] AI 호출이 {MaxTries}회 모두 실패했습니다 - 마지막 사유: {Reason}",
                effectiveMaxTries, lastFailure!.Message);

            // 재시도를 다 쓴 뒤에도 마지막 실패가 429였다면 그때는 짧은 막힘이 아니다
            // (설계 §4-1). 그 밖의 일시적 실패는 계속 Transient로 보고한다 - 승격
            // 근거가 429 하나뿐이라서다.
            var finalVerdict = AiRetryPolicy.Classify(lastFailure, cancellationToken) == AiRetryVerdict.Transient
                && lastFailure is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
                ? AiRetryVerdict.Exhausted
                : AiRetryVerdict.Transient;

            throw new AiCallFailedException(
                $"AI 호출이 {effectiveMaxTries}회 모두 실패했습니다: {lastFailure.Message}",
                lastFailure,
                effectiveMaxTries,
                finalVerdict);
        }

        /// <summary>
        /// 무작위 지연. 동시 실행 중에는 429가 여러 호출을 같은 창에서 때리므로,
        /// 무지연으로 재시도하면 그 시도들을 모두 같은 창 안에 쏟아붓게 된다.
        /// 무작위 지연이 상관된 폭풍을 흩트러진 재시도로 바꾼다.
        /// </summary>
        private static Task DelayAsync(RetryPlan plan, CancellationToken cancellationToken)
        {
            if (plan.MaxDelay <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            var milliseconds = Random.Shared.Next(
                (int)plan.MinDelay.TotalMilliseconds,
                (int)plan.MaxDelay.TotalMilliseconds);

            return Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
        }
    }
}
