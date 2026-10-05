namespace GameDevUsageBar.Core;

public static class TripoRegions
{
    public const string Global="global",China="china";
    public static bool IsValid(string value)=>value is Global or China;
    public static EndpointRule? Endpoint(ProviderDefinition definition,AccountConfig account)
    {
        if(definition.Id!="tripo")return definition.Endpoint;
        if(!IsValid(account.TripoRegion))throw new QueryException(FailureKind.Policy);
        if(account.TripoRegion==Global)return definition.Endpoint;
        // Only the pinned Tripo balance contract can acquire the regional host.
        // Custom definitions are never broadened by their provider ID alone.
        if(definition.Endpoint is not {Host:"openapi.tripo3d.ai",Port:443,Path:"/v3/account/balance"} rule)
            throw new QueryException(FailureKind.Policy);
        return rule with {Host=account.TripoRegion==China ? "openapi.tripo3d.com" : "openapi.tripo3d.ai"};
    }
    public static string Label(string value)=>value==China ? "China" : "Global";
}
