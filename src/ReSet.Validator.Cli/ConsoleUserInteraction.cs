using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Spectre.Console;
using ReSet.Validator.Core.Abstractions;
using ReSet.Validator.Core.Models;
using ValidationResult = ReSet.Validator.Core.Models.ValidationResult;
using ReSet.Core.Services;

namespace ReSet.Validator.Cli
{
    public class ConsoleUserInteraction : IValidationUserInterface
    {
        public void ShowL1Result(string specName, L1ValidationResult result)
        {
            var statusStr = result.Passed 
                ? "[green][[PASS]][/]" 
                : $"[red][[FAIL]][/] ({Markup.Escape(result.ErrorMessage)})";

            AnsiConsole.MarkupLine($"[bold]Level 1 정적 검증 결과:[/] {statusStr}");
            if (result.Passed)
            {
                AnsiConsole.MarkupLine($"  - 매핑된 클래스/메소드명: [cyan]{Markup.Escape(result.ClassOrMethodName)}[/]");
                foreach (var kvp in result.ExtractedMetadata)
                {
                    AnsiConsole.MarkupLine($"  - {Markup.Escape(kvp.Key)}: [grey]{Markup.Escape(kvp.Value)}[/]");
                }
            }
        }

        public void ShowL2Result(string specName, GapReport report)
        {
            var statusColor = report.OverallStatus switch
            {
                "MATCH" => "green",
                "PARTIAL" => "yellow",
                _ => "red"
            };

            var panel = new Panel(
                new Markup(
                    $"[bold]종합 일치도:[/] [{statusColor}]{report.OverallStatus}[/]\n\n" +
                    $"[bold]1. 입력 파라미터 Gap:[/] {Markup.Escape(string.IsNullOrEmpty(report.InputParametersGap) ? "일치" : report.InputParametersGap)}\n" +
                    $"[bold]2. 출력 데이터셋/DTO Gap:[/] {Markup.Escape(string.IsNullOrEmpty(report.OutputResultSetsGap) ? "일치" : report.OutputResultSetsGap)}\n" +
                    $"[bold]3. 비즈니스 로직 Gap:[/] {Markup.Escape(string.IsNullOrEmpty(report.BusinessLogicGap) ? "일치" : report.BusinessLogicGap)}\n" +
                    $"[bold]4. 예외/트랜잭션 Gap:[/] {Markup.Escape(string.IsNullOrEmpty(report.ExceptionHandlingGap) ? "일치" : report.ExceptionHandlingGap)}\n\n" +
                    $"[bold yellow][[코드 수정 제안 사항]][/]\n{Markup.Escape(report.Suggestions)}"
                )
            )
            {
                Header = new PanelHeader($"[bold blue]Level 2 AI 논리 검증 결과: {Markup.Escape(specName)}[/]"),
                Border = BoxBorder.Rounded
            };

            AnsiConsole.Write(panel);
        }

        public Task<bool> ConfirmValidationAsync(string specName, string codePath, GapReport? gapReport)
        {
            AnsiConsole.WriteLine();
            var prompt = new ConfirmationPrompt($"[bold yellow]'{Markup.Escape(specName)}'의 코드 구현을 최종 승인(Approve)하시겠습니까?[/]");
            bool confirmed = AnsiConsole.Prompt(prompt);
            return Task.FromResult(confirmed);
        }

        public Task<string> PromptFeedbackAsync(string specName)
        {
            var feedback = AnsiConsole.Ask<string>("[bold red]불승인 사유 및 수정 사항 피드백을 입력해 주세요:[/] ");
            return Task.FromResult(feedback);
        }

        public string PromptDirectoryPath(string promptMessage, string defaultPath, List<string> choices)
        {
            var prompt = new TextPrompt<string>($"[bold green]{Markup.Escape(promptMessage)}[/]")
                .DefaultValue(defaultPath)
                .AddChoices(choices) // 탭 자동완성을 위한 후보군 리스트 등록
                .ShowChoices(false)  // 슬래시로 엮여 지저분하게 출력되는 선택지 화면 노출 방지
                .Validate(path =>
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return Spectre.Console.ValidationResult.Error("[red]경로를 입력해야 합니다.[/]");
                    }
                    var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);
                    if (!Directory.Exists(fullPath))
                    {
                        return Spectre.Console.ValidationResult.Error($"[red]입력하신 디렉토리가 존재하지 않습니다: {Markup.Escape(fullPath)}[/]");
                    }
                    return Spectre.Console.ValidationResult.Success();
                });

            var chosen = AnsiConsole.Prompt(prompt);
            return Path.IsPathRooted(chosen) ? chosen : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), chosen));
        }

        public void ShowSummary(List<ValidationResult> results)
        {
            AnsiConsole.WriteLine();
            var table = new Table()
                .Title("[bold white][최종 마일스톤 검증 요약 보고서][/]")
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]검증 대상[/]")
                .AddColumn("[bold]L1 정적 검증[/]")
                .AddColumn("[bold]L2 AI 의미 검증[/]")
                .AddColumn("[bold]L3 개발자 승인[/]")
                .AddColumn("[bold]상태[/]");

            foreach (var r in results)
            {
                var l1 = r.L1Passed ? "[green][[PASS]][/]" : "[red][[FAIL]][/]";
                var l2 = r.L2Passed ? "[green][[MATCH]][/]" : "[yellow][[GAP]][/]";
                var l3 = r.IsApproved ? "[green][[APPROVED]][/]" : "[red][[REJECTED]][/]";
                
                var displayStatus = r.IsApproved 
                    ? "[green]Approved[/]" 
                    : (r.L1Passed ? "[yellow]Needs Modification[/]" : "[red]Structure Error[/]");

                table.AddRow(
                    Markup.Escape(r.MappedName),
                    l1,
                    l2,
                    l3,
                    displayStatus
                );
            }

            AnsiConsole.Write(table);
        }

        public void ShowWarning(string message)
        {
            AnsiConsole.MarkupLine($"[bold yellow]경고: {Markup.Escape(message)}[/]");
        }

        public void ShowInfo(string message)
        {
            AnsiConsole.MarkupLine($"[blue]{Markup.Escape(message)}[/]");
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
