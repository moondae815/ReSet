using System.Collections.Generic;
using Xunit;
using ReSet.Core.Models;

namespace ReSet.Core.Tests
{
    public class ModelTests
    {
        [Fact]
        public void SpSelectTableInfo_Properties_AreGettableAndSettable()
        {
            var model = new SpSelectTableInfo();
            model.TableName = "test";
            model.ReferencedColumns = new List<string> { "col1" };
            model.JoinAndFilterConditions = new List<string> { "id=1" };
            
            Assert.Equal("test", model.TableName);
            Assert.Single(model.ReferencedColumns);
            Assert.Single(model.JoinAndFilterConditions);
        }

        [Fact]
        public void ColumnMappingInfo_Properties_AreGettableAndSettable()
        {
            var model = new ColumnMappingInfo();
            model.TargetColumn = "target";
            model.SourceExpression = "source";
            model.Description = "desc";

            Assert.Equal("target", model.TargetColumn);
            Assert.Equal("source", model.SourceExpression);
            Assert.Equal("desc", model.Description);
        }

        [Fact]
        public void SpDeleteTableInfo_Properties_AreGettableAndSettable()
        {
            var model = new SpDeleteTableInfo();
            model.TableName = "test";
            model.BranchName = "b1";
            model.FilterConditions = new List<string> { "id=1" };

            Assert.Equal("test", model.TableName);
            Assert.Equal("b1", model.BranchName);
            Assert.Single(model.FilterConditions);
        }

        [Fact]
        public void SpUdfInfo_Properties_AreGettableAndSettable()
        {
            var model = new SpUdfInfo();
            model.UdfName = "func";
            model.CallingLocation = "loc";
            model.Purpose = "purp";
            model.ComputationLogic = "logic";

            Assert.Equal("func", model.UdfName);
            Assert.Equal("loc", model.CallingLocation);
            Assert.Equal("purp", model.Purpose);
            Assert.Equal("logic", model.ComputationLogic);
        }

        [Fact]
        public void SpExceptionVulnerability_Properties_AreGettableAndSettable()
        {
            var model = new SpExceptionVulnerability();
            model.VulnerabilityType = "vuln";
            model.Details = "det";

            Assert.Equal("vuln", model.VulnerabilityType);
            Assert.Equal("det", model.Details);
        }

        [Fact]
        public void SpIsolationImplication_Properties_AreGettableAndSettable()
        {
            var model = new SpIsolationImplication();
            model.RiskType = "risk";
            model.Details = "det2";

            Assert.Equal("risk", model.RiskType);
            Assert.Equal("det2", model.Details);
        }

        [Fact]
        public void MermaidLink_Properties_AreGettableAndSettable()
        {
            var model = new MermaidLink();
            model.FromId = "n1";
            model.ToId = "n2";
            model.Condition = "lbl";

            Assert.Equal("n1", model.FromId);
            Assert.Equal("n2", model.ToId);
            Assert.Equal("lbl", model.Condition);
        }
    }
}
