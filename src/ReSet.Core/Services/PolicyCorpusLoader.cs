using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>정책 도출이 대상 하나에 대해 필요로 하는 재료 전부.</summary>
    public sealed record PolicySource(
        string Label,
        string SpecMarkdown,
        string DdlText,
        SpStaticAnalysisResult Analysis,
        IReadOnlyList<DependencyInfo> Dependencies);

    /// <summary>
    /// 대상 하나의 Spec.md와 raw/metadata.json을 읽어 재료로 만든다.
    ///
    /// [BOM 주의 - 정정] 이 저장소의 metadata.json은 UTF-8 BOM으로 저장된다. 다만
    /// <see cref="File.ReadAllText(string)"/> 단일 인자 오버로드는 인코딩을 자동
    /// 감지해 BOM을 이미 벗기고 돌려준다(2026-09-06 리뷰에서 실물 파일로 확인) -
    /// 아래의 <c>TrimStart('﻿')</c>는 이 경로에서는 사실 무해한 no-op이다.
    /// 방어적으로 남겨 둔다 - 호출부가 바이트를 직접 읽는 경로로 바뀌면 다시
    /// 의미가 생긴다.
    ///
    /// [소프트 페일] 한 대상의 metadata.json이 깨져도 나머지 대상의 정책 도출을
    /// 세우지 않는다. 그 대상은 DDL 없이(상수 좌변 없이) 명세서만으로 들어간다.
    /// </summary>
    public static class PolicyCorpusLoader
    {
        public static PolicySource? Load(PolicyTarget target)
        {
            var specPath = Path.Combine(target.DocsDirectory, OutputPathResolver.SpecFileNamePublic);
            if (!File.Exists(specPath))
            {
                return null;
            }

            var spec = File.ReadAllText(specPath);
            var ddl = string.Empty;
            var analysis = new SpStaticAnalysisResult();
            IReadOnlyList<DependencyInfo> dependencies = Array.Empty<DependencyInfo>();

            if (File.Exists(target.MetadataPath))
            {
                try
                {
                    // File.ReadAllText가 이미 BOM을 벗겨 준다 - 이 TrimStart는
                    // 방어적 no-op이다(클래스 문서의 [BOM 주의 - 정정] 참조).
                    var json = File.ReadAllText(target.MetadataPath).TrimStart('﻿');
                    var definition = JsonSerializer.Deserialize<SpDefinition>(
                        json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (definition is not null)
                    {
                        ddl = definition.DdlText ?? string.Empty;
                        analysis = definition.StaticAnalysis ?? new SpStaticAnalysisResult();
                        dependencies = definition.Dependencies ?? new List<DependencyInfo>();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "정책 도출 재료 적재 실패 - {Label}의 metadata.json을 읽지 못해 명세서만으로 진행합니다.",
                        target.Label);
                }
            }

            return new PolicySource(target.Label, spec, ddl, analysis, dependencies);
        }
    }
}
