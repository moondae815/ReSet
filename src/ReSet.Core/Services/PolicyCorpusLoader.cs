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
    /// [BOM 주의] 이 저장소의 metadata.json은 UTF-8 BOM으로 저장된다. BOM을 벗기지
    /// 않으면 System.Text.Json이 첫 글자에서 던진다.
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
                    // BOM은 여기서 벗긴다. 파일 바이트를 그대로 넘기면 던진다.
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
