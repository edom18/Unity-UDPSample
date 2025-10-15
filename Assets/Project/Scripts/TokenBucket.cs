using System;
using UnityEngine;

public class TokenBucket
{
    private double _ratePerSec; // 1 秒あたりの補充量（=目標pps）
    private double _tokens;
    private readonly double _burst;
    private double _last;

    public TokenBucket(double ratePerSec, double burst)
    {
        _ratePerSec = Math.Max(0.0, ratePerSec);
        _burst = Math.Max(1.0, burst);
        _tokens = _burst;
        _last = Now();
    }

    public void SetRate(double r) => _ratePerSec = Math.Max(0.0, r);

    public bool Consume(double cost)
    {
        double now = Now();
        _tokens = Math.Min(_burst, _tokens + (now - _last) * _ratePerSec);
        _last = now;
        if (_tokens >= cost)
        {
            _tokens -= cost;
            return true;
        }

        return false;
    }

    private static double Now()
    {
#if UNITY_2021_2_OR_NEWER
        return Time.realtimeSinceStartupAsDouble;
#else
        return (double)Time.realtimeSinceStartup;
#endif
    }
}