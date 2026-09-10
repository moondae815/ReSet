using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Cli
{
    /// <summary>
    /// 계획 수립 재료 한 벌. 명세서 본문(AI 프롬프트의 재료)과 정적 분석 정의(목차 보강·
    /// 지시서 번들의 재료)는 <b>같은 순서</b>여야 한다 — 그 순서가 배치 스텝의 실행 순서다.
    /// </summary>
    public sealed record PlanMaterials(
        List<(string FileName, string Content)> Specs,
        List<SpDefinition> Definitions,
        IReadOnlyList<string> NotFound,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> AddedByClosure);

    /// <summary>
    /// `--plan-only` 의 재료 적재. 저장된 산출물만 읽으며 DB에 붙지 않는다.
    ///
    /// TUI 2번 메뉴가 사람의 순차 선택으로 하는 일을 `--sp` 나열로 하되, 폐포 확장과
    /// 메타데이터 복원은 <see cref="BatchStepCatalog"/> 의 같은 메서드를 쓴다 — 진입
    /// 경로에 따라 재료가 갈리면 같은 도구가 다른 계획서를 낸다.
    /// </summary>
    public static class PlanOnlyMaterialLoader
    {
        public static async Task<PlanMaterials> LoadAsync(
            string outputRoot,
            IReadOnlyList<string> identifiers,
            CancellationToken cancellationToken)
        {
            var resolution = BatchStepCatalog.ResolveEntryPointSpecPaths(outputRoot, identifiers);

            // 진입점 자체를 못 찾았으면 폐포를 돌릴 것도 없다. 판정은 호출부가 한다 -
            // 여기서 화면에 쓰거나 종료 코드를 정하지 않는다.
            if (resolution.SpecPaths.Count == 0)
            {
                return new PlanMaterials(
                    new List<(string, string)>(),
                    new List<SpDefinition>(),
                    resolution.NotFound,
                    new List<string>(),
                    new List<string>());
            }

            var closure = BatchStepCatalog.CloseOverProcedureReferences(outputRoot, resolution.SpecPaths);

            var specs = new List<(string FileName, string Content)>();
            var warnings = new List<string>();

            foreach (var relativePath in closure.SpecPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var identifier = BatchStepCatalog.ExtractProcedureIdentifier(relativePath);
                var fullPath = Path.Combine(outputRoot, relativePath);
                if (identifier == null || !File.Exists(fullPath))
                {
                    continue;
                }

                specs.Add((identifier, await File.ReadAllTextAsync(fullPath, cancellationToken)));
            }

            // 정의는 폐포 순서 그대로 읽는다. LoadDefinitionsAsync 의 계약이 입력 순서를
            // 실행 순서로 쓰므로, 이 순서가 곧 위 specs 의 순서와 같아야 한다.
            var loaded = await BatchStepCatalog.LoadDefinitionsAsync(
                outputRoot, closure.SpecPaths, cancellationToken);

            foreach (var missing in loaded.MissingMetadata)
            {
                warnings.Add(
                    $"{missing} 의 메타데이터(raw/metadata.json)가 없어 정의 재료에서 제외됩니다 — 해당 배치 스텝은 지시서의 구현 대상에서 빠집니다.");
            }

            foreach (var failed in loaded.FailedToParse)
            {
                warnings.Add(
                    $"{failed} 의 메타데이터를 읽지 못해 정의 재료에서 제외됩니다 — 해당 배치 스텝은 지시서의 구현 대상에서 빠집니다.");
            }

            if (closure.CapExceeded)
            {
                warnings.Add("참조 폐포가 상한에 걸려 일부 참조 프로시저가 재료에서 빠졌습니다.");
            }

            return new PlanMaterials(
                specs,
                new List<SpDefinition>(loaded.Definitions),
                resolution.NotFound,
                warnings,
                closure.Added);
        }
    }
}
