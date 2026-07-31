using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    public interface ICacheManager
    {
        string ComputeCompositeHash(SpDefinition spDef, int maxDepth);
        bool IsCacheValid(
            CodeObjectKey objectKey,
            string compositeHash,
            OutputPathResolver outputPaths);
        void UpdateCache(
            CodeObjectKey objectKey,
            SpDefinition spDef,
            string compositeHash,
            OutputPathResolver outputPaths);
    }
}
