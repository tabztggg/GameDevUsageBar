using System.ComponentModel;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

namespace GameDevUsageBar.App.Presentation;

public sealed class NetworkUsageModel(NetworkSpeedSnapshot? initial=null) : INotifyPropertyChanged
{
    private readonly NetworkSpeedReader reader=new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public NetworkSpeedSnapshot Snapshot {get;private set;}=initial??new("warming_up",null,null,null,null,DateTimeOffset.UtcNow);
    public NetworkAdapterChoice[] Choices()=>reader.Choices();
    private static string Value(decimal? n)=>n is {} value?value.ToString("0.00",L.Culture):"—";
    public string Title=>L.T("Network speed");
    public string DownloadLabel=>L.T("Download");
    public string UploadLabel=>L.T("Upload");
    public string Download=>Value(Snapshot.DownloadMbPerSecond)+" MB/s";
    public string Upload=>Value(Snapshot.UploadMbPerSecond)+" MB/s";
    public string BarNumbers=>"↓ "+Value(Snapshot.DownloadMbPerSecond)+"  ↑ "+Value(Snapshot.UploadMbPerSecond);
    public string BarValue=>BarNumbers+" MB/s";
    public string Adapter=>Snapshot.AdapterName??L.T("No connected selected adapter");
    public string Notice=>L.T(Snapshot.Status switch {"live"=>"Live · Windows adapter counters","disabled"=>"Network display disabled","warming_up"=>"Sampling; speed will appear shortly",_=>"Adapter disconnected or unavailable"});
    public string Units=>L.T("MB/s = 1,000,000 bytes per second · sampled every second");
    public string Sampled=>L.F("Updated {0}",L.Date(Snapshot.SampledAt,true));
    public void Update(string? adapter,bool enabled)
    {Snapshot=reader.Sample(adapter,enabled);RefreshLanguage();}
    public void RefreshLanguage()=>PropertyChanged?.Invoke(this,new(null));
}
