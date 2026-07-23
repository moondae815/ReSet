using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class NullProgressScopeTests
    {
        [Fact]
        public void Instance_ShouldReturnNonNullInstance()
        {
            var instance = NullProgressScope.Instance;
            Assert.NotNull(instance);
        }

        [Fact]
        public void Methods_ShouldExecuteWithoutExceptions()
        {
            var instance = NullProgressScope.Instance;
            var taskName = "test_task";

            // All these methods have empty bodies, so they should just return normally without exception
            instance.AddTask(taskName, "Test Description");
            instance.UpdateTask(taskName, 50.0, "Updating");
            instance.CompleteTask(taskName);
            instance.FailTask(taskName);
            instance.Dispose();

            // Dummy assertion to ensure the test passes when reaching here
            Assert.True(true);
        }
    }
}
