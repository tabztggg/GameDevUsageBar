namespace GameDevUsageBar.Core;

public sealed record NetworkAdapterChoice(string Id,string Name,string Kind,bool Connected,bool AutomaticCandidate);
public sealed record NetworkCounters(string AdapterId,string AdapterName,long ReceivedBytes,long SentBytes);
public sealed record NetworkSpeedSnapshot(string Status,string? AdapterId,string? AdapterName,decimal? DownloadMbPerSecond,decimal? UploadMbPerSecond,DateTimeOffset SampledAt,string Unit="MB/s");
public sealed class NetworkRateCalculator
{
    private NetworkCounters? previous;
    private long timestamp;
    public NetworkSpeedSnapshot Update(NetworkCounters? counters,long tick,long frequency,DateTimeOffset now)
    {
        var old=previous;var elapsed=(tick-timestamp)/(double)frequency;previous=counters;timestamp=tick;
        if(counters is null)return new("unavailable",null,null,null,null,now);
        if(old is null||old.AdapterId!=counters.AdapterId||elapsed<=0||elapsed>10||counters.ReceivedBytes<old.ReceivedBytes||counters.SentBytes<old.SentBytes)
            return new("warming_up",counters.AdapterId,counters.AdapterName,null,null,now);
        // Decimal MB, not megabits: bytes / elapsed seconds / 1,000,000.
        return new("live",counters.AdapterId,counters.AdapterName,(counters.ReceivedBytes-old.ReceivedBytes)/(decimal)elapsed/1_000_000m,(counters.SentBytes-old.SentBytes)/(decimal)elapsed/1_000_000m,now);
    }
}
