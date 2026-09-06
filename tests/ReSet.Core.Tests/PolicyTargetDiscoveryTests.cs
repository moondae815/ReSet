using System;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyTargetDiscoveryTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "reset-policy-" + Guid.NewGuid().ToString("N"));

        private void Seed(string label, bool withSpec, bool withMetadata)
        {
            var docs = Path.Combine(_root, "Procedures", label, "docs");
            var raw = Path.Combine(_root, "Procedures", label, "raw");
            Directory.CreateDirectory(docs);
            Directory.CreateDirectory(raw);
            if (withSpec) File.WriteAllText(Path.Combine(docs, "Spec.md"), "## 개요\n\n본문\n");
            if (withMetadata) File.WriteAllText(Path.Combine(raw, "metadata.json"), "{}");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void 명세서가_있는_대상만_찾는다()
        {
            Seed("dbo.A", withSpec: true, withMetadata: true);
            Seed("dbo.B", withSpec: false, withMetadata: true);

            var targets = PolicyTargetDiscovery.Find(_root);

            Assert.Equal(new[] { "dbo.A" }, targets.Select(t => t.Label));
        }

        [Fact]
        public void 라벨_사전순으로_돌려준다()
        {
            Seed("dbo.Z", withSpec: true, withMetadata: true);
            Seed("dbo.A", withSpec: true, withMetadata: true);

            var targets = PolicyTargetDiscovery.Find(_root);

            Assert.Equal(new[] { "dbo.A", "dbo.Z" }, targets.Select(t => t.Label));
        }

        [Fact]
        public void Procedures_디렉터리가_없으면_빈_목록이다()
        {
            Directory.CreateDirectory(_root);

            Assert.Empty(PolicyTargetDiscovery.Find(_root));
        }
    }
}
