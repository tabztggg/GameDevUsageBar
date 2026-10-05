using System.Diagnostics;
using System.Net.NetworkInformation;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.Infrastructure;

public sealed class NetworkSpeedReader
{
    private readonly NetworkRateCalculator calculator=new();
    private static NetworkInterface[] Interfaces()=>NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Wireless80211).ToArray();
    private static bool AutoCandidate(NetworkInterface n)=>!new[]{"virtual","vpn","tap-","tun","hyper-v","vmware","virtualbox","wintun"}.Any(s=>n.Description.Contains(s,StringComparison.OrdinalIgnoreCase));
    public NetworkAdapterChoice[] Choices()
    {
        try{return Interfaces().Select(n=>new NetworkAdapterChoice(n.Id,n.Name,n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211?"Wi-Fi":"Ethernet",n.OperationalStatus==OperationalStatus.Up,AutoCandidate(n))).ToArray();}
        catch(NetworkInformationException){return [];}
    }
    public NetworkSpeedSnapshot Sample(string? id,bool enabled)
    {
        NetworkCounters? counters=null;
        if(enabled)try{
            var interfaces=Interfaces();
            var selected=id is {Length:>0}?interfaces.FirstOrDefault(n=>n.Id==id):interfaces.Where(n=>n.OperationalStatus==OperationalStatus.Up&&AutoCandidate(n)).OrderBy(n=>n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211?1:0).ThenByDescending(n=>n.GetIPProperties().GatewayAddresses.Count).ThenBy(n=>n.Id).FirstOrDefault();
            if(selected?.OperationalStatus==OperationalStatus.Up){var stats=selected.GetIPStatistics();counters=new(selected.Id,selected.Name,stats.BytesReceived,stats.BytesSent);}
        }catch(NetworkInformationException){}catch(InvalidOperationException){}
        var snapshot=calculator.Update(counters,Stopwatch.GetTimestamp(),Stopwatch.Frequency,DateTimeOffset.UtcNow);
        return enabled?snapshot:snapshot with {Status="disabled"};
    }
}
