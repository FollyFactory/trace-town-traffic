using TraceTown.Traffic.Emit;

namespace TraceTown.Traffic.Tests;

/// <summary>Reaches the internal rename table without widening the public API.</summary>
internal static class SemConvNames
{
    internal static (string Current, string Superseded)[] SpanRenames => SemConv.RenamedSpanAttributes;

    internal static (string Current, string Superseded)[] ResourceRenames => SemConv.RenamedResourceAttributes;
}
