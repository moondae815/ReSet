using System.Collections.Generic;
using System.Threading.Tasks;
using Spectre.Console;
using ReSet.Validator.Core.Abstractions;
using ReSet.Validator.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Cli
{
    public class ValidationUiProxy : IValidationUserInterface
    {
        public void ShowInfo(string message) => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
        public void ShowWarning(string message) => AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
        public void ShowSummary(List<ReSet.Validator.Core.Models.ValidationResult> results) {}
        public void ShowL1Result(string specName, L1ValidationResult result) {}
        public void ShowL2Result(string specName, GapReport report) {}
        public Task<bool> ConfirmValidationAsync(string specName, string codePath, GapReport? gapReport) => Task.FromResult(true);
        public Task<string> PromptFeedbackAsync(string specName) => Task.FromResult("");
        public string PromptDirectoryPath(string promptMessage, string defaultPath, List<string> choices) => defaultPath;
        
        public IMultiProgressScope CreateProgressScope(string title)
        {
            // 간단하게 NullProgressScope 대신 콘솔에 출력하는 용도
            AnsiConsole.MarkupLine($"[cyan]▶ {Markup.Escape(title)}[/]");
            return NullProgressScope.Instance;
        }
    }
}
