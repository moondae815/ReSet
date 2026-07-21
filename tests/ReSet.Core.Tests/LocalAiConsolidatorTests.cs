using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class LocalAiConsolidatorTests
    {
        [Fact]
        public void Consolidate_ShouldMergeMultipleChunksIntoOne()
        {
            // Arrange
            var chunk1 = new DeconstructedSpLogic
            {
                Crud = new SpCrudInfo { UpdateTables = new List<SpUpdateMappingInfo> { new SpUpdateMappingInfo { TargetTable = "dbo.TableA" } } },
                Logic = new SpLogicInfo { Steps = new List<SpLogicStep> { new SpLogicStep { StepDescription = "Update A" } } },
                Visualization = new SpVisualizationInfo { Nodes = new List<MermaidNode> { new MermaidNode { Id = "N1", Label = "Node1" } } }
            };

            var chunk2 = new DeconstructedSpLogic
            {
                Crud = new SpCrudInfo { InsertTables = new List<SpInsertMappingInfo> { new SpInsertMappingInfo { TargetTable = "dbo.TableB" } } },
                Logic = new SpLogicInfo { Steps = new List<SpLogicStep> { new SpLogicStep { StepDescription = "Insert B" } } },
                Visualization = new SpVisualizationInfo { Nodes = new List<MermaidNode> { new MermaidNode { Id = "N2", Label = "Node2" } } }
            };

            var overview = new SpOverviewInfo { SpName = "TestProc" };
            var parameters = new List<SpParameterInfo>();

            var consolidator = new LocalAiConsolidator();

            // Act
            var finalResult = consolidator.Consolidate(new List<DeconstructedSpLogic> { chunk1, chunk2 }, overview, parameters);

            // Assert
            Assert.Equal("TestProc", finalResult.Overview.SpName);
            Assert.Single(finalResult.Crud.UpdateTables);
            Assert.Equal("dbo.TableA", finalResult.Crud.UpdateTables[0].TargetTable);
            Assert.Single(finalResult.Crud.InsertTables);
            Assert.Equal("dbo.TableB", finalResult.Crud.InsertTables[0].TargetTable);

            Assert.Equal(2, finalResult.Logic.Steps.Count);
            Assert.Equal(1, finalResult.Logic.Steps[0].StepNumber);
            Assert.Equal(2, finalResult.Logic.Steps[1].StepNumber);

            Assert.Equal(2, finalResult.Visualization.Nodes.Count);
        }
    }
}
