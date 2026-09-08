using System.Globalization;
using System.Net.Http.Headers;

namespace NetLights.Core;

public static class RetryAfterParser
{
    public static long ResolvePauseUntil(
        int statusCode,
        string? retryAfterHeader,
        MonotonicClock clock,
        DateTimeOffset utcNow)
    {
        bool pauseStatus = statusCode is 403 or 429 or 503;
        if (string.IsNullOrWhiteSpace(retryAfterHeader))
        {
            return pauseStatus && statusCode is 403 or 429
                ? clock.Add(clock.Now, MonitorConstants.DefaultPause)
                : 0;
        }

        if (RetryConditionHeaderValue.TryParse(retryAfterHeader, out RetryConditionHeaderValue? header))
        {
            if (header.Delta is TimeSpan delta)
            {
                if (delta <= TimeSpan.Zero)
                {
                    return clock.Add(clock.Now, MonitorConstants.ExpiredRetryAfterPause);
                }

                return clock.Add(clock.Now, delta);
            }

            if (header.Date is DateTimeOffset when)
            {
                TimeSpan until = when - utcNow;
                if (until <= TimeSpan.Zero)
                {
                    return clock.Add(clock.Now, MonitorConstants.ExpiredRetryAfterPause);
                }

                return clock.Add(clock.Now, until);
            }
        }

        if (DateTimeOffset.TryParseExact(
                retryAfterHeader,
                "r",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            TimeSpan until = parsed - utcNow;
            if (until <= TimeSpan.Zero)
            {
                return clock.Add(clock.Now, MonitorConstants.ExpiredRetryAfterPause);
            }

            return clock.Add(clock.Now, until);
        }

        if (int.TryParse(retryAfterHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            && seconds >= 0)
        {
            TimeSpan pause = TimeSpan.FromSeconds(seconds);
            if (pause <= TimeSpan.Zero)
            {
                return clock.Add(clock.Now, MonitorConstants.ExpiredRetryAfterPause);
            }

            return clock.Add(clock.Now, pause);
        }

        return pauseStatus
            ? clock.Add(clock.Now, MonitorConstants.DefaultPause)
            : 0;
    }
}
