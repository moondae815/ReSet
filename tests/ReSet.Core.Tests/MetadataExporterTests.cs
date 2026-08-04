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

        [Fact]
        public async Task ExportCodeObjectArtifactsAsync_WritesDefinitionPromptContextEvenWhenArgumentIsOmitted()
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

                var promptPath = Path.Combine(outputRoot, "Objects", "dbo.USP_Prompt.Procedure", "raw", "prompt-context.md");
                Assert.Equal("actual prompt body", await File.ReadAllTextAsync(promptPath));
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

                var promptPath = Path.Combine(outputRoot, "Objects", "dbo.USP_EmptyPrompt.Procedure", "raw", "prompt-context.md");
                Assert.True(File.Exists(promptPath));
                Assert.Equal(string.Empty, await File.ReadAllTextAsync(promptPath));
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
            Assert.Contains(consolidatedPlan, content);
            var tableSchemasPath1 = Path.Combine(testOutputDir, "raw", "ddl", "dbo.TBL_TestDep.md");
            
            Assert.True(File.Exists(tableSchemasPath1));

            var context1 = await File.ReadAllTextAsync(tableSchemasPath1);
            Assert.DoesNotContain("CREATE PROCEDURE dbo.USP_Sp1 AS SELECT 1;", context1);
            Assert.Contains("TBL_TestDep", context1);
            Assert.Contains("의존 테이블 설명", context1);
            
            Assert.Contains("[raw/ddl/dbo.TBL_TestDep.md]", content);
            Assert.Contains("todo.md", content);

            var todoContent = await File.ReadAllTextAsync(expectedTodoPath);
            Assert.Contains($"# 📋 {jobName} 통합 배치 마이그레이션 구현 체크리스트", todoContent);

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
                Assert.Contains("명세서 파일을 찾을 수 없습니다", instructions);
                Assert.DoesNotContain("[Spec.md](", instructions);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        // 지시서 번들은 계획서 본문을 그대로 심는다(MetadataExporter.cs의 1번 항목).
        // L1 소진/품질 미달/리뷰 미수행 세 상태는 배너가 계획서 문자열 자체에 붙어
        // 번들까지 따라오지만, L3 피드백 재생성 경로는 계획서를 통째로 교체하므로
        // 배너가 사라지고 YAML 헤더에만 의존한다 - 그 헤더는 번들에 들어가지 않는다.
        // 그래서 번들은 문자열에 무엇이 우연히 들어 있는지가 아니라 종료 상태 값을
        // 직접 받아 자기 검증 상태를 밝힌다.
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

                // 상태 고지가 계획 본문보다 먼저 와야 한다. 코딩 에이전트는 위에서부터
                // 읽으므로, 계획을 소비한 뒤에 경고를 만나면 이미 늦다.
                Assert.True(
                    instructions.IndexOf("0. 이 계획서의 검증 상태", StringComparison.Ordinal) <
                    instructions.IndexOf("1. 통합 배치 전환 계획", StringComparison.Ordinal));
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
                // 배치 호스팅과 멀티 DB 설정 안내는 그대로 남는다.
                Assert.Contains("Worker Service", instructions);
                Assert.Contains("ConnectionStrings", instructions);

                var todo = await File.ReadAllTextAsync(
                    Path.Combine(testOutputDir, "agent", "todo.md"));
                Assert.Contains("EF Core", todo);
                Assert.Contains("경계 규칙", todo);

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
    }
}
