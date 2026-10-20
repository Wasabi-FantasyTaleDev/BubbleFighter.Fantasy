using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>
/// Runs compiler-backed SDK tests without overlapping process deadline tests, so native and
/// WebAssembly compilation cannot consume the CPU needed by a plugin's initialization reply.
/// </summary>
[CollectionDefinition("SDK compilation", DisableParallelization = true)]
public sealed class SdkCompilationCollection;
