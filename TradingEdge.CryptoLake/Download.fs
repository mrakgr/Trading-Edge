module TradingEdge.CryptoLake.Download

open System
open System.IO
open System.Threading
open Amazon.S3
open TradingEdge.CryptoLake

// =============================================================================
// Crypto Lake downloader
// =============================================================================
//
// We pass the upstream snappy parquet through *verbatim*. The original Python
// downloader spent 19 minutes on a single 458 MB file because it round-tripped
// through pyarrow.compute (ns->us conversion + zstd recompression, all of it
// I/O-bound at 4% CPU). The OBI consumer can read the raw schema directly
// (timestamp i64 ns -> us is one DuckDB SELECT cast away), so the rewrite
// pass adds no value.
//
// Output: {root}/{table}/{exchange}/{symbol}/{date}.parquet, byte-for-byte
// identical to the source object (snappy compression preserved).

type DownloadResult =
    | Downloaded of bytes: int64 * elapsed: TimeSpan
    | Skipped
    | Failed of error: string

let downloadOne
    (client: AmazonS3Client)
    (root: string)
    (table: string)
    (exchange: string)
    (symbol: string)
    (date: DateTime)
    (parallelism: int)
    (partSize: int64)
    (overwrite: bool)
    (ct: CancellationToken)
    : System.Threading.Tasks.Task<DownloadResult> =
    task {
        let dest = Schema.dataPath root table exchange symbol date
        if File.Exists dest && not overwrite then
            return Skipped
        else
            let key = S3.dataKey table exchange symbol date
            let sw = Diagnostics.Stopwatch.StartNew()
            try
                let! bytes =
                    if parallelism <= 1 then
                        S3.downloadObjectStreaming client key dest ct
                    else
                        S3.downloadObjectMultipart client key dest parallelism partSize ct
                sw.Stop()
                return Downloaded(bytes, sw.Elapsed)
            with ex ->
                sw.Stop()
                // Clean up any partial .tmp on failure.
                let tmp = dest + ".tmp"
                if File.Exists tmp then
                    try File.Delete tmp with _ -> ()
                return Failed ex.Message
    }
