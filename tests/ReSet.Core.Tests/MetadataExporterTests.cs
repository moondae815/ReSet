using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class MetadataExporterTests
    {
        [Fact]
        public async Task ExportRawMetadataAsync_ShouldCreateJsonFile_WhenSaveJsonIsTrue()
        {
            // Arrange
            var testOutputDir = Path.Combine(Directory.GetCurrentDirectory(), "test_output_exporter");
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }

            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "USP_TestExporter",
                DdlText = "SELECT 1;"
            };
            var rawContext = "Test Context Header\nSELECT 1;";
            
            // IMetadataExporter 선언
            IMetadataExporter exporter = new MetadataExporter();

            // Act
            await exporter.ExportRawMetadataAsync(spDef, rawContext, testOutputDir, true, false, false);

            // Assert
            var expectedJsonPath = Path.Combine(testOutputDir, "raw", "metadata.json");
            Assert.True(File.Exists(expectedJsonPath));

            // Clean up
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }
        }

        [Fact]
        public async Task AppendFeedbackToInstructionsAsync_ShouldAppendFeedback_WhenFileExists()
        {
            // Arrange
            var testOutputDir = Path.Combine(Directory.GetCurrentDirectory(), "test_output_append");
            if (!Directory.Exists(testOutputDir)) Directory.CreateDirectory(testOutputDir);

            var instructionsPath = Path.Combine(testOutputDir, "MigrationInstructions.md");
            await File.WriteAllTextAsync(instructionsPath, "# Original Content\n");

            var exporter = new MetadataExporter();
            var feedback = "This is a test feedback from AI";

            // Act
            await exporter.AppendFeedbackToInstructionsAsync(instructionsPath, feedback);

            // Assert
            var resultText = await File.ReadAllTextAsync(instructionsPath);
            Assert.Contains("# Original Content", resultText);
            Assert.Contains("This is a test feedback from AI", resultText);

            // Clean up
            if (Directory.Exists(testOutputDir)) Directory.Delete(testOutputDir, true);
        }

        [Fact]
        public async Task ExportRawMetadataAsync_ShouldIncludeDescriptionsInMarkdown_WhenSaveFilesIsTrue()
        {
            // Arrange
            var testOutputDir = Path.Combine(Directory.GetCurrentDirectory(), "test_output_exporter_desc");
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }

            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "USP_TestExporterDesc",
                DdlText = "SELECT 1;"
            };
            
            var depInfo = new DependencyInfo
            {
                Schema = "dbo",
                Name = "TBL_TestDesc",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Description = "테스트용 테이블 설명"
            };
            depInfo.Columns.Add(new ColumnInfo
            {
                ColumnName = "COL_Test",
                DataType = "INT",
                IsNullable = false,
                IsPrimaryKey = true,
                Description = "테스트용 컬럼 설명"
            });
            spDef.Dependencies.Add(depInfo);

            var rawContext = "Test Context Header";
            IMetadataExporter exporter = new MetadataExporter();

            // Act
            await exporter.ExportRawMetadataAsync(spDef, rawContext, testOutputDir, false, false, true);

            // Assert
            var expectedMdPath = Path.Combine(testOutputDir, "raw", "ddl", "tables", "dbo.TBL_TestDesc.md");
            Assert.True(File.Exists(expectedMdPath));

            var mdContent = await File.ReadAllTextAsync(expectedMdPath);
            Assert.Contains("테스트용 테이블 설명", mdContent);
            Assert.Contains("테스트용 컬럼 설명", mdContent);

            // Clean up
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }
        }

        [Fact]
        public async Task ExportRawMetadataAsync_ShouldSaveContext_WhenSaveContextIsTrue()
        {
            // Arrange
            var testOutputDir = Path.Combine(Directory.GetCurrentDirectory(), "test_output_exporter_context");
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }

            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "USP_TestExporterContext",
                DdlText = "SELECT 1;"
            };
            var rawContext = "Test Context Content";
            IMetadataExporter exporter = new MetadataExporter();

            // Act
            await exporter.ExportRawMetadataAsync(spDef, rawContext, testOutputDir, false, true, false);

            // Assert
            var expectedContextPath = Path.Combine(testOutputDir, "raw", "prompt-context.md");
            Assert.True(File.Exists(expectedContextPath));
            var savedContext = await File.ReadAllTextAsync(expectedContextPath);
            Assert.Contains(rawContext, savedContext);

            // Clean up
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }
        }

        [Fact]
        public async Task ExportRawMetadataAsync_ShouldExportProceduresAndFunctions_WhenSaveFilesIsTrue()
        {
            // Arrange
            var testOutputDir = Path.Combine(Directory.GetCurrentDirectory(), "test_output_exporter_objects");
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }

            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "USP_TestExporterObjects",
                DdlText = "SELECT 1;"
            };

            var procDep = new DependencyInfo
            {
                Schema = "dbo",
                Name = "USP_ChildProc",
                Type = "SQL_STORED_PROCEDURE",
                DiscoveryDepth = 2,
                ReferencedDdlText = "CREATE PROCEDURE dbo.USP_ChildProc AS SELECT 2;"
            };

            var funcDep = new DependencyInfo
            {
                Schema = "dbo",
                Name = "UFN_ChildFunc",
                Type = "SQL_SCALAR_FUNCTION",
                DiscoveryDepth = 2,
                ReferencedDdlText = "CREATE FUNCTION dbo.UFN_ChildFunc() RETURNS INT AS BEGIN RETURN 1; END;"
            };

            spDef.Dependencies.Add(procDep);
            spDef.Dependencies.Add(funcDep);

            IMetadataExporter exporter = new MetadataExporter();

            // Act
            await exporter.ExportRawMetadataAsync(spDef, "dummy context", testOutputDir, false, false, true);

            // Assert
            var expectedProcPath = Path.Combine(testOutputDir, "raw", "ddl", "procedures", "dbo.USP_ChildProc.sql");
            var expectedFuncPath = Path.Combine(testOutputDir, "raw", "ddl", "functions", "dbo.UFN_ChildFunc.sql");

            Assert.True(File.Exists(expectedProcPath));
            Assert.True(File.Exists(expectedFuncPath));

            var procContent = await File.ReadAllTextAsync(expectedProcPath);
            var funcContent = await File.ReadAllTextAsync(expectedFuncPath);

            Assert.Equal(procDep.ReferencedDdlText, procContent);
            Assert.Equal(funcDep.ReferencedDdlText, funcContent);

            // Clean up
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }
        }

        [Fact]
        public async Task ExportCodeObjectArtifactsAsync_ReferenceModeWritesCanonicalDdlOnly()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-MetadataExporter-{Guid.NewGuid():N}");
            var resolver = new OutputPathResolver("PaymentDB", outputRoot);
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Parent", CodeObjectType.Procedure);
            var childKey = CodeObjectKey.Create("PaymentDB", "dbo", "FN_X", CodeObjectType.Function);
            var definition = new SpDefinition
            {
                Schema = "dbo",
                Name = "USP_Parent",
                DdlText = "CREATE PROCEDURE dbo.USP_Parent AS SELECT dbo.FN_X();"
            };
            definition.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "FN_X",
                Type = "SQL_SCALAR_FUNCTION",
                ReferencedDdlText = "CREATE FUNCTION dbo.FN_X() RETURNS INT AS BEGIN RETURN 1; END;"
            });
            var graph = new CodeObjectPipelineResult
            {
                Nodes = new System.Collections.Generic.List<AnalysisNode>
                {
                    new(key) { Status = AnalysisNodeStatus.Succeeded },
                    new(childKey) { Status = AnalysisNodeStatus.Succeeded }
                },
                DependencyEdges = new System.Collections.Generic.List<DependencyEdge> { new(key, childKey) }
            };

            try
            {
                IMetadataExporter exporter = new MetadataExporter();

                await exporter.ExportCodeObjectArtifactsAsync(
                    definition,
                    key,
                    graph,
                    DependencyArtifactMode.Reference,
                    outputRoot);

                Assert.True(File.Exists(resolver.ResolveCanonicalDdlPath(key)));
                Assert.False(File.Exists(Path.Combine(
                    outputRoot,
                    "Procedures",
                    "dbo.USP_Parent",
                    "raw",
                    "ddl",
                    "functions",
                    "dbo.FN_X.sql")));
            }
            finally
            {
                if (Directory.Exists(outputRoot))
                {
                    Directory.Delete(outputRoot, true);
                }
            }
        }

        /// <summary>
        /// rawPromptContext 인자를 넘기지 않아도 definition.RawPromptContext가 쓰인다.
        /// 그리고 그 결과는 metadata.json·dependency-manifest.json과 같은 집에 놓인다 -
        /// prompt-context.md는 정본 DDL이 아니라 회차별 분석 흔적이기 때문이다.
        /// </summary>
        [Fact]
        public async Task ExportCodeObjectArtifactsAsync_WritesPromptContextNextToManifest()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-MetadataExporter-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Prompt", CodeObjectType.Procedure);
            var definition = new SpDefinition
            {
                Schema = key.Schema,
                Name = key.Name,
                DdlText = "SELECT 1;",
                RawPromptContext = "actual prompt body"
            };

            try
            {
                await new MetadataExporter().ExportCodeObjectArtifactsAsync(
                    definition,
                    key,
                    new CodeObjectPipelineResult { Nodes = new System.Collections.Generic.List<AnalysisNode> { new(key) { Status = AnalysisNodeStatus.Succeeded } } },
                    DependencyArtifactMode.Reference,
                    outputRoot);

                var promptPath = Path.Combine(outputRoot, "Procedures", "dbo.USP_Prompt", "raw", "prompt-context.md");
                Assert.Equal("actual prompt body", await File.ReadAllTextAsync(promptPath));

                // 정본 폴더에는 DDL만 남는다. 한 객체의 raw가 두 집으로 쪼개지면
                // §11의 되짚는 순서("모델이 무엇을 봤나 → raw/prompt-context.md")가 거짓이 된다.
                Assert.False(File.Exists(Path.Combine(
                    outputRoot, "Objects", "dbo.USP_Prompt.Procedure", "raw", "prompt-context.md")));
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task ExportCodeObjectArtifactsAsync_WritesMetadataJsonNextToManifest()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-MetadataExporter-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Meta", CodeObjectType.Procedure);
            var definition = new SpDefinition
            {
                Schema = key.Schema,
                Name = key.Name,
                DdlText = "SELECT 1;",
                Dependencies = new System.Collections.Generic.List<DependencyInfo>
                {
                    new() { SourceObjectKey = key, Schema = "dbo", Name = "TOrder", Type = "TABLE" }
                }
            };

            try
            {
                await new MetadataExporter().ExportCodeObjectArtifactsAsync(
                    definition,
                    key,
                    new CodeObjectPipelineResult
                    {
                        Nodes = new System.Collections.Generic.List<AnalysisNode>
                        {
                            new(key) { Status = AnalysisNodeStatus.Succeeded }
                        }
                    },
                    DependencyArtifactMode.Reference,
                    outputRoot);

                var metadataPath = Path.Combine(
                    outputRoot, "Procedures", "dbo.USP_Meta", "raw", "metadata.json");
                Assert.True(File.Exists(metadataPath), $"metadata.json이 없습니다: {metadataPath}");

                // 지시서 번들이 실제로 쓰는 payload는 Dependencies다. 왕복이 되어야 한다.
                var restored = System.Text.Json.JsonSerializer.Deserialize<SpDefinition>(
                    await File.ReadAllTextAsync(metadataPath),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.NotNull(restored);
                Assert.Equal("TOrder", Assert.Single(restored!.Dependencies).Name);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task ExportCodeObjectArtifactsAsync_CreatesEmptyPromptContextFileWhenNoPromptExists()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-MetadataExporter-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_EmptyPrompt", CodeObjectType.Procedure);

            try
            {
                await new MetadataExporter().ExportCodeObjectArtifactsAsync(
                    new SpDefinition { Schema = key.Schema, Name = key.Name, DdlText = "SELECT 1;" },
                    key,
                    new CodeObjectPipelineResult { Nodes = new System.Collections.Generic.List<AnalysisNode> { new(key) { Status = AnalysisNodeStatus.Succeeded } } },
                    DependencyArtifactMode.Reference,
                    outputRoot);

                var promptPath = Path.Combine(outputRoot, "Procedures", "dbo.USP_EmptyPrompt", "raw", "prompt-context.md");
                Assert.True(File.Exists(promptPath));
                Assert.Equal(string.Empty, await File.ReadAllTextAsync(promptPath));
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        /// <summary>
        /// 캐시 히트는 SpDefinition.RawPromptContext를 채우지 않는다(파이프라인이 AI를
        /// 호출한 회차에만 채운다). 그 빈 값으로 기존 파일을 덮으면, 앞선 회차가 실제로
        /// 모델에 보낸 프롬프트 원문이 사라진다 - 산출물이 이상할 때 입력부터 확인하라는
        /// 이 파일의 존재 이유가 캐시 히트 한 번에 파괴된다.
        /// 파일이 아예 없을 때 빈 파일을 남기는 계약(위 테스트)은 그대로 지킨다.
        /// </summary>
        [Fact]
        public async Task ExportCodeObjectArtifactsAsync_PreservesExistingPromptContextWhenPromptIsEmpty()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-MetadataExporter-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_CacheHit", CodeObjectType.Procedure);
            var promptPath = Path.Combine(
                outputRoot, "Procedures", "dbo.USP_CacheHit", "raw", "prompt-context.md");

            try
            {
                // 1회차: 실제로 AI를 호출해 프롬프트 원문을 남겼다.
                await new MetadataExporter().ExportCodeObjectArtifactsAsync(
                    new SpDefinition
                    {
                        Schema = key.Schema,
                        Name = key.Name,
                        DdlText = "SELECT 1;",
                        RawPromptContext = "=== [System Prompt] ===\nreal prompt from attempt 1"
                    },
                    key,
                    new CodeObjectPipelineResult { Nodes = new System.Collections.Generic.List<AnalysisNode> { new(key) { Status = AnalysisNodeStatus.Succeeded } } },
                    DependencyArtifactMode.Reference,
                    outputRoot);

                Assert.Contains("real prompt from attempt 1", await File.ReadAllTextAsync(promptPath));

                // 2회차: 같은 객체가 다른 SP의 의존성으로 걸려 캐시 히트했다.
                // RawPromptContext는 null이다.
                await new MetadataExporter().ExportCodeObjectArtifactsAsync(
                    new SpDefinition { Schema = key.Schema, Name = key.Name, DdlText = "SELECT 1;" },
                    key,
                    new CodeObjectPipelineResult { Nodes = new System.Collections.Generic.List<AnalysisNode> { new(key) { Status = AnalysisNodeStatus.Succeeded } } },
                    DependencyArtifactMode.Reference,
                    outputRoot);

                Assert.Contains("real prompt from attempt 1", await File.ReadAllTextAsync(promptPath));
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task ExportCodeObjectArtifactsAsync_PortableBundleWritesOnlyReferencedDdlAlongsideCanonicalDdl()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-MetadataExporter-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Portable", CodeObjectType.Procedure);
            var childKey = CodeObjectKey.Create("PaymentDB", "dbo", "FN_X", CodeObjectType.Function);
            var definition = new SpDefinition { Schema = key.Schema, Name = key.Name, DdlText = "CREATE PROCEDURE dbo.USP_Portable AS SELECT 1;" };
            definition.Dependencies.Add(new DependencyInfo
            {
                Schema = childKey.Schema,
                Name = childKey.Name,
                Type = "SQL_SCALAR_FUNCTION",
                ReferencedDdlText = "CREATE FUNCTION dbo.FN_X() RETURNS INT AS BEGIN RETURN 1; END;"
            });
            definition.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "USP_Child",
                Type = "SQL_STORED_PROCEDURE",
                ReferencedDdlText = "CREATE PROCEDURE dbo.USP_Child AS SELECT 1;"
            });
            definition.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "TBL_ShouldNotCopy",
                Type = "USER_TABLE",
                ReferencedDdlText = "CREATE TABLE dbo.TBL_ShouldNotCopy (Id int);"
            });
            definition.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "VW_ShouldNotCopy",
                Type = "VIEW",
                ReferencedDdlText = "CREATE VIEW dbo.VW_ShouldNotCopy AS SELECT 1 AS Id;"
            });
            definition.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "IF_Inline",
                Type = "SQL_INLINE_TABLE_VALUED_FUNCTION",
                ReferencedDdlText = "CREATE FUNCTION dbo.IF_Inline() RETURNS TABLE AS RETURN SELECT 1 AS Id;"
            });
            definition.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "FS_ClrScalar",
                Type = "CLR_SCALAR_FUNCTION",
                ReferencedDdlText = "EXTERNAL NAME Assembly.[Type].Scalar;"
            });
            definition.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "FT_ClrTable",
                Type = "CLR_TABLE_VALUED_FUNCTION",
                ReferencedDdlText = "EXTERNAL NAME Assembly.[Type].Table;"
            });
            definition.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "PC_ClrProcedure",
                Type = "CLR_STORED_PROCEDURE",
                ReferencedDdlText = "EXTERNAL NAME Assembly.[Type].Procedure;"
            });

            try
            {
                await new MetadataExporter().ExportCodeObjectArtifactsAsync(
                    definition,
                    key,
                    new CodeObjectPipelineResult { Nodes = new System.Collections.Generic.List<AnalysisNode> { new(key) { Status = AnalysisNodeStatus.Succeeded }, new(childKey) { Status = AnalysisNodeStatus.Succeeded } } },
                    DependencyArtifactMode.PortableBundle,
                    outputRoot);

                var rawDirectory = Path.Combine(outputRoot, "Objects", "dbo.USP_Portable.Procedure", "raw");
                Assert.True(File.Exists(Path.Combine(rawDirectory, "object_definition.sql")));
                Assert.True(File.Exists(Path.Combine(rawDirectory, "ddl", "functions", "dbo.FN_X.sql")));
                Assert.True(File.Exists(Path.Combine(rawDirectory, "ddl", "functions", "dbo.IF_Inline.sql")));
                Assert.True(File.Exists(Path.Combine(rawDirectory, "ddl", "functions", "dbo.FS_ClrScalar.sql")));
                Assert.True(File.Exists(Path.Combine(rawDirectory, "ddl", "functions", "dbo.FT_ClrTable.sql")));
                Assert.True(File.Exists(Path.Combine(rawDirectory, "ddl", "procedures", "dbo.USP_Child.sql")));
                Assert.True(File.Exists(Path.Combine(rawDirectory, "ddl", "procedures", "dbo.PC_ClrProcedure.sql")));
                Assert.False(File.Exists(Path.Combine(rawDirectory, "ddl", "functions", "dbo.TBL_ShouldNotCopy.sql")));
                Assert.False(File.Exists(Path.Combine(rawDirectory, "ddl", "functions", "dbo.VW_ShouldNotCopy.sql")));
                Assert.False(File.Exists(Path.Combine(rawDirectory, "ddl", "sp_definition.sql")));
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_ShouldCreateInstructionsFile_WithCorrectContent()
        {
            // Arrange
            var testOutputDir = Path.Combine(Directory.GetCurrentDirectory(), "test_output_exporter_consolidated");
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }

            var spDefs = new System.Collections.Generic.List<SpDefinition>
            {
                new SpDefinition
                {
                    Schema = "dbo",
                    Name = "USP_Sp1",
                    DdlText = "CREATE PROCEDURE dbo.USP_Sp1 AS SELECT 1;"
                },
                new SpDefinition
                {
                    Schema = "dbo",
                    Name = "USP_Sp2",
                    DdlText = "CREATE PROCEDURE dbo.USP_Sp2 AS SELECT 2;"
                }
            };

            var tableDep = new DependencyInfo
            {
                Schema = "dbo",
                Name = "TBL_TestDep",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Description = "의존 테이블 설명"
            };
            tableDep.Columns.Add(new ColumnInfo
            {
                ColumnName = "ID",
                DataType = "INT",
                IsNullable = false,
                IsPrimaryKey = true,
                Description = "PK 컬럼"
            });
            spDefs[0].Dependencies.Add(tableDep);

            var consolidatedPlan = "# Consolidated Plan\n- Job steps...";
            var jobName = "TestConsolidatedJob";

            IMetadataExporter exporter = new MetadataExporter();
            var paths = new OutputPathResolver("TestDB", testOutputDir);

            // Act
            await exporter.ExportConsolidatedMigrationInstructionsAsync(spDefs, consolidatedPlan, VerificationOutcome.Passed, jobName, testOutputDir, "C#", paths);

            // Assert
            var expectedPath = Path.Combine(testOutputDir, "agent", "MigrationInstructions.md");
            var expectedTodoPath = Path.Combine(testOutputDir, "agent", "todo.md");
            Assert.True(File.Exists(expectedPath));
            Assert.True(File.Exists(expectedTodoPath));

            var content = await File.ReadAllTextAsync(expectedPath);
            Assert.Contains($"# 🚀 Consolidated Migration Instructions for Coding Agent ({jobName})", content);

            // 계획 전문은 더 이상 진입점에 인라인되지 않는다. 진입점은 인덱스다.
            Assert.DoesNotContain(consolidatedPlan, content);

            // 지침이 어떤 계획 링크보다도 앞에 있어야 한다.
            var guidelines = content.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal);
            var index = content.IndexOf("파일 인덱스", StringComparison.Ordinal);
            Assert.True(guidelines >= 0 && guidelines < index);

            var tableSchemasPath1 = Path.Combine(testOutputDir, "raw", "ddl", "dbo.TBL_TestDep.md");

            Assert.True(File.Exists(tableSchemasPath1));

            var context1 = await File.ReadAllTextAsync(tableSchemasPath1);
            Assert.DoesNotContain("CREATE PROCEDURE dbo.USP_Sp1 AS SELECT 1;", context1);
            Assert.Contains("TBL_TestDep", context1);
            Assert.Contains("의존 테이블 설명", context1);

            Assert.Contains("raw/ddl/dbo.TBL_TestDep.md", content);
            Assert.Contains("todo.md", content);

            var todoContent = await File.ReadAllTextAsync(expectedTodoPath);
            Assert.Contains($"# 📋 {jobName} 통합 배치 마이그레이션 진행 상태", todoContent);

            // Clean up
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }
        }

        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_LinksExternalProcedureUnderExternalDirectory()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Instructions-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("AuditDB", "dbo", "USP_External", CodeObjectType.Procedure);
            var paths = new OutputPathResolver("PaymentDB", outputRoot);
            var specPath = paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            await File.WriteAllTextAsync(specPath, "# Spec");

            var spDef = new SpDefinition
            {
                ObjectKey = key, Schema = "dbo", Name = "USP_External", DdlText = "SELECT 1;"
            };

            try
            {
                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    new System.Collections.Generic.List<SpDefinition> { spDef },
                    "## 통합 배치 아키텍처 개요",
                    VerificationOutcome.Passed,
                    "Job1",
                    Path.Combine(outputRoot, "Jobs", "Job1"),
                    "C#",
                    paths);

                var instructions = await File.ReadAllTextAsync(
                    Path.Combine(outputRoot, "Jobs", "Job1", "agent", "MigrationInstructions.md"));

                // 지시서는 outputRoot/Jobs/Job1/agent/MigrationInstructions.md 에 쓰인다.
                // agent 폴더는 outputRoot 로부터 3단계 아래(Jobs, Job1, agent)이므로,
                // agent 폴더를 기준으로 상대화하면 반드시 "../../../"로 시작해야 한다.
                // (Path.GetRelativePath 를 여기서 호출해 계산하지 않는다 — 구현이 잘못된
                // 기준 디렉터리를 쓰더라도 같은 계산식이면 테스트가 그 오류에 동의해버린다.)
                Assert.Contains(
                    "../../../External/AuditDB/Procedures/dbo.USP_External/docs/Spec.md",
                    instructions);
                Assert.DoesNotContain("../../../Procedures/dbo.USP_External/docs/Spec.md", instructions);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_WritesReasonWhenSpecFileIsMissing()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Instructions-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Gone", CodeObjectType.Procedure);
            var paths = new OutputPathResolver("PaymentDB", outputRoot);
            var spDef = new SpDefinition
            {
                ObjectKey = key, Schema = "dbo", Name = "USP_Gone", DdlText = "SELECT 1;"
            };

            try
            {
                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    new System.Collections.Generic.List<SpDefinition> { spDef },
                    "## 통합 배치 아키텍처 개요",
                    VerificationOutcome.Passed,
                    "Job1",
                    Path.Combine(outputRoot, "Jobs", "Job1"),
                    "C#",
                    paths);

                var instructions = await File.ReadAllTextAsync(
                    Path.Combine(outputRoot, "Jobs", "Job1", "agent", "MigrationInstructions.md"));
                Assert.Contains("명세서 파일 없음", instructions);

                // InstructionBundleWriter.BuildSpecIndex는 모든 항목을 같은
                // [Spec.md](경로) 서식으로 렌더링한다 - 파일이 없으면 실제 경로 대신
                // "#" 자리표시자를 쓴다. 링크 문법 자체가 사라지는 게 아니라 아무 곳도
                // 가리키지 않는 링크가 된다. 실제 파일 경로로는 만들어지지 않는다.
                Assert.Contains("[Spec.md](#)", instructions);
                Assert.DoesNotContain("[Spec.md](../", instructions);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        // 검증 상태 배너(InstructionEntryPointComposer §0)는 계획 본문이 인라인되어
        // 있는지와 무관하게 항상 진입점에 실린다 - planOutcome 값에서 직접 만들어지기
        // 때문이다(계획 문자열에 무엇이 들어 있는지 살피지 않는다). L1 소진/품질 미달/
        // 리뷰 미수행 세 상태는 예전에는 배너가 계획서 문자열 자체에 붙어 있어야만
        // 번들까지 따라왔지만, L3 피드백 재생성 경로는 계획서를 통째로 교체하므로
        // 배너가 사라지고 YAML 헤더에만 의존한다 - 그 헤더는 번들에 들어가지 않는다.
        // 그래서 번들은 종료 상태 값을 직접 받아 자기 검증 상태를 밝힌다.
        [Theory]
        [InlineData(VerificationOutcome.ReviewNotRun, "리뷰 미수행")]
        [InlineData(VerificationOutcome.QualityRejected, "품질 미달")]
        [InlineData(VerificationOutcome.L1Exhausted, "L1 미통과")]
        public async Task ExportConsolidatedMigrationInstructionsAsync_StatesTheOutcomeAndWarnsWhenNotVerified(
            VerificationOutcome outcome,
            string expectedLabel)
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Instructions-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Unverified", CodeObjectType.Procedure);
            var paths = new OutputPathResolver("PaymentDB", outputRoot);
            var spDef = new SpDefinition
            {
                ObjectKey = key, Schema = "dbo", Name = "USP_Unverified", DdlText = "SELECT 1;"
            };

            try
            {
                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    new System.Collections.Generic.List<SpDefinition> { spDef },
                    // 배너가 전혀 없는 계획서 본문. L3 재생성 경로가 만들어내는 모양이다.
                    "## 통합 배치 아키텍처 개요",
                    outcome,
                    "Job1",
                    Path.Combine(outputRoot, "Jobs", "Job1"),
                    "C#",
                    paths);

                var instructions = await File.ReadAllTextAsync(
                    Path.Combine(outputRoot, "Jobs", "Job1", "agent", "MigrationInstructions.md"));

                Assert.Contains("0. 이 계획서의 검증 상태", instructions);
                Assert.Contains(expectedLabel, instructions);
                Assert.Contains("사람의 검토가 필요합니다", instructions);

                // 상태 고지가 계획 본문 및 지침보다 먼저 와야 한다. 코딩 에이전트는
                // 위에서부터 읽으므로, 계획을 소비한 뒤에 경고를 만나면 이미 늦다.
                Assert.True(
                    instructions.IndexOf("0. 이 계획서의 검증 상태", StringComparison.Ordinal) <
                    instructions.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal));
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_StatesPassedExplicitlyRatherThanStayingSilent()
        {
            // 통과일 때 아무것도 쓰지 않으면 "표기 부재 = 검증됨"이라는 추론을 낳는다.
            // 그것이 이 계열 결함의 뿌리이므로 네 상태를 모두 명시한다.
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Instructions-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Ok", CodeObjectType.Procedure);
            var paths = new OutputPathResolver("PaymentDB", outputRoot);
            var spDef = new SpDefinition
            {
                ObjectKey = key, Schema = "dbo", Name = "USP_Ok", DdlText = "SELECT 1;"
            };

            try
            {
                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    new System.Collections.Generic.List<SpDefinition> { spDef },
                    "## 통합 배치 아키텍처 개요",
                    VerificationOutcome.Passed,
                    "Job1",
                    Path.Combine(outputRoot, "Jobs", "Job1"),
                    "C#",
                    paths);

                var instructions = await File.ReadAllTextAsync(
                    Path.Combine(outputRoot, "Jobs", "Job1", "agent", "MigrationInstructions.md"));

                Assert.Contains("0. 이 계획서의 검증 상태", instructions);
                Assert.Contains("통과", instructions);
                Assert.DoesNotContain("사람의 검토가 필요합니다", instructions);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        // coverage 매개변수가 실제로 BundleInputs까지 전달되는지 확인한다. 이전에는
        // 시그니처에만 받아 두고 BundleInputs 생성 호출에는 넘기지 않아, §0이 항상
        // "커버리지 없음"으로 렌더링됐다 - PlanVerificationSection 단위 테스트만으로는
        // 이 배선 누락을 잡을 수 없어 exporter를 관통하는 이 테스트가 필요하다.
        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_ForwardsCoverageIntoSection0()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Instructions-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Partial", CodeObjectType.Procedure);
            var paths = new OutputPathResolver("PaymentDB", outputRoot);
            var spDef = new SpDefinition
            {
                ObjectKey = key, Schema = "dbo", Name = "USP_Partial", DdlText = "SELECT 1;"
            };

            try
            {
                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    new System.Collections.Generic.List<SpDefinition> { spDef },
                    "## 통합 배치 아키텍처 개요",
                    VerificationOutcome.Passed,
                    "Job1",
                    Path.Combine(outputRoot, "Jobs", "Job1"),
                    "C#",
                    paths,
                    layout: null,
                    coverage: new VerificationCoverage(19, 17, false, false));

                var instructions = await File.ReadAllTextAsync(
                    Path.Combine(outputRoot, "Jobs", "Job1", "agent", "MigrationInstructions.md"));

                Assert.Contains("⚠️", instructions);
                Assert.Contains("검증되지 못한 단계", instructions);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_CarriesTheDataAccessBoundaryRules()
        {
            var testOutputDir = Path.Combine(Directory.GetCurrentDirectory(), "test_output_exporter_boundary");
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }

            try
            {
                var spDefs = new System.Collections.Generic.List<SpDefinition>
                {
                    new SpDefinition
                    {
                        Schema = "dbo",
                        Name = "USP_Sp1",
                        DdlText = "CREATE PROCEDURE dbo.USP_Sp1 AS SELECT 1;"
                    }
                };

                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    spDefs,
                    "# Plan",
                    VerificationOutcome.Passed,
                    "BoundaryJob",
                    testOutputDir,
                    "C#",
                    new OutputPathResolver("TestDB", testOutputDir));

                var instructions = await File.ReadAllTextAsync(
                    Path.Combine(testOutputDir, "agent", "MigrationInstructions.md"));

                // 규칙 문구는 DataAccessPolicy가 단독 소유한다. 지시서는 그것을 그대로 싣는다.
                Assert.Contains(DataAccessPolicy.InstructionRules("C#"), instructions);
                // 지침 7번의 placeholder 금지는 경계 규칙 도입 후에도 유지되어야 한다.
                Assert.Contains("Placeholder", instructions);
                Assert.Contains("허용 목록", instructions);

                // 패키지 설치 안내는 이제 todo.md가 아니라 부트스트랩 회차 작업 지시서
                // (task-00-bootstrap.md)가 진다 - 그 회차가 실제로 패키지를 설치하는 회차이기 때문이다.
                var bootstrapTask = await File.ReadAllTextAsync(
                    Path.Combine(testOutputDir, "agent", "task-00-bootstrap.md"));
                Assert.Contains("EF Core", bootstrapTask);
                // 모든 회차 작업 지시서는 공통 경계 규칙 문서를 "먼저 읽을 것"에 링크한다.
                Assert.Contains("경계 규칙", bootstrapTask);

                // 배치 호스팅(Worker Service)과 멀티 DB 커넥션 설정(ConnectionStrings) 안내는
                // common/03-hosting-and-config.md로 복원됐다. 스캐폴딩을 세우는 것은
                // Bootstrap 회차의 일이므로 그 회차의 작업 지시서만 이 파일을 가리킨다.
                Assert.Contains("common/03-hosting-and-config.md", bootstrapTask);

                var hostingConfig = await File.ReadAllTextAsync(
                    Path.Combine(testOutputDir, "agent", "common", "03-hosting-and-config.md"));
                Assert.Contains("Worker Service", hostingConfig);
                Assert.Contains("ConnectionStrings", hostingConfig);

                var stub = await File.ReadAllTextAsync(
                    Path.Combine(testOutputDir, "agent", "src", "AbstractSettleTasklet.cs"));
                Assert.Contains("UseTransaction", stub);
                Assert.DoesNotContain("[[ORM_BOUNDARY]]", stub);
            }
            finally
            {
                if (Directory.Exists(testOutputDir))
                {
                    Directory.Delete(testOutputDir, true);
                }
            }
        }

        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_ForCSharp_NamesOnlyTheCSharpTooling()
        {
            // 체크리스트가 두 스택을 모두 나열하면, C# 작업을 맡은 에이전트가 MyBatis를 설치하려 하고
            // 바로 앞 장에 실린 언어별 스택 표(DataAccessPolicy)와 모순된다.
            // 패키지 설치 목록은 이제 todo.md가 아니라 부트스트랩 회차 작업 지시서가 진다.
            var bootstrapTask = await ExportAndReadBootstrapTaskAsync("C#", "cs");

            Assert.Contains("Dapper", bootstrapTask);
            Assert.Contains("EF Core", bootstrapTask);
            Assert.Contains("NetArchTest", bootstrapTask);
            Assert.DoesNotContain("MyBatis", bootstrapTask);
            Assert.DoesNotContain("Spring Data JPA", bootstrapTask);
            Assert.DoesNotContain("ArchUnit", bootstrapTask);
        }

        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_ForJava_NamesOnlyTheJavaTooling()
        {
            var bootstrapTask = await ExportAndReadBootstrapTaskAsync("Java", "java");

            Assert.Contains("MyBatis", bootstrapTask);
            Assert.Contains("Spring Data JPA", bootstrapTask);
            Assert.Contains("ArchUnit", bootstrapTask);
            Assert.DoesNotContain("Dapper", bootstrapTask);
            Assert.DoesNotContain("EF Core", bootstrapTask);
            Assert.DoesNotContain("NetArchTest", bootstrapTask);
        }

        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_ForCSharp_WritesArchitectureAndContractStubsAtCorrectPaths()
        {
            // Task 14 리뷰가 지적한 결함: DataAccessPolicy 단위 테스트는 문자열 내용만
            // 확인하고, MetadataExporter가 그 문자열을 실제로 어떤 파일 이름/경로에
            // 배치하는지는 아무 테스트도 고정하지 않았다. Java 산출물이 컴파일되지
            // 않는 결함이 바로 그 배선 지점에서 났다 - 여기서 언어별 배치를 고정한다.
            var testOutputDir = Path.Combine(
                Directory.GetCurrentDirectory(), "test_output_exporter_wiring_cs");
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }

            try
            {
                var spDefs = new System.Collections.Generic.List<SpDefinition>
                {
                    new SpDefinition { Schema = "dbo", Name = "USP_Sp1", DdlText = "CREATE PROCEDURE dbo.USP_Sp1 AS SELECT 1;" }
                };

                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    spDefs, "# Plan", VerificationOutcome.Passed, "WiringJobCs", testOutputDir, "C#",
                    new OutputPathResolver("TestDB", testOutputDir));

                var agentFolder = Path.Combine(testOutputDir, "agent");

                Assert.True(File.Exists(Path.Combine(agentFolder, "src", "AbstractSettleTasklet.cs")));
                Assert.True(File.Exists(Path.Combine(agentFolder, "src", "SettleContracts.cs")));
                Assert.True(File.Exists(Path.Combine(agentFolder, "tests", "StepLogicTests.cs")));
                Assert.True(File.Exists(Path.Combine(agentFolder, "tests", "ArchitectureTests.cs")));

                // Java 전용 파일은 C# 타깃에서는 나오지 않아야 한다. Java 쪽 테스트가
                // 고정하는 8개 파일 전부와 대칭이어야 한다 - 이 목록이 Java 쪽 파일
                // 추가를 따라가지 못하면(재리뷰에서 지적된 결함) 새 파일이 C# 타깃에도
                // 잘못 새어나가는 회귀를 이 테스트가 놓친다.
                Assert.False(File.Exists(Path.Combine(agentFolder, "src", "ISettleStep.java")));
                Assert.False(File.Exists(Path.Combine(agentFolder, "src", "AbstractSettleTasklet.java")));
                Assert.False(File.Exists(Path.Combine(agentFolder, "src", "SettleContext.java")));
                Assert.False(File.Exists(Path.Combine(agentFolder, "src", "StepResult.java")));
                Assert.False(File.Exists(Path.Combine(agentFolder, "src", "IDbConnectionFactory.java")));
                Assert.False(File.Exists(Path.Combine(agentFolder, "src", "ICheckpointRepository.java")));
                Assert.False(File.Exists(Path.Combine(agentFolder, "src", "ISettleStepDescriptor.java")));
                Assert.False(File.Exists(Path.Combine(agentFolder, "src", "ISettleRepository.java")));

                var architectureTest = await File.ReadAllTextAsync(
                    Path.Combine(agentFolder, "tests", "ArchitectureTests.cs"));
                Assert.Contains("NetArchTest.Rules", architectureTest);
            }
            finally
            {
                if (Directory.Exists(testOutputDir))
                {
                    Directory.Delete(testOutputDir, true);
                }
            }
        }

        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_ForJava_WritesArchitectureAndContractStubsAtCorrectPaths()
        {
            // Critical: ArchitectureTests.java가 com.reset.batch.core.ISettleStep /
            // AbstractSettleTasklet을 클래스 리터럴로 참조하므로, 이 두 파일이 실제로
            // agent/src에 쓰이지 않으면 javac가 즉시 실패한다. 이 테스트가 없으면
            // DataAccessPolicyTests(문자열만 확인)를 통과해도 산출물은 컴파일되지 않는다.
            var testOutputDir = Path.Combine(
                Directory.GetCurrentDirectory(), "test_output_exporter_wiring_java");
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }

            try
            {
                var spDefs = new System.Collections.Generic.List<SpDefinition>
                {
                    new SpDefinition { Schema = "dbo", Name = "USP_Sp1", DdlText = "CREATE PROCEDURE dbo.USP_Sp1 AS SELECT 1;" }
                };

                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    spDefs, "# Plan", VerificationOutcome.Passed, "WiringJobJava", testOutputDir, "Java",
                    new OutputPathResolver("TestDB", testOutputDir));

                var agentFolder = Path.Combine(testOutputDir, "agent");
                var src = (string name) => Path.Combine(agentFolder, "src", name);

                var settleStepPath = src("ISettleStep.java");
                var abstractTaskletPath = src("AbstractSettleTasklet.java");
                var settleContextPath = src("SettleContext.java");
                var stepResultPath = src("StepResult.java");
                var dbConnectionFactoryPath = src("IDbConnectionFactory.java");
                var checkpointRepositoryPath = src("ICheckpointRepository.java");
                var stepDescriptorPath = src("ISettleStepDescriptor.java");
                var repositoryPath = src("ISettleRepository.java");
                var architectureTestPath = Path.Combine(agentFolder, "tests", "ArchitectureTests.java");
                var stepLogicTestPath = Path.Combine(agentFolder, "tests", "StepLogicTests.java");

                Assert.True(File.Exists(settleStepPath), settleStepPath);
                Assert.True(File.Exists(abstractTaskletPath), abstractTaskletPath);
                Assert.True(File.Exists(settleContextPath), settleContextPath);
                Assert.True(File.Exists(stepResultPath), stepResultPath);
                Assert.True(File.Exists(dbConnectionFactoryPath), dbConnectionFactoryPath);
                Assert.True(File.Exists(checkpointRepositoryPath), checkpointRepositoryPath);
                Assert.True(File.Exists(stepDescriptorPath), stepDescriptorPath);
                Assert.True(File.Exists(repositoryPath), repositoryPath);
                Assert.True(File.Exists(architectureTestPath), architectureTestPath);
                Assert.True(File.Exists(stepLogicTestPath), stepLogicTestPath);

                // C# 전용 파일은 Java 타깃에서는 나오지 않아야 한다.
                Assert.False(File.Exists(src("AbstractSettleTasklet.cs")));
                Assert.False(File.Exists(src("SettleContracts.cs")));

                var settleStep = await File.ReadAllTextAsync(settleStepPath);
                var abstractTasklet = await File.ReadAllTextAsync(abstractTaskletPath);
                var settleContext = await File.ReadAllTextAsync(settleContextPath);
                var stepResult = await File.ReadAllTextAsync(stepResultPath);
                var dbConnectionFactory = await File.ReadAllTextAsync(dbConnectionFactoryPath);
                var checkpointRepository = await File.ReadAllTextAsync(checkpointRepositoryPath);
                var stepDescriptor = await File.ReadAllTextAsync(stepDescriptorPath);
                var repository = await File.ReadAllTextAsync(repositoryPath);
                var architectureTest = await File.ReadAllTextAsync(architectureTestPath);

                // 아키텍처 테스트가 참조하는 패키지·타입이 실제로 이 패키지에 존재해야 한다.
                Assert.Contains("package com.reset.batch.core;", settleStep);
                Assert.Contains("package com.reset.batch.core;", abstractTasklet);
                Assert.Contains("package com.reset.batch.core;", settleContext);
                Assert.Contains("package com.reset.batch.core;", stepResult);
                Assert.Contains("package com.reset.batch.core;", dbConnectionFactory);
                Assert.Contains("package com.reset.batch.core;", checkpointRepository);
                Assert.Contains("package com.reset.batch.core;", stepDescriptor);
                Assert.Contains("package com.reset.batch.core;", repository);

                // Critical(2차): AbstractSettleTasklet/ISettleStep의 확장 표면(preCheck/
                // runBusinessSteps/execute의 매개변수·반환 타입)에 나오는 모든 타입이 public
                // 이어야, 다른 패키지의 Tasklet 서브클래스가 이 시그니처를 오버라이드할 수
                // 있다. package-private으로 회귀하면 "is not public in ...; cannot be
                // accessed from outside package"로 컴파일이 깨진다 - 파일이 존재하는 것만으로는
                // 이 회귀를 못 잡으므로 가시성 리터럴을 직접 고정한다.
                Assert.Contains("public interface ISettleStep", settleStep);
                Assert.Contains("public abstract class AbstractSettleTasklet implements ISettleStep", abstractTasklet);
                Assert.Contains("public class SettleContext", settleContext);
                Assert.Contains("public class StepResult", stepResult);
                Assert.Contains("public StepResult(", stepResult);
                Assert.Contains("public interface IDbConnectionFactory", dbConnectionFactory);
                Assert.Contains("public interface ICheckpointRepository", checkpointRepository);
                Assert.Contains("public interface ISettleStepDescriptor", stepDescriptor);
                Assert.Contains("public interface ISettleRepository", repository);

                Assert.Contains("com.reset.batch.core.ISettleStep.class", architectureTest);
                Assert.Contains("com.reset.batch.core.AbstractSettleTasklet.class", architectureTest);
                Assert.Contains("package com.reset.batch.tests.architecture;", architectureTest);

                // ArchUnit의 ClassesThat에는 areNotAbstract()가 없다 - 존재하지 않는 메서드를
                // 다시 부르면 javac가 즉시 "cannot find symbol"로 죽는다.
                Assert.DoesNotContain("areNotAbstract()", architectureTest);
                Assert.Contains("doNotHaveModifier(JavaModifier.ABSTRACT)", architectureTest);

                // 부트스트랩 회차에는 Tasklet 구현체도 domain 패키지도 아직 없다. ArchUnit은
                // should절의 대상 집합이 비어 있으면 기본적으로 실패시키므로, 대상이 아직
                // 없을 뿐인 두 규칙(EverySettleStep.../DomainMustNot...)에는
                // allowEmptyShould(true)가 있어야 지시서가 "부트스트랩에서 통과시켜라"라고
                // 말하는 것이 실제로 가능하다. taskletsMustNotCreateTheirOwnConnection은
                // AbstractSettleTasklet 자신이 항상 대상 집합에 있어 필요 없다.
                Assert.Contains("everySettleStepMustExtendAbstractSettleTasklet", architectureTest);
                var everySettleStepRuleStart = architectureTest.IndexOf("everySettleStepMustExtendAbstractSettleTasklet", StringComparison.Ordinal);
                var everySettleStepRuleEnd = architectureTest.IndexOf("taskletsMustNotCreateTheirOwnConnection", StringComparison.Ordinal);
                Assert.Contains("allowEmptyShould(true)", architectureTest[everySettleStepRuleStart..everySettleStepRuleEnd]);

                var domainRuleStart = architectureTest.IndexOf("domainMustNotDependOnInfrastructure", StringComparison.Ordinal);
                Assert.Contains("allowEmptyShould(true)", architectureTest[domainRuleStart..]);

                // BOM(EF BB BF): javac가 BOM으로 시작하는 소스를 거부한다는 보고가 있다
                // (JDK-4508058). 컴파일러 없이도 바이트 자체는 고정할 수 있다.
                var javaFilePaths = new[]
                {
                    settleStepPath, abstractTaskletPath, settleContextPath, stepResultPath,
                    dbConnectionFactoryPath, checkpointRepositoryPath, stepDescriptorPath, repositoryPath,
                    architectureTestPath, stepLogicTestPath,
                };
                foreach (var path in javaFilePaths)
                {
                    var firstBytes = await File.ReadAllBytesAsync(path);
                    var startsWithBom = firstBytes.Length >= 3
                        && firstBytes[0] == 0xEF && firstBytes[1] == 0xBB && firstBytes[2] == 0xBF;
                    Assert.False(startsWithBom, $"{path}가 UTF-8 BOM으로 시작한다");
                }
            }
            finally
            {
                if (Directory.Exists(testOutputDir))
                {
                    Directory.Delete(testOutputDir, true);
                }
            }
        }

        private static async Task<string> ExportAndReadBootstrapTaskAsync(string targetLanguage, string dirSuffix)
        {
            var testOutputDir = Path.Combine(
                Directory.GetCurrentDirectory(), $"test_output_exporter_tooling_{dirSuffix}");
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }

            try
            {
                var spDefs = new System.Collections.Generic.List<SpDefinition>
                {
                    new SpDefinition
                    {
                        Schema = "dbo",
                        Name = "USP_Sp1",
                        DdlText = "CREATE PROCEDURE dbo.USP_Sp1 AS SELECT 1;"
                    }
                };

                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    spDefs,
                    "# Plan",
                    VerificationOutcome.Passed,
                    "ToolingJob",
                    testOutputDir,
                    targetLanguage,
                    new OutputPathResolver("TestDB", testOutputDir));

                return await File.ReadAllTextAsync(
                    Path.Combine(testOutputDir, "agent", "task-00-bootstrap.md"));
            }
            finally
            {
                if (Directory.Exists(testOutputDir))
                {
                    Directory.Delete(testOutputDir, true);
                }
            }
        }
    }
}
