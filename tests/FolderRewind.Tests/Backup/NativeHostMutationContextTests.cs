using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeHostMutationContextTests
{
    [TestMethod]
    public async Task CoordinatorCallbackBlocksNestedMutationButSuppliedContinuationIsAllowed()
    {
        Assert.IsFalse(NativeHostMutationContext.IsNestedMutationBlocked);
        using (NativeHostMutationContext.EnterCoordinatorCallback())
        {
            Assert.IsTrue(NativeHostMutationContext.IsNestedMutationBlocked);
            var error = Assert.ThrowsExactly<InvalidOperationException>(
                NativeHostMutationContext.ThrowIfNestedMutation);
            Assert.AreEqual("NestedHostMutationNotAllowed", error.Message);

            var allowed = await NativeHostMutationContext.RunSuppliedContinuationAsync(() =>
            {
                NativeHostMutationContext.ThrowIfNestedMutation();
                return Task.FromResult(true);
            });
            Assert.IsTrue(allowed);
            Assert.IsTrue(NativeHostMutationContext.IsNestedMutationBlocked);
        }
        Assert.IsFalse(NativeHostMutationContext.IsNestedMutationBlocked);
    }
}
