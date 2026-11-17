using Dirused.Common.Renderers;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class FullnessColorTests
{
    [Fact]
    public void Test_ShouldColorOnlyForRealUnredirectedConsole()
    {
        Assert.True(Fullness.ShouldColor(Console.Out, true, false, null));

        Assert.False(Fullness.ShouldColor(Console.Out, false, false, null));
        Assert.False(Fullness.ShouldColor(new StringWriter(), true, false, null));
        Assert.False(Fullness.ShouldColor(Console.Out, true, true, null));
        Assert.False(Fullness.ShouldColor(Console.Out, true, false, "1"));
    }
}
