using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ReSet.Validator.Core.Models;
using Serilog;

namespace ReSet.Validator.Core.Services
{
    /// <param name="SourceFileNameHint">확장자를 뺀 예상 소스 파일명. null이면 MappedName으로 찾는다.</param>
    public sealed record ExplicitPair(string SpecFilePath, string MappedName, string? SourceFileNameHint);

    public class FileMappingService
    {
        /// <summary>
        /// 호출부가 지정한 쌍만 검증 대상으로 만든다.
        ///
        /// 무인자 오버로드는 Job 하나당 BatchMigrationPlan.md 1쌍만 매핑하므로,
        /// L2 AI가 계획서 전문과 프로젝트 전체 소스를 한 번에 받는다. 회차 단위
        /// 검증에서는 그 범위가 회차 분할의 이득을 그대로 되돌린다.
        ///
        /// 소스를 찾지 못한 쌍은 버린다 - 소스 디렉터리 전체로 폴백하면 범위를
        /// 좁힌 의미가 사라진다. Tasklet이 없다는 것 자체가 그 회차의 실패다.
        /// </summary>
        public List<ValidationResult> ResolveMappings(
            ValidatorConfig config, IReadOnlyList<ExplicitPair> explicitPairs)
        {
            var results = new List<ValidationResult>();

            if (!Directory.Exists(config.SourceCodeDirectory))
            {
                Log.Warning("소스코드 디렉토리가 없습니다 - Path: {Path}", config.SourceCodeDirectory);
                return results;
            }

            var sourceFiles = Directory
                .EnumerateFiles(config.SourceCodeDirectory, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var pair in explicitPairs)
            {
                if (!File.Exists(pair.SpecFilePath))
                {
                    Log.Warning("검증 대상 설계서가 없습니다 - Name: {Name}, Path: {Path}",
                        pair.MappedName, pair.SpecFilePath);
                    continue;
                }

                var hint = pair.SourceFileNameHint;
                var matched = hint != null
                    ? sourceFiles.FirstOrDefault(f =>
                        Path.GetFileNameWithoutExtension(f).Equals(hint, StringComparison.OrdinalIgnoreCase))
                    : null;

                // 힌트로 못 찾으면 파일명에 단계 코드가 든 것을 찾는다.
                matched ??= sourceFiles.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f)
                        .Contains(pair.MappedName, StringComparison.OrdinalIgnoreCase));

                if (matched == null)
                {
                    Log.Warning("검증 대상 소스를 찾지 못했습니다 - Name: {Name}", pair.MappedName);
                    continue;
                }

                results.Add(new ValidationResult
                {
                    SpecFilePath = pair.SpecFilePath,
                    SourceCodePath = matched,
                    MappedName = pair.MappedName,
                });
            }

            return results;
        }

        public List<ValidationResult> ResolveMappings(ValidatorConfig config)
        {
            var results = new List<ValidationResult>();

            if (!Directory.Exists(config.SpecDirectory))
            {
                throw new DirectoryNotFoundException($"설계서 디렉토리를 찾을 수 없습니다: {config.SpecDirectory}");
            }

            if (!Directory.Exists(config.SourceCodeDirectory))
            {
                throw new DirectoryNotFoundException($"소스코드 디렉토리를 찾을 수 없습니다: {config.SourceCodeDirectory}");
            }

            // 1. 설계서 파일 탐색 (BatchMigrationPlan.md)
            var specFiles = new List<string>();
            specFiles.AddRange(Directory.GetFiles(config.SpecDirectory, "BatchMigrationPlan.md", SearchOption.AllDirectories));
            
            // 2. 소스코드 파일 탐색 (C# 및 Java)
            var sourceFiles = new List<string>();
            var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".java" };
            
            foreach (var file in Directory.EnumerateFiles(config.SourceCodeDirectory, "*.*", SearchOption.AllDirectories))
            {
                if (allowedExtensions.Contains(Path.GetExtension(file)))
                {
                    sourceFiles.Add(file);
                }
            }

            foreach (var specPath in specFiles)
            {
                var specFileName = Path.GetFileName(specPath);
                // 경로에서 SP 폴더명 추출 (Procedures/dbo.CustOrderHist/docs/Spec.md -> dbo.CustOrderHist)
                var baseName = Directory.GetParent(specPath)?.Parent?.Name ?? specFileName.Replace(".md", "");
                
                // 스키마 제거 (dbo.CustOrderHist -> CustOrderHist)
                var cleanName = baseName;
                if (baseName.Contains('.'))
                {
                    var dotIndex = baseName.LastIndexOf('.');
                    cleanName = baseName.Substring(dotIndex + 1);
                }

                string? mappedSourcePath = null;

                // 규칙 1: 규칙 기반 파일명 매치
                foreach (var srcPath in sourceFiles)
                {
                    var srcFileName = Path.GetFileNameWithoutExtension(srcPath);
                    if (srcFileName.Equals(cleanName, StringComparison.OrdinalIgnoreCase) || 
                        srcFileName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        mappedSourcePath = srcPath;
                        break;
                    }
                }

                // 규칙 2: 다중 파일 프로젝트 (폴더 매치) - 배치 마이그레이션 대응
                if (string.IsNullOrEmpty(mappedSourcePath))
                {
                    var noUnderscore = cleanName.Replace("_", "");
                    var possibleDirs = new[]
                    {
                        Path.Combine(config.SourceCodeDirectory, $"{noUnderscore}.Batch"),
                        Path.Combine(config.SourceCodeDirectory, $"{cleanName}.Batch"),
                        Path.Combine(config.SourceCodeDirectory, noUnderscore),
                        Path.Combine(config.SourceCodeDirectory, cleanName)
                    };

                    foreach (var dir in possibleDirs)
                    {
                        if (Directory.Exists(dir))
                        {
                            mappedSourcePath = dir;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(mappedSourcePath))
                {
                    results.Add(new ValidationResult
                    {
                        SpecFilePath = specPath,
                        SourceCodePath = mappedSourcePath,
                        MappedName = baseName
                    });
                }
            }

            return results;
        }
    }
}
