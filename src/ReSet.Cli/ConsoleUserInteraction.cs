using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Cli
{
    public class ConsoleUserInteraction : IVerificationUserInteraction
    {
        private string StripMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(text, @"\[\/?[a-zA-Z\s,=#]*\]", "");
        }

        public void NotifyStatus(string message)
        {
            AnsiConsole.MarkupLine(message);
            Serilog.Log.Information(StripMarkup(message));
        }

        public void NotifyError(string message)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
            Serilog.Log.Error(StripMarkup(message));
        }

        public void NotifyWarnings(string selectedOption, List<string> warnings)
        {
            if (warnings == null || warnings.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[yellow]Stored Procedure 수집 중 일부 데이터 누락 또는 접근 실패가 감지되었습니다. AI 분석 프롬프트에는 포함되나, 결과물이 불완전할 수 있습니다:[/]");
            sb.AppendLine();
            foreach (var warn in warnings)
            {
                sb.AppendLine($"[grey]- {Markup.Escape(warn)}[/]");
            }

            var panel = new Panel(new Markup(sb.ToString().TrimEnd()))
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader($"[yellow] 경고: {Markup.Escape(selectedOption)} 수집 정보 누락 ([bold]{warnings.Count}[/]) [/]"),
                BorderStyle = new Style(Color.Yellow)
            };

            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();

            Serilog.Log.Warning($"[{selectedOption}] 수집 경고 ({warnings.Count}건):");
            foreach (var warn in warnings)
            {
                Serilog.Log.Warning($"  - {warn}");
            }
        }

        public void NotifyL1Errors(string selectedOption, int attempt, int maxAttempts, List<string> errors)
        {
            var maxStr = maxAttempts == -1 ? "검증 완료까지" : maxAttempts.ToString();
            AnsiConsole.MarkupLine($"[yellow]{selectedOption} - [[L1 기계 검증]] 문법/구조 오류 발견 (시도 {attempt}/{maxStr}):[/]");
            foreach (var err in errors)
            {
                AnsiConsole.MarkupLine($"  [red]=> {Markup.Escape(err)}[/]");
            }
            AnsiConsole.WriteLine();

            Serilog.Log.Warning($"[{selectedOption}] L1 기계 검증 오류 발견 (시도 {attempt}/{maxStr}):");
            foreach (var err in errors)
            {
                Serilog.Log.Warning($"  - {err}");
            }
        }

        public void NotifyL2Defects(string selectedOption, int attempt, int maxAttempts, string feedbackComment)
        {
            var maxStr = maxAttempts == -1 ? "검증 완료까지" : maxAttempts.ToString();
            AnsiConsole.MarkupLine($"[yellow]{selectedOption} - [[L2 AI 리뷰]] 결함 및 보완 권고 발견 (시도 {attempt}/{maxStr}):[/]");
            if (!string.IsNullOrWhiteSpace(feedbackComment))
            {
                var lines = feedbackComment.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    AnsiConsole.MarkupLine($"  [red]=> {Markup.Escape(line)}[/]");
                }
            }
            AnsiConsole.WriteLine();

            Serilog.Log.Warning($"[{selectedOption}] L2 AI 리뷰 결함 발견 (시도 {attempt}/{maxStr}): {feedbackComment}");
        }

        public void NotifyValidationSuccess(string selectedOption)
        {
            AnsiConsole.MarkupLine($"[green]{selectedOption} - [[L1/L2 자동 검증]] 모두 통과![/]");
            Serilog.Log.Information($"[{selectedOption}] L1/L2 자동 검증 모두 통과!");
        }

        /// <summary>
        /// 단계 선택 목록에서 골격을 가리키는 항목. 매핑과 프롬프트가 같은
        /// 문자열을 써야 하므로 상수로 둔다.
        /// </summary>
        public const string SkeletonSelectionLabel = "(골격) 개요 · Mermaid 흐름도 · 검증 SQL 세트";

        /// <summary>
        /// 다중 선택 결과를 재생성 대상으로 옮긴다.
        ///
        /// 프롬프트에서 분리한 이유: AnsiConsole은 단위 테스트에서 구동하기 어렵고,
        /// 정작 틀리기 쉬운 것은 프롬프트가 아니라 이 매핑 규칙이다.
        /// </summary>
        public static (List<string> TargetStepCodes, bool RegenerateSkeleton) MapStepSelection(
            IReadOnlyList<string> selectedLabels,
            IReadOnlyList<BatchStepPlan> steps)
        {
            var regenerateSkeleton = selectedLabels.Contains(SkeletonSelectionLabel);

            // 골격을 고르면 공통 규약이 바뀌므로 그것을 인용한 섹션이 전부 낡는다.
            // 단계를 함께 골랐더라도 전체 재생성으로 승격한다.
            if (regenerateSkeleton)
            {
                return (new List<string>(), true);
            }

            var codes = steps
                .Where(step => selectedLabels.Contains(StepSelectionLabel(step)))
                .Select(step => step.Code)
                .ToList();

            return (codes, false);
        }

        private static string StepSelectionLabel(BatchStepPlan step) => $"{step.Code}  {step.Name}";

        public async Task<HumanReviewResult> RequestHumanReviewAsync(
            string selectedOption,
            string specificationMarkdown,
            VerificationOutcome outcome,
            bool structureRedraftSupported = false,
            IReadOnlyList<BatchStepPlan>? steps = null)
        {
            // 점수 필드는 여전히 문서 본문의 YAML 헤더에서 읽는다. 파이프라인 진행 중에는
            // 아직 헤더가 씌워지지 않아 항상 비어 있지만(VerificationDocumentFormatter가
            // 파이프라인 종료 후에 헤더를 붙이므로), 헤더가 이미 포함된 문자열이 들어오는
            // 호출 경로(예: 캐시 히트 재확인)에서는 여전히 유효하다.
            var header = SpecHeaderReader.Read(specificationMarkdown);
            var score = header.NormalizedScore ?? 100;
            var acc = header.Accuracy ?? 10;
            var crud = header.Crud ?? 10;
            var read = header.Readability ?? 10;
            var ex = header.Exception ?? 10;
            var scoreFound = header.NormalizedScore.HasValue;

            string scoreText = "";
            if (scoreFound)
            {
                var color = score >= 90 ? "green" : (score >= 70 ? "yellow" : "red");
                scoreText = $" | [bold {color}]AI 신뢰도: {score}/100점 (정합성:{acc}, CRUD:{crud}, 가독성:{read}, 예외:{ex})[/]";
            }

            // 검증 상태가 통과가 아니면 승인 직전에 눈에 띄어야 한다.
            // 문자열 파싱이 아니라 파이프라인이 넘겨준 실제 종료 상태(outcome)를 그대로 신뢰한다.
            var isVerified = outcome == VerificationOutcome.Passed;
            var statusText = "";
            if (!isVerified)
            {
                // 표기는 VerificationDocumentFormatter가 단독으로 소유한다. 같은 switch를
                // 복제하면 VerificationOutcome에 상태가 추가됐을 때 한 곳이 빠뜨릴 수 있고,
                // 그러면 승인 화면만 다른 말을 하게 된다.
                var statusLabel = VerificationDocumentFormatter.StatusLabel(outcome);
                statusText = $" | [bold red]검증 상태: {statusLabel}[/]";
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[yellow]{selectedOption}{scoreText}{statusText}[/]") { Justification = Justify.Left });
            AnsiConsole.Write(new Text(specificationMarkdown));
            AnsiConsole.Write(new Rule().RuleStyle("grey"));
            AnsiConsole.WriteLine();

            var menuChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(isVerified
                        ? $"[bold blue]{selectedOption} 명세서 검증 완료.[/] 다음 작업을 선택하세요:"
                        : $"[bold red]{selectedOption} 명세서가 검증을 완료하지 못했습니다.[/] 다음 작업을 선택하세요:")
                    .AddChoices(new[] { "1. 승인 및 최종 저장 (Approve)", "2. 추가 보완 요청 피드백 입력 (Feedback)", "3. 저장 없이 이탈 (Cancel)" })
            );

            if (menuChoice.StartsWith("1"))
            {
                return new HumanReviewResult { Decision = UserDecision.Approve };
            }
            if (menuChoice.StartsWith("3"))
            {
                return new HumanReviewResult { Decision = UserDecision.Cancel };
            }

            var userFeedback = AnsiConsole.Prompt(
                new TextPrompt<string>("보완할 피드백 내용을 구체적으로 기재해 주십시오:")
            );

            if (string.IsNullOrWhiteSpace(userFeedback))
            {
                AnsiConsole.MarkupLine("[yellow]피드백이 비어있어 승인 여부 선택 메뉴로 복귀합니다.[/]");
                return new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = null };
            }

            // 구조를 바꾸는 피드백은 본문만 다시 써서는 반영되지 않는다. 통합 배치
            // 계획서는 목차를 고정한 채 본문을 생성하므로 목차부터 다시 세워야 한다.
            // 반대로 목차가 없는 단일 SP 명세서 경로에서는 답을 받아도 쓸 곳이 없으므로
            // 아예 묻지 않는다 — 사용자에게 무의미한 질문을 한 번 더 던지지 않는다.
            var redraftStructure = structureRedraftSupported && AnsiConsole.Confirm(
                "이 피드백이 문서 구조(목차)까지 바꾸나요? (단계 추가/분할/순서 변경 등)", false);

            // 구조가 바뀌면 단계 목록 자체가 바뀌므로 지금 고른 단계는 의미가 없다.
            // 답을 쓸 곳이 있을 때만 묻는다 — 위 구조 질문과 같은 원칙이다.
            var targetStepCodes = new List<string>();
            var regenerateSkeleton = false;
            if (!redraftStructure && steps is { Count: > 0 })
            {
                var choices = new List<string> { SkeletonSelectionLabel };
                choices.AddRange(steps.Select(StepSelectionLabel));

                var selected = AnsiConsole.Prompt(
                    new MultiSelectionPrompt<string>()
                        .Title("어느 단계에 대한 피드백입니까? [grey](Space로 선택, Enter로 확정, 미선택 시 전체)[/]")
                        .NotRequired()
                        .PageSize(20)
                        .AddChoices(choices));

                (targetStepCodes, regenerateSkeleton) = MapStepSelection(selected, steps);
            }

            AnsiConsole.MarkupLine("[blue]사용자 피드백을 적용하여 보완 분석 프로세스를 재가동합니다...[/]");
            return new HumanReviewResult
            {
                Decision = UserDecision.ProvideFeedback,
                UserFeedback = userFeedback,
                RedraftStructure = redraftStructure,
                TargetStepCodes = targetStepCodes,
                RegenerateSkeleton = regenerateSkeleton
            };
        }

        public Task<bool> ConfirmMetadataSyncAsync(string selectedOption)
        {
            var result = AnsiConsole.Confirm($"[bold yellow]{selectedOption}[/] - AI가 보완한 설명(Extended Properties) 목록을 실제 데이터베이스에 동기화(Sync)하시겠습니까?", false);
            return Task.FromResult(result);
        }

        public IMultiProgressScope CreateProgressScope(string title)
        {
            return new ConsoleProgressScope(title);
        }
    }

    public class ConsoleProgressScope : IMultiProgressScope
    {
        private readonly Task _progressTask;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProgressTask> _tasks = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string desc, double val, bool comp, bool fail)> _pendingUpdates = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _originalDescriptions = new();
        private readonly System.Collections.Generic.List<string> _taskOrder = new();
        private readonly object _lock = new();
        private readonly TaskCompletionSource _tcs = new();
        private readonly string _title;

        public ConsoleProgressScope(string title)
        {
            _title = title;
            _progressTask = Task.Run(async () =>
            {
                await AnsiConsole.Progress()
                    .Columns(new ProgressColumn[]
                    {
                        new SpinnerColumn(Spinner.Known.Dots),
                        new TaskDescriptionColumn { Alignment = Justify.Left },
                        new ElapsedTimeColumn(),
                    })
                    .StartAsync(async ctx =>
                    {
                        while (!_tcs.Task.IsCompleted || !_pendingUpdates.IsEmpty)
                        {
                            // 1. 등록 순서대로 화면 행(Row)을 선제 생성
                            System.Collections.Generic.List<string> orderedKeys;
                            lock (_lock)
                            {
                                orderedKeys = new System.Collections.Generic.List<string>(_taskOrder);
                            }

                            foreach (var name in orderedKeys)
                            {
                                if (!_tasks.ContainsKey(name))
                                {
                                    if (_pendingUpdates.TryGetValue(name, out var item))
                                    {
                                        var task = ctx.AddTask(item.desc, autoStart: true);
                                        _tasks[name] = task;
                                    }
                                }
                            }

                            // 2. 갱신 내용 반영
                            var keys = new List<string>(_pendingUpdates.Keys);
                            foreach (var name in keys)
                            {
                                if (_pendingUpdates.TryRemove(name, out var item))
                                {
                                    var (desc, val, comp, fail) = item;
                                    if (!_tasks.TryGetValue(name, out var task))
                                    {
                                        task = ctx.AddTask(desc, autoStart: true);
                                        _tasks[name] = task;
                                    }

                                    task.Description = desc;
                                    task.Value = val;

                                    if (comp)
                                    {
                                        task.Value = 100.0;
                                        task.StopTask();
                                    }
                                    if (fail)
                                    {
                                        task.Description = $"[red]실패:[/] {desc}";
                                        task.StopTask();
                                    }
                                }
                            }

                            // API 호출 대기 중인 활성 태스크들의 모의 진척률 업데이트는
                            // 청크 단위의 실제 진척률(Value = val)과 충돌하여 프로그레스 바가 뒤로 가는 현상을 유발하므로 제거함.

                            await Task.Delay(100);
                        }
                    });
            });
        }

        public void AddTask(string taskName, string description)
        {
            lock (_lock)
            {
                if (!_taskOrder.Contains(taskName))
                {
                    _taskOrder.Add(taskName);
                }
            }
            _originalDescriptions[taskName] = description;
            _pendingUpdates[taskName] = (description, 0.0, false, false);
        }

        public void UpdateTask(string taskName, double value, string? description = null)
        {
            string desc = description ?? taskName;
            if (description == null)
            {
                if (_tasks.TryGetValue(taskName, out var task))
                    desc = task.Description;
                else if (_originalDescriptions.TryGetValue(taskName, out var orig))
                    desc = orig;
            }

            _pendingUpdates.AddOrUpdate(taskName, 
                (desc, value, false, false),
                (k, old) => (description ?? old.desc, value, old.comp, old.fail));
        }

        public void CompleteTask(string taskName)
        {
            string desc = taskName;
            if (_tasks.TryGetValue(taskName, out var task))
                desc = task.Description;
            else if (_originalDescriptions.TryGetValue(taskName, out var orig))
                desc = orig;

            _pendingUpdates.AddOrUpdate(taskName, 
                (desc, 100.0, true, false),
                (k, old) => (old.desc, 100.0, true, false));
        }

        public void FailTask(string taskName)
        {
            string desc = taskName;
            if (_tasks.TryGetValue(taskName, out var task))
                desc = task.Description;
            else if (_originalDescriptions.TryGetValue(taskName, out var orig))
                desc = orig;

            _pendingUpdates.AddOrUpdate(taskName, 
                (desc, 0.0, false, true),
                (k, old) => (old.desc, old.val, false, true));
        }

        public void Dispose()
        {
            _tcs.TrySetResult();
            _progressTask.GetAwaiter().GetResult();
        }
    }
}
