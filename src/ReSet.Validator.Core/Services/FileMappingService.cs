using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ReSet.Validator.Core.Models;

namespace ReSet.Validator.Core.Services
{
    public class FileMappingService
    {
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
