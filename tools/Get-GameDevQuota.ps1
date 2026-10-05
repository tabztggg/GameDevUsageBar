#Requires -Version 7.0
param([ValidatePattern('^[a-z0-9-]{1,64}$')][string]$Provider,[switch]$AsJson,[switch]$Summary)
$ErrorActionPreference='Stop'
$address='http://127.0.0.1:17864/v1/usage'
if($Provider){$address+='/'+[Uri]::EscapeDataString($Provider)}
try {$response=Invoke-WebRequest -Uri $address -Method Get -NoProxy -TimeoutSec 5}
catch {throw 'GameDevUsageBar quota API is unavailable or the provider ID is unknown. Check that GameDevUsageBar is running and port 17864 is available. No refresh, login, or generation was attempted.'}
$result=$response.Content | ConvertFrom-Json -Depth 32
if($result.schema_version -ne 1 -or $result.app -ne 'GameDevUsageBar'){throw 'Unexpected quota API identity or schema.'}
if($AsJson){$response.Content;return}
if(!$Summary){$result;return}
$sources=if($Provider){@($result.provider)}else{@($result.providers)}
foreach($source in $sources){
 if(!$source.metrics.Count){[pscustomobject]@{Provider=$source.id;Metric='';Value=$null;Unit='';Usable=$false;Freshness=$source.freshness;RetrievedAt=$source.retrieved_at;ResetAt=$null;Error=$source.error};continue}
 foreach($metric in $source.metrics){[pscustomobject]@{Provider=$source.id;Metric=$metric.id;Value=$metric.value;Unit=$metric.unit;Usable=$metric.usable;Freshness=$source.freshness;RetrievedAt=$source.retrieved_at;ResetAt=$metric.resets_at;Error=$source.error}}
}
