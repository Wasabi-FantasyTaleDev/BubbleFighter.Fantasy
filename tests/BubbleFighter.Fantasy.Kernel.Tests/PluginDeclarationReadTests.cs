using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>
/// The kernel reads a descriptor's captured declaration without copying it, while the public declaration stays a
/// defensive copy that plugin code cannot use to change what the kernel schedules by.
/// </summary>
public sealed class PluginDeclarationReadTests
{
    private static PluginDescriptor Descriptor()
    {
        var type = typeof(DecoratorPluginTests.Consumer);
        return new PluginDescriptor("decorator.consumer", null, type, "test", null);
    }

    [Fact]
    public void KernelReadsTheCapturedDeclarationWithoutAllocating()
    {
        var descriptor = Descriptor();
        var first = descriptor.ReadOnlyDeclaration;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var again = descriptor.ReadOnlyDeclaration;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(first, again);
        Assert.Equal(0, allocated);
        Assert.Equal([typeof(DecoratorPluginTests.IText)], again.Requires);
    }

    [Fact]
    public void PublicDeclarationIsACopyThatCannotChangeTheKernelsView()
    {
        var descriptor = Descriptor();

        var copy = descriptor.Declaration;
        copy.Requires[0] = typeof(IPluginHost);

        Assert.NotSame(copy, descriptor.ReadOnlyDeclaration);
        Assert.Equal([typeof(DecoratorPluginTests.IText)], descriptor.ReadOnlyDeclaration.Requires);
        Assert.Equal([typeof(DecoratorPluginTests.IText)], descriptor.Declaration.Requires);
    }
}
