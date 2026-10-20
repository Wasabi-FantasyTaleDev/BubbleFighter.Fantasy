using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// The kernel refuses a publication the plugin did not declare, and only at activation. This
/// catches a binding or encoder added to the play-room codec without its declaration.
/// </summary>
public sealed class PlayRoomDeclarationTests
{
    [Fact]
    public void EveryBindingAndEncoderOfThePlayRoomCodecIsDeclaredByThePlugin()
    {
        var declared = typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!.ConditionalProvidesMany;
        var published = typeof(PlayRoomCodec).GetInterfaces().Where(type => type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(IInboundMessageBinding<>)
                || type.GetGenericTypeDefinition() == typeof(IMessageSender<>)));

        Assert.Empty(published.Except(declared).Select(type => type.ToString()));
    }
}
