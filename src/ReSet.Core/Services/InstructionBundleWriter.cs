using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public sealed record BundleInputs(
        string JobName,
        string TargetLanguage,
        VerificationOutcome PlanOutcome,
        string FinalPlanMarkdown,
        PlanLayout? Layout,
        IReadOnlyList<SpDefinition> SpDefs,
        OutputPathResolver Paths,
        string JobOutputDir,
        // 기본값을 두지 않는다 - MetadataExporter가 Coverage를 받아 놓고 여기로
        // 넘기지 않는 실수가 실제로 있었다. 그때는 이 값이 조용히 null로 떨어져
        // 빌드도 깨끗하고 테스트도 전부 통과했으며, §0은 매 실행마다 초록 ✅만
        // 찍었다 - 리뷰에서만 발견됐다. 생성자를 이 자리 하나(MetadataExporter.cs)로
        // 묶어 두는 대신, 기본값을 없애 컴파일러가 이 연결을 영구히 강제하게 한다.
        VerificationCoverage? Coverage);

    /// <param name="StepCodes">실제로 파일이 쓰인 단계 코드(파일명에 쓰인 <b>정화된</b> 값).
    /// 회차 정의의 근거가 되며, steps/&lt;코드&gt;.md와 task-NN-&lt;코드&gt;.md가 같은 값을 쓴다.</param>
    /// <param name="TaskFilePaths">회차 순서대로의 작업 지시서 절대 경로. 회차 정의의 근거.</param>
    public sealed record BundleResult(
        string EntryPointPath,
        IReadOnlyList<string> StepCodes,
        IReadOnlyList<string> Warnings,
        bool StepsSplit,
        IReadOnlyList<string> TaskFilePaths);

    /// <summary>
    /// 코딩 에이전트에 넘길 `agent/` 번들을 디스크에 쓴다.
    ///
    /// 이전에는 계획서 전문(7,661줄)을 진입점 한 파일에 인라인했다. 그러면 에이전트가
    /// 읽어야 할 입력이 253k 토큰이 되어 코드를 쓰기 전에 컨텍스트가 찬다. 여기서
    /// 계획을 파일로 나누고, 진입점은 인덱스만 남긴다.
    /// </summary>
    public sealed class InstructionBundleWriter
    {
        public async Task<BundleResult> WriteAsync(BundleInputs inputs, CancellationToken cancellationToken = default)
        {
            var agentDir = Path.Combine(inputs.JobOutputDir, "agent");
            Directory.CreateDirectory(agentDir);

            // agent/ 직하의 파일(진입점 자신, 이후 태스크가 쓸 task-*.md·todo.md·progress.json,
            // 코딩 에이전트가 만든 산출물)은 이 메서드가 정리하지 않는다. 여기서 지우는 것은
            // 이 클래스가 전적으로 새로 쓰는 하위 디렉터리(common/01, verification/, steps/)
            // 뿐이다 - agentDir을 통째로 비우거나 재생성하면 지시서를 다시 쓰는 것만으로
            // 진행 상태와 에이전트 산출물이 함께 사라진다.
            var slices = PlanBoundaryResolver.Resolve(inputs.FinalPlanMarkdown, inputs.Layout);
            var warnings = new List<string>(slices.Warnings);

            var commonDir = Path.Combine(agentDir, "common");
            Directory.CreateDirectory(commonDir);

            await WriteAsync(Path.Combine(commonDir, "00-architecture.md"), slices.Architecture, cancellationToken);

            var stepContractPath = Path.Combine(commonDir, "01-step-contract.md");
            if (slices.StepContract != null)
            {
                await WriteAsync(stepContractPath, slices.StepContract, cancellationToken);
            }
            else if (File.Exists(stepContractPath))
            {
                // 이번 회차는 공통 규약을 잘라내지 못했다. 이전 실행이 남긴 파일을 그대로
                // 두면 진입점 인덱스(HasStepContract=false, 링크 없음)와 디스크 상태가
                // 어긋나 에이전트가 인덱스에 없는 오래된 파일을 몰래 읽을 길이 열린다.
                File.Delete(stepContractPath);
                Log.Information("이전 실행의 공통 규약 파일을 정리했습니다 - Path: {Path}", stepContractPath);
            }

            // 경계 규칙은 계획서가 아니라 DataAccessPolicy에서 온다. 계획 분할이 실패해도
            // 이 파일은 언제나 존재한다 - 규칙 없이 코드를 쓰게 두지 않는다.
            await WriteAsync(
                Path.Combine(commonDir, "02-data-access-boundary.md"),
                "# 데이터 액세스 경계 규칙\n\n" + DataAccessPolicy.InstructionRules(inputs.TargetLanguage),
                cancellationToken);

            // 배치 호스팅(Worker Service/Spring Batch)과 멀티 DB 연결 문자열 안내는
            // 예전에는 진입점 §5에 있었다가 Phase B 분할 때 어디에도 옮겨지지 않고
            // 유실됐다. 스캐폴딩을 세우는 것은 Bootstrap 회차의 일이므로 그 회차만
            // 이 파일을 가리킨다(TaskFileComposer.AppendBootstrap) - 매 회차가 공통으로
            // 읽는 00~02와 달리 이 파일은 Bootstrap 전용이라 진입점 인덱스에는 넣지 않는다.
            await WriteAsync(
                Path.Combine(commonDir, "03-hosting-and-config.md"),
                BuildHostingAndConfigMarkdown(inputs.TargetLanguage),
                cancellationToken);

            var verificationDir = Path.Combine(agentDir, "verification");
            if (slices.Verification != null)
            {
                Directory.CreateDirectory(verificationDir);
                await WriteAsync(
                    Path.Combine(verificationDir, "integrity-sql.md"), slices.Verification, cancellationToken);
            }
            else if (Directory.Exists(verificationDir))
            {
                // verification/ 아래에는 integrity-sql.md 하나만 존재한다. 이번 회차가
                // 검증 SQL을 잘라내지 못했으면 디렉터리째 지운다 - 비워 두면 "이전에는
                // 검증 SQL이 있었는데 이번엔 없다"는 사실이 조용히 사라진다.
                var staleVerificationFiles = Directory.GetFiles(verificationDir, "*", SearchOption.AllDirectories).Length;
                Directory.Delete(verificationDir, recursive: true);
                Log.Information(
                    "이전 실행의 검증 SQL 파일을 정리했습니다 - 대상: {Count}개", staleVerificationFiles);
            }

            // 원본 코드(계획서 헤딩·본문에 그대로 나타나는 표시용)와 정화 코드(파일명·
            // 검증 매핑용)를 짝으로 들고 다닌다. 두 값이 필요한 자리가 다르다 - 지시서
            // 본문은 에이전트가 계획서에서 찾을 수 있어야 하므로 원본이어야 하고,
            // 파일명과 소스 매칭 접두사는 파일 시스템이 받아 주는 값이어야 한다.
            var stepPairs = new List<(string Raw, string Safe)>();
            var stepCodes = new List<string>();
            var stepIndex = new List<IndexEntry>();
            var stepsDir = Path.Combine(agentDir, "steps");

            // 정화가 서로 다른 두 코드를 같은 이름으로 뭉갤 수 있다(예: "S01 "과 "S01").
            // 그러면 한 단계의 상세가 다른 단계의 파일을 소리 없이 덮어쓰고, 두 회차가
            // 같은 소스 파일을 상대로 게이트를 통과한다. 부분 분할을 하지 않는다는 규칙과
            // 같은 이유로 그때는 분할 자체를 포기하고 단일 파일 폴백으로 내려간다.
            var stepsSplit = slices.StepsSplit;
            if (stepsSplit)
            {
                foreach (var code in OrderedStepCodes(inputs.Layout, slices.Steps))
                {
                    stepPairs.Add((code, TaskFileComposer.SanitizeStepCode(code)));
                }

                var distinctSafe = new HashSet<string>(
                    stepPairs.Select(pair => pair.Safe), StringComparer.OrdinalIgnoreCase);

                if (distinctSafe.Count != stepPairs.Count)
                {
                    var warning =
                        "단계 코드를 파일명으로 정화하면 서로 다른 단계가 같은 이름이 됩니다. " +
                        "부분 분할 대신 계획서를 단일 파일로 유지합니다.";
                    warnings.Add(warning);
                    Log.Error(
                        "{Warning} - 코드: {Codes}",
                        warning, string.Join(", ", stepPairs.Select(pair => $"{pair.Raw}→{pair.Safe}")));

                    stepPairs.Clear();
                    stepsSplit = false;
                }
            }

            if (stepsSplit)
            {
                Directory.CreateDirectory(stepsDir);

                // 단계 집합이 이전 실행보다 줄었을 수 있다(예: S01-S03 → S01-S02). 이번
                // 목차에 없는 파일을 먼저 지워야, 사라진 단계의 낡은 지침이 --add-dir로
                // 스코프된 에이전트에게 계속 보이는 일이 없다.
                CleanupStaleStepFiles(stepsDir, stepPairs.Select(pair => pair.Safe));

                foreach (var (raw, safe) in stepPairs)
                {
                    var body = slices.Steps[raw];
                    var banner = BuildFloorBanner(inputs.Layout, raw);
                    await WriteAsync(
                        Path.Combine(stepsDir, $"{safe}.md"), banner + body, cancellationToken);

                    stepCodes.Add(safe);
                    stepIndex.Add(new IndexEntry(DescribeStep(inputs.Layout, raw), $"steps/{safe}.md"));
                }
            }
            else if (Directory.Exists(stepsDir))
            {
                // 이번 회차는 폴백이라 steps/ 전체가 무효하다. 지우지 않으면 진입점은
                // 단일 파일을 가리키는데 steps/ 아래 이전 회차 파일이 그대로 남아, 그
                // 파일들만 보고 작업을 시작하는 에이전트가 생길 수 있다.
                var staleStepFiles = Directory.GetFiles(stepsDir, "*.md").Length;
                Directory.Delete(stepsDir, recursive: true);
                Log.Information(
                    "이전 실행의 단계 파일을 정리했습니다 - 폴백 전환으로 전체 삭제, 대상: {Count}개", staleStepFiles);
            }

            var dependencies = await WriteDependencySchemasAsync(inputs, agentDir, cancellationToken);
            var specs = BuildSpecIndex(inputs, agentDir);

            // 진입점과 task-*.md 양쪽이 같은 폴백 경로를 가리켜야 하므로 한 번만 계산한다.
            var singlePlanRelative = stepsSplit
                ? null
                : RelativeToAgent(agentDir, Path.Combine(inputs.JobOutputDir, "docs", "BatchMigrationPlan.md"));

            var entryPoint = InstructionEntryPointComposer.Compose(new EntryPointInputs(
                inputs.JobName,
                inputs.TargetLanguage,
                inputs.PlanOutcome,
                slices.Preamble,
                stepsSplit,
                stepIndex,
                dependencies,
                specs,
                HasStepContract: slices.StepContract != null,
                HasVerification: slices.Verification != null,
                SinglePlanRelativePath: singlePlanRelative,
                // 커버리지는 파이프라인이 한 번 계산해 넘긴 값을 그대로 쓴다.
                // 여기서 다시 세면 헤더와 §0이 서로 다른 수를 말하게 된다.
                Coverage: inputs.Coverage));

            var entryPointPath = Path.Combine(agentDir, "MigrationInstructions.md");
            await WriteAsync(entryPointPath, entryPoint, cancellationToken);

            // 회차 전환은 코딩 엔진에 다른 task-*.md 경로를 넘기는 것으로 끝난다.
            // 여기서 회차 0(부트스트랩)·단계별·회차 99(조립)까지 한 벌을 미리 써 둔다.
            var taskFiles = new List<string>();

            async Task WriteTaskAsync(
                StageKind kind, int ordinal, string? code, string? safeCode, string? name, string? specRelative)
            {
                // 부트스트랩·조립 회차는 특정 단계에 매인 스키마가 없다 - 작업 전체
                // 스키마를 붙이면 "단계 상세 문서를 읽지 마십시오"(부트스트랩)와
                // 모순된다. Step 회차만 그 단계의 SchemaTables로 좁힌다.
                var stepDependencies = kind == StageKind.Step && code != null
                    ? DependenciesForStep(dependencies, inputs.Layout, code)
                    : Array.Empty<IndexEntry>();

                var taskInputs = new TaskFileInputs(
                    Kind: kind,
                    JobName: inputs.JobName,
                    TargetLanguage: inputs.TargetLanguage,
                    StepCode: code,
                    StepName: name,
                    StepRelativePath: safeCode != null && stepsSplit ? $"steps/{safeCode}.md" : null,
                    SpecRelativePath: specRelative,
                    Dependencies: stepDependencies,
                    HasStepContract: slices.StepContract != null,
                    HasVerification: slices.Verification != null,
                    // 회차 실행 전이므로 실패 단계는 아직 없다. 오케스트레이터가
                    // 조립 회차 직전에 이 파일을 다시 쓴다(Task 13).
                    FailedStepCodes: Array.Empty<string>(),
                    SinglePlanRelativePath: singlePlanRelative);

                var path = Path.Combine(agentDir, TaskFileComposer.FileName(kind, ordinal, code));
                await WriteAsync(path, TaskFileComposer.Compose(taskInputs), cancellationToken);
                taskFiles.Add(path);
            }

            await WriteTaskAsync(StageKind.Bootstrap, 0, null, null, null, null);

            var ordinal = 1;
            foreach (var (raw, safe) in stepPairs)
            {
                await WriteTaskAsync(
                    StageKind.Step, ordinal, raw, safe,
                    DescribeStep(inputs.Layout, raw), SpecPathForStep(inputs, agentDir, raw));
                ordinal++;
            }

            await WriteTaskAsync(StageKind.Assembly, 99, null, null, null, null);

            Log.Information(
                "지시서 번들을 작성했습니다 - Job: {JobName}, 단계 분할: {StepsSplit}, 단계 수: {StepCount}개, 경고: {WarningCount}건",
                inputs.JobName, stepsSplit, stepCodes.Count, warnings.Count);

            return new BundleResult(entryPointPath, stepCodes, warnings, stepsSplit, taskFiles);
        }

        /// <summary>
        /// 목차가 선언한 순서를 따른다. 사전 순으로 정렬하면 S10이 S2 앞에 오는 식으로
        /// 회차 순서가 실행 의존성과 어긋난다.
        /// </summary>
        private static IReadOnlyList<string> OrderedStepCodes(
            PlanLayout? layout, IReadOnlyDictionary<string, string> steps)
        {
            if (layout?.Steps is { Count: > 0 })
            {
                var ordered = layout.Steps
                    .Select(step => step.Code)
                    .Where(steps.ContainsKey)
                    .ToList();

                if (ordered.Count == steps.Count)
                {
                    return ordered;
                }
            }

            return steps.Keys.ToList();
        }

        private static string DescribeStep(PlanLayout? layout, string code)
        {
            var name = layout?.Steps?.FirstOrDefault(step =>
                string.Equals(step.Code, code, StringComparison.OrdinalIgnoreCase))?.Name;

            return string.IsNullOrWhiteSpace(name) ? code : $"{code} {name}";
        }

        /// <summary>
        /// 하한 미달 기록이 있는 단계에만 배너를 붙인다. 이전에는 문서 전체 상단에
        /// 배너 하나만 있어 어느 단계가 부실한지 에이전트가 알 수 없었다.
        ///
        /// Kind에 따라 문구를 가른다. QualityFloor는 섹션이 실제로 최소 요건을
        /// 못 채운 경우이고, Unverifiable은 섹션은 멀쩡할 수 있는데 대조할
        /// 재료(대상 테이블·원본 오류코드)가 목차에 없어 검사 자체가 못 돈
        /// 경우다 - 후자에 "부실하다"거나 "원본 명세서를 확인하라"는 문구를
        /// 붙이면 근거 없는 지시가 된다.
        /// </summary>
        private static string BuildFloorBanner(PlanLayout? layout, string code)
        {
            if (layout?.FloorViolations == null ||
                !layout.FloorViolations.TryGetValue(code, out var defect) ||
                string.IsNullOrWhiteSpace(defect.Reason))
            {
                return string.Empty;
            }

            var (headline, tail) = defect.Kind switch
            {
                StepDefectKind.Unverifiable => (
                    "> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**",
                    "> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다."),
                _ => (
                    "> ⚠️ **이 단계는 품질 미달로 기록되었습니다.**",
                    "> 이 절만으로 구현이 불가능하면 추측하지 말고 원본 명세서(Spec.md)를 확인하십시오."),
            };

            var sb = new StringBuilder();
            sb.AppendLine(headline);
            sb.AppendLine("> ");
            sb.AppendLine($"> {defect.Reason.Trim()}");
            sb.AppendLine("> ");
            sb.AppendLine(tail);
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>
        /// 이전 실행이 남긴 단계 파일 중 이번 목차에 없는 것을 지운다. 목차 앵커로만
        /// 쓰는 규칙과 마찬가지로, 무엇을 지울지도 언제나 이번 회차의 최종 결과
        /// (<paramref name="currentCodes"/>)를 기준으로 판단한다.
        /// </summary>
        private static void CleanupStaleStepFiles(string stepsDir, IEnumerable<string> currentCodes)
        {
            var keep = new HashSet<string>(currentCodes, StringComparer.OrdinalIgnoreCase);
            var removed = 0;

            foreach (var file in Directory.GetFiles(stepsDir, "*.md"))
            {
                if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
                {
                    File.Delete(file);
                    removed++;
                }
            }

            if (removed > 0)
            {
                Log.Information("이전 실행의 단계 파일을 정리했습니다 - 현재 목차에 없는 파일 삭제, 대상: {Count}개", removed);
            }
        }

        /// <summary>
        /// 단계 하나가 실제로 건드리는 테이블 스키마로 의존성 목록을 좁힌다.
        ///
        /// 좁히지 않으면 Job 전체의 의존성이 모든 회차의 지시서에 실린다 - S01
        /// 지시서가 S02만 건드리는 테이블의 DDL까지 가리키는 식이다. "한 회차의
        /// 지시서는 그 회차가 읽어야 할 것만 가리켜야 한다"는 이 작업의 목적과
        /// 정면으로 어긋난다.
        ///
        /// 목차가 없거나(레이아웃 폴백) 이 단계의 SchemaTables를 특정할 수 없거나,
        /// 특정했는데도 일치하는 의존성이 하나도 없으면 전체 목록으로 떨어뜨리고
        /// 경고를 남긴다. 빈 목록을 조용히 내보내면 "이 단계는 스키마를 안 쓴다"와
        /// "일치 규칙이 틀렸다"를 구분할 방법이 없어진다 - 데이터 액세스 코드를
        /// 쓰다가 필요한 테이블의 컬럼 정의를 찾지 못하는 쪽이, 몇 개 더 실리는
        /// 쪽보다 훨씬 나쁘다.
        ///
        /// 스코프의 원천이 TargetTables가 아니라 SchemaTables인 이유: 앞은 쓰기 대상만
        /// 담는 검증 재료라, 그것으로 좁히면 에이전트가 SELECT를 쓸 원본 테이블의 컬럼
        /// 정의를 받지 못한다.
        /// </summary>
        private static IReadOnlyList<IndexEntry> DependenciesForStep(
            IReadOnlyList<IndexEntry> dependencies, PlanLayout? layout, string stepCode)
        {
            var step = layout?.Steps?.FirstOrDefault(s =>
                string.Equals(s.Code, stepCode, StringComparison.OrdinalIgnoreCase));
            if (step == null)
            {
                return dependencies;
            }

            // 목차가 스키마 테이블을 안 냈으면 좁힐 근거가 없어 전체를 준다. 아래
            // matched.Count == 0 폴백과 결과가 같으므로 관측성도 같아야 한다 -
            // 여기만 조용하면 그 회차 하나가 Job 전체 스키마를 받은 사실이
            // "경고 0건"에 묻힌다.
            if (step.SchemaTables.Count == 0)
            {
                Log.Warning(
                    "단계의 목차 SchemaTables가 비어 있어 의존성 스키마를 좁히지 못하고 전체 목록으로 대체합니다 - " +
                    "Step: {StepCode}, 스키마 수: {Count}개",
                    stepCode, dependencies.Count);
                return dependencies;
            }

            var matched = dependencies
                .Where(dep => step.SchemaTables.Any(target => TableTokensMatch(dep.Label, target)))
                .ToList();

            if (matched.Count == 0)
            {
                Log.Warning(
                    "단계의 SchemaTables와 일치하는 의존성 스키마를 찾지 못해 전체 목록으로 대체합니다 - " +
                    "Step: {StepCode}, SchemaTables: {SchemaTables}",
                    stepCode, string.Join(", ", step.SchemaTables));
                return dependencies;
            }

            return matched;
        }

        /// <summary>
        /// 테이블 식별자 두 개(의존성 라벨 "dbo.TClient" 또는 "SettleDB.dbo.TClient",
        /// 목차의 SchemaTables 표기 "dbo.Ledger"·"[dbo].[Ledger]"·"Ledger")가 같은
        /// 테이블을 가리키는지 비교한다.
        ///
        /// 대괄호를 벗기고 마지막 두 조각(스키마.테이블)만 본다 - 의존성 라벨은
        /// Database가 있으면 3조각, 없으면 2조각이라 위치가 고정돼 있지 않다.
        /// 양쪽 다 스키마 표기가 있으면 스키마까지 일치해야 하고, 한쪽이라도
        /// 스키마를 안 적었으면(목차가 테이블명만 쓴 경우) 테이블명만으로 판단한다 -
        /// 스키마가 없다고 매칭을 포기하면 "일치 없음"과 "표기 생략"을 구분할 수
        /// 없는 문제가 그대로 반복된다.
        /// </summary>
        private static bool TableTokensMatch(string dependencyLabel, string targetTable)
        {
            var (depSchema, depTable) = ParseTableToken(dependencyLabel);
            var (targetSchema, targetTableName) = ParseTableToken(targetTable);

            if (depTable.Length == 0 || !string.Equals(depTable, targetTableName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (depSchema != null && targetSchema != null)
            {
                return string.Equals(depSchema, targetSchema, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static (string? Schema, string Table) ParseTableToken(string raw)
        {
            var cleaned = raw.Replace("[", string.Empty).Replace("]", string.Empty).Trim();
            var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);

            return parts.Length switch
            {
                0 => (null, string.Empty),
                1 => (null, parts[0]),
                // 2조각(스키마.테이블)이든 3조각(DB.스키마.테이블)이든 뒤에서
                // 두 조각을 스키마.테이블로 본다.
                _ => (parts[^2], parts[^1]),
            };
        }

        /// <summary>
        /// 단계가 유래한 레거시 프로시저의 Spec.md 경로. 목차의 LegacyProcedures가
        /// 그 대응을 갖고 있으므로 이름 추측을 하지 않는다. 찾지 못하면 null이며,
        /// 그때 작업 지시서는 명세서 링크 없이 단계 상세만 가리킨다.
        /// </summary>
        private static string? SpecPathForStep(BundleInputs inputs, string agentDir, string stepCode)
        {
            var step = inputs.Layout?.Steps?.FirstOrDefault(s =>
                string.Equals(s.Code, stepCode, StringComparison.OrdinalIgnoreCase));
            if (step == null || step.LegacyProcedures.Count == 0)
            {
                return null;
            }

            foreach (var procedure in step.LegacyProcedures)
            {
                var bare = procedure.Contains('.') ? procedure[(procedure.LastIndexOf('.') + 1)..] : procedure;

                var spDef = inputs.SpDefs.FirstOrDefault(sp =>
                    string.Equals(sp.Name, bare, StringComparison.OrdinalIgnoreCase));
                if (spDef == null)
                {
                    continue;
                }

                var objectKey = spDef.ObjectKey ?? CodeObjectKey.Create(
                    inputs.Paths.CurrentDatabase, spDef.Schema, spDef.Name, CodeObjectType.Procedure);
                var specPath = inputs.Paths.ResolveSpecPath(objectKey);

                if (File.Exists(specPath))
                {
                    return RelativeToAgent(agentDir, specPath);
                }
            }

            return null;
        }

        private static async Task<List<IndexEntry>> WriteDependencySchemasAsync(
            BundleInputs inputs, string agentDir, CancellationToken cancellationToken)
        {
            var rawDdlDir = Path.Combine(inputs.JobOutputDir, "raw", "ddl");
            Directory.CreateDirectory(rawDdlDir);

            var distinct = inputs.SpDefs
                .SelectMany(sp => sp.Dependencies)
                .GroupBy(d => $"{d.Database}.{d.Schema}.{d.Name}")
                .Select(g => g.First())
                .ToList();

            var entries = new List<IndexEntry>();

            foreach (var dep in distinct)
            {
                var cleanName = string.IsNullOrEmpty(dep.Database)
                    ? $"{dep.Schema}.{dep.Name}"
                    : $"{dep.Database}.{dep.Schema}.{dep.Name}";

                var filePath = Path.Combine(rawDdlDir, $"{cleanName}.md");
                var sb = new StringBuilder();
                sb.AppendLine($"# {dep.Type}: {cleanName}");
                sb.AppendLine();

                if (dep.Columns.Count > 0)
                {
                    sb.AppendLine(MetadataExporter.FormatTableSchemaToMarkdown(dep));
                }

                if (!string.IsNullOrEmpty(dep.ReferencedDdlText))
                {
                    sb.AppendLine("## Referenced SQL DDL:");
                    sb.AppendLine("```sql");
                    sb.AppendLine(dep.ReferencedDdlText);
                    sb.AppendLine("```");
                }

                await WriteAsync(filePath, sb.ToString(), cancellationToken);
                entries.Add(new IndexEntry(cleanName, RelativeToAgent(agentDir, filePath)));
            }

            return entries;
        }

        /// <summary>
        /// 배치 호스팅/DI와 멀티 DB 연결 문자열 안내. 문구는 구 MetadataExporter가
        /// 진입점 §5에 심던 것을 글자 그대로 옮겼다(96ad2d7^의 MetadataExporter.cs) -
        /// 리뷰에서 다시 쓰지 말고 원문을 그대로 가져오라고 확정됐다. DataAccessPolicy의
        /// SQL/ORM 경계 규칙과는 다른 관심사라 그 클래스에는 두지 않는다.
        /// 알 수 없는 언어는 기존 §5와 마찬가지로 언어별 블록 없이 헤더만 남긴다.
        /// </summary>
        private static string BuildHostingAndConfigMarkdown(string targetLanguage)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 배치 호스팅 및 인프라 설정 가이드");
            sb.AppendLine();

            if (targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("* **배치 호스팅 및 DI**: 배치 호스팅은 .NET 10 Worker Service 기반으로 작성하며, Microsoft.Extensions.DependencyInjection을 통해 의존성을 주입하십시오.");
                sb.AppendLine("* **멀티 DB 커넥션 설정**: `appsettings.json` 내에 다음과 같은 `ConnectionStrings` 구조를 구성하고, `RetryableSqlExecutor`에서 분기 처리하여 주입받을 수 있도록 모델링하십시오.");
                sb.AppendLine("  ```json");
                sb.AppendLine("  {");
                sb.AppendLine("    \"ConnectionStrings\": {");
                sb.AppendLine("      \"PaymentDB\": \"Server=...;Database=PaymentDB;...\",");
                sb.AppendLine("      \"SettleCardDB\": \"Server=...;Database=SETTLE_CARD_DB;...\",");
                sb.AppendLine("      \"PLCardDB\": \"Server=...;Database=PLCardDB;...\",");
                sb.AppendLine("      \"SettlePoqDB\": \"Server=...;Database=SETTLE_POQ_DB;...\"");
                sb.AppendLine("    }");
                sb.AppendLine("  }");
                sb.AppendLine("  ```");
            }
            else if (targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("* **배치 호스팅 및 DI**: 배치 호스팅은 Spring Batch (Spring Boot 기반)로 작성하며, 의존성 주입을 활용하십시오.");
                sb.AppendLine("* **멀티 DB 커넥션 설정**: `application.yml` 내에 다음과 같은 다중 DataSource 구조를 구성하고, 각 Step이 알맞은 TransactionManager와 JdbcTemplate을 주입받을 수 있도록 모델링하십시오.");
                sb.AppendLine("  ```yaml");
                sb.AppendLine("  spring:");
                sb.AppendLine("    datasource:");
                sb.AppendLine("      payment:");
                sb.AppendLine("        jdbc-url: jdbc:sqlserver://...;databaseName=PaymentDB");
                sb.AppendLine("      settle-card:");
                sb.AppendLine("        jdbc-url: jdbc:sqlserver://...;databaseName=SETTLE_CARD_DB");
                sb.AppendLine("      pl-card:");
                sb.AppendLine("        jdbc-url: jdbc:sqlserver://...;databaseName=PLCardDB");
                sb.AppendLine("      settle-poq:");
                sb.AppendLine("        jdbc-url: jdbc:sqlserver://...;databaseName=SETTLE_POQ_DB");
                sb.AppendLine("  ```");
            }

            return sb.ToString();
        }

        private static List<IndexEntry> BuildSpecIndex(BundleInputs inputs, string agentDir)
        {
            var entries = new List<IndexEntry>();

            foreach (var spDef in inputs.SpDefs)
            {
                var objectKey = spDef.ObjectKey ?? CodeObjectKey.Create(
                    inputs.Paths.CurrentDatabase, spDef.Schema, spDef.Name, CodeObjectType.Procedure);
                var specPath = inputs.Paths.ResolveSpecPath(objectKey);
                var label = $"{spDef.Schema}.{spDef.Name}";

                entries.Add(File.Exists(specPath)
                    ? new IndexEntry(label, RelativeToAgent(agentDir, specPath))
                    : new IndexEntry($"{label} (명세서 파일 없음 — 이 단계의 비즈니스 로직은 참조할 수 없습니다)", "#"));
            }

            return entries;
        }

        private static string RelativeToAgent(string agentDir, string absolutePath) =>
            Path.GetRelativePath(agentDir, absolutePath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

        private static Task WriteAsync(string path, string content, CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
    }
}
