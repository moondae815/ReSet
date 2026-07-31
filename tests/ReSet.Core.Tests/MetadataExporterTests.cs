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
                Assert.True(File.Exists(Path.Combine(rawDirectory, "ddl", "procedures", "dbo.USP_Child.sql")));
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

            // Act
            await exporter.ExportConsolidatedMigrationInstructionsAsync(spDefs, consolidatedPlan, jobName, testOutputDir, "C#");

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
    }
}
