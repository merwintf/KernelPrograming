public sealed class TimeInfo
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
}

public static List<TimeInfo> CompressTimes(IEnumerable<TimeInfo> items)
{
    var sorted = items
        .OrderBy(x => x.Start)
        .ThenBy(x => x.End)
        .ToList();

    if (sorted.Count == 0)
        return new List<TimeInfo>();

    var result = new List<TimeInfo>();
    var current = sorted[0];

    foreach (var item in sorted.Skip(1))
    {
        // Merge when current.End matches next.Start
        // Also handles overlapping ranges using <=
        if (item.Start <= current.End)
        {
            current.End = item.End > current.End
                ? item.End
                : current.End;
        }
        else
        {
            result.Add(current);
            current = item;
        }
    }

    result.Add(current);

    return result;
}
