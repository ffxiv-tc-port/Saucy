#nullable disable
namespace Saucy.TripleTriad.GameLogic;

/// <summary>
///     Produces exactly the sequence of <c>new System.Random(seed).Next(maxValue)</c> (the seeded generator .NET keeps
///     compatible with .NET Framework), but <see cref="Reseed" /> refills the same state array instead of allocating.
///     Not thread-safe; one instance per agent, like the <c>System.Random</c> it replaces.
/// </summary>
internal sealed class TriadSeededRandom
{
    private const int MBig = int.MaxValue;
    private const int MSeed = 161803398;

    private readonly int[] seedArray = new int[56];
    private int inext;
    private int inextp;

    public void Reseed(int seed)
    {
        var subtraction = (seed == int.MinValue) ? int.MaxValue : System.Math.Abs(seed);
        var mj = MSeed - subtraction;
        seedArray[55] = mj;
        var mk = 1;
        var ii = 0;
        for (var i = 1; i < 55; i++)
        {
            if ((ii += 21) >= 55)
            {
                ii -= 55;
            }

            seedArray[ii] = mk;
            mk = mj - mk;
            if (mk < 0)
            {
                mk += MBig;
            }

            mj = seedArray[ii];
        }

        for (var k = 1; k < 5; k++)
        {
            for (var i = 1; i < 56; i++)
            {
                var n = i + 30;
                if (n >= 55)
                {
                    n -= 55;
                }

                seedArray[i] -= seedArray[1 + n];
                if (seedArray[i] < 0)
                {
                    seedArray[i] += MBig;
                }
            }
        }

        inext = 0;
        inextp = 21;
    }

    /// <summary>Same as <c>System.Random.Next(maxValue)</c> for a non-negative <paramref name="maxValue" />.</summary>
    public int Next(int maxValue) => (int)(InternalSample() * (1.0 / MBig) * maxValue);

    private int InternalSample()
    {
        var locINext = inext;
        if (++locINext >= 56)
        {
            locINext = 1;
        }

        var locINextp = inextp;
        if (++locINextp >= 56)
        {
            locINextp = 1;
        }

        var retVal = seedArray[locINext] - seedArray[locINextp];
        if (retVal == MBig)
        {
            retVal--;
        }

        if (retVal < 0)
        {
            retVal += MBig;
        }

        seedArray[locINext] = retVal;
        inext = locINext;
        inextp = locINextp;
        return retVal;
    }
}
