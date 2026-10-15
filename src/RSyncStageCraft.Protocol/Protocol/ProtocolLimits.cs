namespace RSyncStageCraft.Core.Protocol;

/// <summary>Wire limits shared by every client and server.</summary>
public static class ProtocolLimits
{
    public const int MaximumControlMessageBytes = 16 * 1024 * 1024;
    public const int MaximumObjectsPerSnapshot = 4096;
    public const int MaximumAuthoritativeObjectEvents = 8192;
    public const int MaximumIdentifierLength = 256;

    internal static bool HasValidCombinedCount(
        int firstCount,
        int secondCount,
        int maximumCount) =>
        firstCount >= 0 &&
        secondCount >= 0 &&
        firstCount <= maximumCount &&
        secondCount <= maximumCount - firstCount;

    public static bool TryCopyBoundedPair<TFirst, TSecond>(
        IReadOnlyList<TFirst>? first,
        IReadOnlyList<TSecond>? second,
        int maximumCount,
        out TFirst[] firstCopy,
        out TSecond[] secondCopy)
    {
        firstCopy = [];
        secondCopy = [];
        if (first is null || second is null)
            return false;

        try
        {
            var firstCount = first.Count;
            var secondCount = second.Count;
            if (!HasValidCombinedCount(firstCount, secondCount, maximumCount) ||
                !TryCopyExactCount(first, firstCount, out firstCopy) ||
                !TryCopyExactCount(second, secondCount, out secondCopy))
            {
                firstCopy = [];
                secondCopy = [];
                return false;
            }

            return true;
        }
        catch
        {
            firstCopy = [];
            secondCopy = [];
            return false;
        }
    }

    public static bool TryCopyBounded<T>(
        IReadOnlyList<T>? source,
        int maximumCount,
        out T[] copy)
    {
        copy = [];
        if (source is null)
            return false;

        try
        {
            var count = source.Count;
            if (count < 0 || count > maximumCount ||
                !TryCopyExactCount(source, count, out copy))
            {
                copy = [];
                return false;
            }

            return true;
        }
        catch
        {
            copy = [];
            return false;
        }
    }

    private static bool TryCopyExactCount<T>(
        IReadOnlyList<T> source,
        int expectedCount,
        out T[] copy)
    {
        copy = new T[expectedCount];
        using var enumerator = source.GetEnumerator();
        for (var index = 0; index < expectedCount; index++)
        {
            if (!enumerator.MoveNext())
                return false;

            copy[index] = enumerator.Current;
        }

        return !enumerator.MoveNext();
    }
}
