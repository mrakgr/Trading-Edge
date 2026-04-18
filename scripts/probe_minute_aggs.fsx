#r "../TradingEdge.Massive/bin/Release/net10.0/TradingEdge.Massive.dll"
#r "../TradingEdge.Massive/bin/Release/net10.0/AWSSDK.S3.dll"
#r "../TradingEdge.Massive/bin/Release/net10.0/AWSSDK.Core.dll"

// Stage 0 probe — does Massive's flat-file bucket carry a `minute_aggs_v1`
// prefix parallel to the `day_aggs_v1` we already use?
//
// Lists the first page of objects under
//   s3://flatfiles/us_stocks_sip/minute_aggs_v1/2024/04/
// If the listing returns .csv.gz files, the bulk download plan proceeds as
// designed. If empty or AccessDenied, we pivot to per-ticker REST.

open Amazon.S3.Model
open TradingEdge
open TradingEdge.S3Download

let config = Config.loadConfigOrFail "api_key.json"
let client = createS3Client config.S3AccessKey config.S3SecretKey

let req = ListObjectsV2Request(
    BucketName = "flatfiles",
    Prefix = "us_stocks_sip/minute_aggs_v1/2024/04/",
    MaxKeys = 20)

printfn "Listing: s3://flatfiles/%s" req.Prefix
let resp = client.ListObjectsV2Async(req).GetAwaiter().GetResult()

if resp.S3Objects = null || resp.S3Objects.Count = 0 then
    printfn ""
    printfn "EMPTY LISTING — endpoint does not expose minute_aggs_v1 under this prefix."
    printfn "Falling back to per-ticker REST is likely necessary."
else
    printfn ""
    printfn "Found %d object(s):" resp.S3Objects.Count
    for o in resp.S3Objects do
        printfn "  %s  (%.2f MB)" o.Key (float (o.Size.GetValueOrDefault()) / 1024.0 / 1024.0)
    if resp.IsTruncated.GetValueOrDefault() then
        printfn "(response truncated — more keys available)"
