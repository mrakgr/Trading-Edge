module TradingEdge.CryptoLake.S3

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Amazon
open Amazon.Runtime
open Amazon.Runtime.CredentialManagement
open Amazon.S3
open Amazon.S3.Model

// =============================================================================
// Crypto Lake S3 access
// =============================================================================
//
// Credentials live in the named AWS profile 'crypto-lake' (see
// ~/.aws/credentials). We load them via AWSSDK's CredentialProfileStoreChain
// — that's the same chain `boto3.Session(profile_name=...)` uses on the
// Python side, so behaviour is consistent across language stacks.
//
// Bucket: qnt.data, region: eu-west-1.
// Path layout:
//   market-data/cryptofeed/{table}/exchange={EXCHANGE}/symbol={SYMBOL}
//     /dt={YYYY-MM-DD}/1.snappy.parquet

let bucket = "qnt.data"
let region = RegionEndpoint.EUWest1

let dataKey (table: string) (exchange: string) (symbol: string) (date: DateTime) : string =
    sprintf "market-data/cryptofeed/%s/exchange=%s/symbol=%s/dt=%s/1.snappy.parquet"
        table exchange symbol (date.ToString("yyyy-MM-dd"))

let createClient (profileName: string) : AmazonS3Client =
    let chain = CredentialProfileStoreChain()
    match chain.TryGetAWSCredentials(profileName) with
    | true, creds ->
        let cfg = AmazonS3Config(RegionEndpoint = region)
        new AmazonS3Client(creds, cfg)
    | false, _ ->
        failwithf "AWS profile '%s' not found in ~/.aws/credentials" profileName

/// Streamed download to file. Use for small objects (<100 MB).
let downloadObjectStreaming
    (client: AmazonS3Client)
    (key: string)
    (destination: string)
    (ct: CancellationToken)
    : Task<int64> =
    task {
        let req = GetObjectRequest(BucketName = bucket, Key = key)
        use! resp = client.GetObjectAsync(req, ct)
        Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
        let tmp = destination + ".tmp"
        if File.Exists tmp then File.Delete tmp
        do!
            task {
                use out = File.Create tmp
                do! resp.ResponseStream.CopyToAsync(out, ct)
            }
        if File.Exists destination then File.Delete destination
        File.Move(tmp, destination)
        return resp.ContentLength
    }

/// Probe object size with HEAD (no body fetch).
let headObjectSize
    (client: AmazonS3Client)
    (key: string)
    (ct: CancellationToken)
    : Task<int64> =
    task {
        let req = GetObjectMetadataRequest(BucketName = bucket, Key = key)
        let! resp = client.GetObjectMetadataAsync(req, ct)
        return resp.ContentLength
    }

/// Concurrent multipart download. Splits the object into ranges, fetches them
/// in parallel, writes them to disjoint regions of the destination file, then
/// renames atomically. The performance win versus a single-threaded GET is
/// large for fat parquets (700 MB+), where the bottleneck is per-connection
/// throughput, not bandwidth.
let downloadObjectMultipart
    (client: AmazonS3Client)
    (key: string)
    (destination: string)
    (parallelism: int)
    (partSize: int64)
    (ct: CancellationToken)
    : Task<int64> =
    task {
        let! totalSize = headObjectSize client key ct
        Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
        let tmp = destination + ".tmp"
        if File.Exists tmp then File.Delete tmp

        // Pre-size the output file so each worker can write to its slice
        // without seeking past EOF.
        do
            use fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write,
                                    FileShare.Write, 4096, FileOptions.None)
            fs.SetLength totalSize

        let nParts =
            int (totalSize / partSize +
                 (if totalSize % partSize > 0L then 1L else 0L))
        let parts = [| for i in 0 .. nParts - 1 -> i |]
        use sem = new SemaphoreSlim(parallelism)

        let downloadPart (idx: int) : Task<unit> =
            task {
                do! sem.WaitAsync ct
                try
                    let startByte = int64 idx * partSize
                    let endByte = min (startByte + partSize - 1L) (totalSize - 1L)
                    let req =
                        GetObjectRequest(
                            BucketName = bucket,
                            Key = key,
                            ByteRange = ByteRange(startByte, endByte))
                    use! resp = client.GetObjectAsync(req, ct)
                    use fs =
                        new FileStream(tmp, FileMode.Open, FileAccess.Write,
                                       FileShare.Write, 1 <<< 16, FileOptions.Asynchronous)
                    fs.Position <- startByte
                    do! resp.ResponseStream.CopyToAsync(fs, ct)
                finally
                    sem.Release() |> ignore
            }

        do!
            parts
            |> Array.map downloadPart
            |> Task.WhenAll
            :> Task

        if File.Exists destination then File.Delete destination
        File.Move(tmp, destination)
        return totalSize
    }
