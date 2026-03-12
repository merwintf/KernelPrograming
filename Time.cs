public static bool TryGetOverlap(
    DateTime rangeStart,
    DateTime rangeEnd,
    DateTime? filterStart,
    DateTime? filterEnd,
    out DateTime overlapStart,
    out DateTime overlapEnd)
{
    if (rangeStart > rangeEnd)
        throw new ArgumentException("rangeStart cannot be after rangeEnd");

    if (filterStart.HasValue && filterEnd.HasValue && filterStart > filterEnd)
        throw new ArgumentException("filterStart cannot be after filterEnd");

    // If filter bound is null, treat it as open-ended
    DateTime effectiveFilterStart = filterStart ?? DateTime.MinValue;
    DateTime effectiveFilterEnd = filterEnd ?? DateTime.MaxValue;

    overlapStart = rangeStart > effectiveFilterStart ? rangeStart : effectiveFilterStart;
    overlapEnd = rangeEnd < effectiveFilterEnd ? rangeEnd : effectiveFilterEnd;

    return overlapStart <= overlapEnd;
}
