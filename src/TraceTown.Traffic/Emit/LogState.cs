using System.Collections;

namespace TraceTown.Traffic.Emit;

/// <summary>
/// A log state the OpenTelemetry logging provider can read attributes from
/// directly, rather than having to parse them back out of a formatted string.
/// </summary>
internal sealed class LogState(string message, IReadOnlyList<KeyValuePair<string, object?>> attributes)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    public int Count => attributes.Count + 1;

    public KeyValuePair<string, object?> this[int index]
        => index == attributes.Count
            ? new KeyValuePair<string, object?>("{OriginalFormat}", message)
            : attributes[index];

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        foreach (KeyValuePair<string, object?> attribute in attributes)
        {
            yield return attribute;
        }

        yield return new KeyValuePair<string, object?>("{OriginalFormat}", message);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => message;
}
