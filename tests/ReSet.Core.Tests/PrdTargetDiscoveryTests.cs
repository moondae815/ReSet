using System;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdTargetDiscoveryTests : IDisposable
    {
        private readonly string _root;

        public PrdTargetDiscoveryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "reset-discovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Seed(string relativeObjectDir, bool withSpec, bool withPrd)
        {
            var docs = Path.Combine(_root, relativeObjectDir, "docs");
            Directory.CreateDirectory(docs);
            if (withSpec) File.WriteAllText(Path.Combine(docs, "Spec.md"), "## 개요");
            if (withPrd) File.WriteAllText(Path.Combine(docs, "Prd.md"), "## 배경 및 목적");
        }

        [Fact]
        public void Find_ShouldListOnlyObjectsThatHaveASpec()
        {
            Seed(Path.Combine("Procedures", "dbo.UP_A"), withSpec: true, withPrd: false);
            Seed(Path.Combine("Procedures", "dbo.UP_B"), withSpec: false, withPrd: false);

            var targets = PrdTargetDiscovery.Find(_root);

            Assert.Single(targets);
            Assert.Equal("dbo.UP_A", targets[0].Label);
        }

        [Fact]
        public void Find_ShouldFlagObjectsThatAlreadyHaveAPrd()
        {
            Seed(Path.Combine("Procedures", "dbo.UP_A"), withSpec: true, withPrd: true);

            var targets = PrdTargetDiscovery.Find(_root);

            Assert.True(targets[0].HasExistingPrd);
        }

        [Fact]
        public void Find_ShouldIgnoreFunctionsAndExternal()
        {
            // 1차 범위는 Procedures 뿐이다(설계 §7.1).
            Seed(Path.Combine("Procedures", "dbo.UP_A"), withSpec: true, withPrd: false);
            Seed(Path.Combine("Functions", "dbo.UF_A"), withSpec: true, withPrd: false);
            Seed(Path.Combine("External", "OtherDb", "Procedures", "dbo.UP_C"), withSpec: true, withPrd: false);

            var targets = PrdTargetDiscovery.Find(_root);

            Assert.Single(targets);
            Assert.Equal("dbo.UP_A", targets[0].Label);
        }

        [Fact]
        public void Find_ShouldReturnEmpty_WhenOutputRootDoesNotExist()
        {
            var targets = PrdTargetDiscovery.Find(Path.Combine(_root, "nope"));

            Assert.Empty(targets);
        }

        [Fact]
        public void Find_ShouldSortByLabel()
        {
            Seed(Path.Combine("Procedures", "dbo.UP_B"), withSpec: true, withPrd: false);
            Seed(Path.Combine("Procedures", "dbo.UP_A"), withSpec: true, withPrd: false);

            var targets = PrdTargetDiscovery.Find(_root);

            Assert.Equal(new[] { "dbo.UP_A", "dbo.UP_B" }, targets.Select(t => t.Label).ToArray());
        }
    }
}
