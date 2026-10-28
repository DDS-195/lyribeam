namespace LyricBridge;

internal static class KugouCandidateOrder
{
    internal const int MaximumCandidates = 20;

    internal static List<T> Interleave<T>(IEnumerable<T> candidates, Func<T,string> id,
        Func<T,double> score, Func<T,double> difference, Func<T,string> groupKey)
    {
        var ranked = candidates.OrderByDescending(score).ThenBy(difference)
            .DistinctBy(id, StringComparer.Ordinal).ToList();
        var result = new List<T>(Math.Min(ranked.Count, MaximumCandidates));
        // Exhaust each confidence tier before weaker matches. Inside a tier,
        // sample each title/duration family before another similar upload.
        foreach (var tier in ranked.GroupBy(score))
        {
            var families = tier.GroupBy(groupKey, StringComparer.Ordinal)
                .Select(group => new Queue<T>(group)).ToArray();
            bool remaining;
            do
            {
                remaining = false;
                foreach (var family in families)
                {
                    if (family.Count == 0) continue;
                    result.Add(family.Dequeue());
                    if (result.Count == MaximumCandidates) return result;
                    remaining |= family.Count > 0;
                }
            } while (remaining);
        }
        return result;
    }
}
