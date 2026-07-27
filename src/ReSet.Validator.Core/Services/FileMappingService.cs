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

            // 1. 설계서 파일 탐색 (Spec.md 및 BatchMigrationPlan.md)
            var specFiles = new List<string>();
            specFiles.AddRange(Directory.GetFiles(config.SpecDirectory, "Spec.md", SearchOption.AllDirectories));
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

                // 규칙 1: YAML Front Matter 검사
                mappedSourcePath = TryGetPathFromYaml(specPath, config.SourceCodeDirectory);

                // 규칙 2: 규칙 기반 파일명 매치
                if (string.IsNullOrEmpty(mappedSourcePath))
                {
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

        private string? TryGetPathFromYaml(string specPath, string sourceBaseDir)
        {
            try
            {
                using var reader = new StreamReader(specPath);
                var firstLine = reader.ReadLine();
                if (firstLine != null && firstLine.Trim() == "---")
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null && line.Trim() != "---")
                    {
                        // TargetCode: src/Migration/CustOrderHist.cs
                        var match = Regex.Match(line, @"^\s*(TargetCode|TargetFile)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            var relativePath = match.Groups[2].Value.Trim();
                            // 따옴표 제거
                            relativePath = relativePath.Trim('\'', '"');
                            
                            var fullPath = Path.Combine(sourceBaseDir, relativePath);
                            if (File.Exists(fullPath))
                            {
                                return fullPath;
                            }
                            
                            // 보정: relativePath가 "src/" 등으로 시작하고 sourceBaseDir의 폴더명과 겹치는 경우
                            var sourceBaseDirName = Path.GetFileName(sourceBaseDir);
                            if (!string.IsNullOrEmpty(sourceBaseDirName))
                            {
                                var prefix = sourceBaseDirName + "/";
                                var normalizedRel = relativePath.Replace('\\', '/');
                                if (normalizedRel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                {
                                    var slicedPath = relativePath.Substring(prefix.Length);
                                    var altFullPath = Path.Combine(sourceBaseDir, slicedPath);
                                    if (File.Exists(altFullPath))
                                    {
                                        return altFullPath;
                                    }
                                }
                            }
                            
                            // 절대 경로이거나 프로젝트 내 독립적 경로인 경우 검사
                            if (File.Exists(relativePath))
                            {
                                return Path.GetFullPath(relativePath);
                            }
                        }
                    }
                }
            }
            catch
            {
                // YAML 분석 중 오류는 조용히 넘어가고 규칙 기반 매핑 시도
            }
            return null;
        }
    }
}
