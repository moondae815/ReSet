using System.Collections.Generic;
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

            Serilog.Log.Warning($"[{selectedOption}] L2 AI 리뷰 결함 발견 (시도 {attempt}/{maxStr}): {feedbackComment}");
        }

        public void NotifyValidationSuccess(string selectedOption)
        {
            AnsiConsole.MarkupLine($"[green]{selectedOption} - [[L1/L2 자동 검증]] 모두 통과![/]");
            Serilog.Log.Information($"[{selectedOption}] L1/L2 자동 검증 모두 통과!");
        }

        public async Task<HumanReviewResult> RequestHumanReviewAsync(string selectedOption, string specificationMarkdown)
        {
            int score = 100;
            int acc = 10, crud = 10, read = 10, ex = 10;
            bool scoreFound = false;

            // YAML Front Matter에서 점수 추출 시도
            if (specificationMarkdown.StartsWith("---"))
            {
                var endOfYaml = specificationMarkdown.IndexOf("---", 3);
                if (endOfYaml > 0)
                {
                    var yamlBlock = specificationMarkdown.Substring(3, endOfYaml - 3);
                    var lines = yamlBlock.Split('\n');
                    foreach (var line in lines)
                    {
                        var parts = line.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim();
                            var val = parts[1].Trim();
                            
                            // # 주석 제거
                            var commentIdx = val.IndexOf('#');
                            if (commentIdx >= 0)
                            {
                                val = val.Substring(0, commentIdx).Trim();
                            }

                            // 괄호 설명 제거 (예: "95 (종합 신뢰도)" -> "95")
                            var parenIdx = val.IndexOf('(');
                            if (parenIdx >= 0)
                            {
                                val = val.Substring(0, parenIdx).Trim();
                            }

                            // '/'가 포함된 경우 첫 번째 부분 추출 (예: "9/10" -> "9")
                            var slashIdx = val.IndexOf('/');
                            var numberPart = slashIdx >= 0 ? val.Substring(0, slashIdx).Trim() : val;

                            if ((key == "AiConfidenceScore" || key == "종합 신뢰도 점수" || key == "종합 신뢰도" || key == "종합신뢰도") && int.TryParse(numberPart, out int scoreVal)) { score = scoreVal; scoreFound = true; }
                            else if ((key == "AccuracyScore" || key == "정합성 점수" || key == "정합성") && int.TryParse(numberPart, out int accVal)) acc = accVal;
                            else if ((key == "CrudScore" || key == "CRUD 점수" || key == "CRUD") && int.TryParse(numberPart, out int crudVal)) crud = crudVal;
                            else if ((key == "ReadabilityScore" || key == "가독성 점수" || key == "가독성") && int.TryParse(numberPart, out int readVal)) read = readVal;
                            else if ((key == "ExceptionScore" || key == "예외처리 점수" || key == "예외처리" || key == "예외 처리 점수" || key == "예외 처리") && int.TryParse(numberPart, out int exVal)) ex = exVal;
                        }
                    }
                }
            }

            string scoreText = "";
            if (scoreFound)
            {
                var color = score >= 90 ? "green" : (score >= 70 ? "yellow" : "red");
                scoreText = $" | [bold {color}]AI 신뢰도: {score}/100점 (정합성:{acc}, CRUD:{crud}, 가독성:{read}, 예외:{ex})[/]";
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[yellow]{selectedOption}{scoreText}[/]") { Justification = Justify.Left });
            AnsiConsole.Write(new Text(specificationMarkdown));
            AnsiConsole.Write(new Rule().RuleStyle("grey"));
            AnsiConsole.WriteLine();

            var menuChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[bold blue]{selectedOption} 명세서 검증 완료.[/] 다음 작업을 선택하세요:")
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

            AnsiConsole.MarkupLine("[blue]사용자 피드백을 적용하여 보완 분석 프로세스를 재가동합니다...[/]");
            return new HumanReviewResult
            {
                Decision = UserDecision.ProvideFeedback,
                UserFeedback = userFeedback
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
                        new TaskDescriptionColumn(),
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

                            // API 호출 대기 중인 활성 태스크들의 모의 진척률(Simulated Progress) 업데이트
                            foreach (var kvp in _tasks)
                            {
                                var task = kvp.Value;
                                if (!task.IsFinished && task.Value < 95.0)
                                {
                                    // 95%까지 매 100ms마다 남은 간격의 2%씩 점진적 증가
                                    var increment = (95.0 - task.Value) * 0.02;
                                    task.Increment(increment);
                                }
                            }

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
            _pendingUpdates[taskName] = (description, 0.0, false, false);
        }

        public void UpdateTask(string taskName, double value, string? description = null)
        {
            _pendingUpdates.AddOrUpdate(taskName, 
                (description ?? taskName, value, false, false),
                (k, old) => (description ?? old.desc, value, old.comp, old.fail));
        }

        public void CompleteTask(string taskName)
        {
            _pendingUpdates.AddOrUpdate(taskName, 
                (taskName, 100.0, true, false),
                (k, old) => (old.desc, 100.0, true, false));
        }

        public void FailTask(string taskName)
        {
            _pendingUpdates.AddOrUpdate(taskName, 
                (taskName, 0.0, false, true),
                (k, old) => (old.desc, old.val, false, true));
        }

        public void Dispose()
        {
            _tcs.TrySetResult();
            _progressTask.GetAwaiter().GetResult();
        }
    }
}
