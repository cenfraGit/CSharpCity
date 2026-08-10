namespace CSharpCity.Layout.Tests;

/// <summary>
/// Guards the one property <see cref="StableHash"/> exists for.
/// </summary>
/// <remarks>
/// The city used to be built partly on <c>HashCode.Combine</c>, which is seeded randomly once per
/// process — so "deterministic variety from the building's position" was neither deterministic nor
/// reproducible, and the same solution came out with a different number of trees and props every
/// run. Nothing caught it: every count the console reported was computed from integers, so the
/// notes matched perfectly while the scene underneath did not.
///
/// The expected values here are computed by an independent implementation of the same mix rather
/// than pasted in as magic numbers. That way the test fails if the algorithm is swapped for a
/// randomised one, which is the actual failure worth catching, and not merely if it is retuned.
/// </remarks>
public class StableHashTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-7)]
    [InlineData(int.MaxValue)]
    public void CombineMatchesAnIndependentImplementation(int seed)
    {
        int[] values = { seed, seed * 3 + 1, seed ^ 0x5F5F };

        Assert.Equal(Reference(values), StableHash.Combine(values));
    }

    [Fact]
    public void UnitStaysInRangeAndSpreadsOut()
    {
        var samples = Enumerable.Range(0, 500).Select(i => StableHash.Unit(i, i * 7)).ToList();

        Assert.All(samples, s => Assert.InRange(s, 0f, 1f));
        // Four buckets, none starved: enough to catch a mix that has collapsed to a constant.
        for (int bucket = 0; bucket < 4; bucket++)
            Assert.True(samples.Count(s => (int)(s * 4f) == bucket) > 50,
                $"Bucket {bucket} holds too few of 500 samples; the mix is not spreading.");
    }

    [Fact]
    public void DifferentInputsGiveDifferentAnswers()
    {
        Assert.NotEqual(StableHash.Combine(1, 2), StableHash.Combine(2, 1));
        Assert.NotEqual(StableHash.Combine(1, 2), StableHash.Combine(1, 3));
    }

    static int Reference(int[] values)
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
}
