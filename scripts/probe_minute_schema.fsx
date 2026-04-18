#r "../TradingEdge.Massive/bin/Release/net10.0/TradingEdge.Massive.dll"
#r "../TradingEdge.Massive/bin/Release/net10.0/AWSSDK.S3.dll"
#r "../TradingEdge.Massive/bin/Release/net10.0/AWSSDK.Core.dll"

// Fetch one day of the minute_aggs_v1 flat file and print the CSV header plus
// the first few data rows, so we can confirm which columns are present
// (especially whether vwap is included).

open System
open System.IO
open System.IO.Compression
open Amazon.S3.Model
open TradingEdge
open TradingEdge.S3Download

let config = Config.loadConfigOrFail "api_key.json"
let client = createS3Client config.S3AccessKey config.S3SecretKey

let key = "us_stocks_sip/minute_aggs_v1/2024/04/2024-04-01.csv.gz"
printfn "Fetching s3://flatfiles/%s ..." key

let req = GetObjectRequest(BucketName = "flatfiles", Key = key)
use resp = client.GetObjectAsync(req).GetAwaiter().GetResult()
use gz = new GZipStream(resp.ResponseStream, CompressionMode.Decompress)
use reader = new StreamReader(gz)

let header = reader.ReadLine()
printfn ""
printfn "Header: %s" header
printfn ""
printfn "First 5 rows:"
for _ in 1 .. 5 do
    let line = reader.ReadLine()
    if line <> null then printfn "  %s" line
