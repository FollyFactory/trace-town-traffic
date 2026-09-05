using System.Diagnostics;
using System.Globalization;
using System.Text;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Control;

/// <summary>
/// Renders a simulated trace as a tree on the console, for <c>--dry-run</c>.
/// Being able to see the shape of what would be exported, without standing up a
/// backend first, is the difference between debugging a config in a minute and
/// debugging it in an afternoon.
/// </summary>
public static class TracePrinter
{
    public static string Render(SimulatedRequest request)
    {
        var output = new StringBuilder();
        double total = request.Root.Descend().Max(s => s.EndOffsetMs);

        output.Append("flow: ").Append(request.FlowId)
            .Append("   total: ").Append(total.ToString("F1", CultureInfo.InvariantCulture)).Append("ms")
            .Append("   spans: ").Append(request.Root.Descend().Count())
            .Append("   outcome: ").Append(request.Failed ? "FAILED" : "ok")
            .AppendLine();

        Render(request.Root, output, prefix: string.Empty, isLast: true, isRoot: true);
        return output.ToString();
    }

    private static void Render(SpanPlan plan, StringBuilder output, string prefix, bool isLast, bool isRoot)
    {
        string connector = isRoot ? string.Empty : isLast ? "└─ " : "├─ ";
        string label = $"{prefix}{connector}{plan.Name}";

        output.Append(label.PadRight(58)[..Math.Max(58, label.Length)])
            .Append("  ")
            .Append(Kind(plan.Kind).PadRight(9))
            .Append(plan.Emitter.Id.PadRight(22))
            .Append(plan.DurationMs.ToString("F1", CultureInfo.InvariantCulture).PadLeft(8))
            .Append("ms");

        if (plan.Peer is { } peer && !plan.Children.Any(c => c.Emitter.Id == peer.Id))
        {
            // No child span from the far side means nothing over there
            // instruments itself — worth showing, since it is the single most
            // misunderstood thing about tracing infrastructure.
            output.Append("   → ").Append(peer.Id).Append(" (no server span)");
        }

        if (plan.Failed)
        {
            output.Append("   ✗ ").Append(plan.ErrorType);
        }

        output.AppendLine();

        string childPrefix = prefix + (isRoot ? string.Empty : isLast ? "   " : "│  ");
        for (int i = 0; i < plan.Children.Count; i++)
        {
            Render(plan.Children[i], output, childPrefix, i == plan.Children.Count - 1, isRoot: false);
        }
    }

    private static string Kind(ActivityKind kind) => kind switch
    {
        ActivityKind.Server => "SERVER",
        ActivityKind.Client => "CLIENT",
        ActivityKind.Producer => "PRODUCER",
        ActivityKind.Consumer => "CONSUMER",
        _ => "INTERNAL",
    };
}
