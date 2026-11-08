using System;

namespace CatPatchspace.SprayFallback;

// Token buckets bound cumulative work, including offers later cancelled by a peer.
internal sealed class TransferRateLimit
{
    private readonly double _capacity, _perSecond;
    private double _tokens, _last = -1;
    internal TransferRateLimit(double capacity, double perSecond)
    {
        _capacity = capacity; _perSecond = perSecond; _tokens = capacity;
    }
    internal bool Take(double amount, double now)
    {
        if (_last >= 0) _tokens = Math.Min(_capacity, _tokens + Math.Max(0, now - _last) * _perSecond);
        _last = Math.Max(_last, now);
        if (amount < 0 || amount > _tokens) return false;
        _tokens -= amount; return true;
    }
}
