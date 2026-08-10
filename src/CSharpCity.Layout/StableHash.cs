namespace CSharpCity.Layout;

/// <summary>
/// A hash that gives the same answer in every process, for the scattered "deterministic variety"
/// the city is built from.
/// </summary>
/// <remarks>
/// This exists because <see cref="System.HashCode"/> does not. <c>HashCode.Combine</c> is seeded
/// from a random value chosen once per process, deliberately, to make hash-flooding attacks
/// impractical — which is the right default for a dictionary and exactly wrong for anything meant to
/// be reproducible. Several places used it to pick tree counts and prop placement "deterministically
/// from position", and the result was a city that quietly came out slightly different every run:
/// same buildings, same districts, same everything the console reported, but a box count that
/// wandered by thirty or forty each time. Nothing pointed at it, because everything anyone thought
/// to count was computed from integers.
/// </remarks>
internal static class StableHash
{
    /// <summary>Mixes values into a 32-bit hash. Same inputs, same output, forever.</summary>
    public static int Combine(params int[] values)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (int value in values)
            {
                h ^= (uint)value;
                h *= 16777619u;
                h ^= h >> 13;
            }
            return (int)h;
        }
    }

    /// <summary>The same mix, folded to the 0..1 range most callers actually want.</summary>
    public static float Unit(params int[] values)
    {
        unchecked
        {
            uint x = (uint)Combine(values) * 2654435761u;
            x ^= x >> 15;
            x *= 2246822519u;
            x ^= x >> 13;
            return (x & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }
}
