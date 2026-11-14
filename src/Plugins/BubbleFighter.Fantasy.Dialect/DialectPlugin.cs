using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;

namespace BubbleFighter.Fantasy.Dialect;

/// <summary>
/// Publishes the wire dialect of one client build, described by its profile row: the frame layout and
/// cipher tables come from the row's config, region and version from its <c>client_build</c>.
/// </summary>
[Plugin("dialect", Category = "network", Names = ["en=Client dialect", "zh-Hans=客户端方言", "zh-Hant=用戶端方言", "ko=클라이언트 방언"], Description = "Client wire dialect, from the profile's frame and cipher data.", ProvidesMany = [typeof(IWireDialectProvider)])]
public sealed class DialectPlugin : ConfiguredDialectPlugin
{
}
