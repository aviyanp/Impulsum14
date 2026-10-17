namespace Impulsum14;

internal static class AccountRequestLimit
{
    // One monotonic fixed-second window, shared by HTTP and RPC for the account.
    // Keeping clock acquisition outside makes concurrency and rollover testable.
    internal static bool TryAccept(ref long state, int limit, uint second)
    {
        while (true)
        {
            long previous = Volatile.Read(ref state);
            uint previousSecond = (uint)((ulong)previous >> 32);
            uint previousCount = (uint)previous;
            // A thread paused at the boundary must not reset a newer window back
            // to its old second and thereby replenish the new second twice.
            bool stale = previousCount != 0 && unchecked((int)(second - previousSecond)) < 0;
            bool sameWindow = previousSecond == second || stale;
            uint count = sameWindow ? previousCount : 0;
            if (count >= limit) return false;
            uint nextSecond = sameWindow ? previousSecond : second;
            long next = unchecked((long)(((ulong)nextSecond << 32) | (count + 1)));
            if (Interlocked.CompareExchange(ref state, next, previous) == previous) return true;
        }
    }
}
