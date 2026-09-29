module TradingEdge.V2Build

// =============================================================================
// DATA v2 (user, 2026-09-29): the daily data as PARQUET, built from the raw
// downloaded files alone — no trading.db. Research (at home) and production
// (on the VPS) run this same command, so both get the same bytes from the same
// code; trading.db (v1) stays for the legacy systems.
//
//   raw files (data/daily_aggregates/*.csv.gz, data/splits.csv, data/dividends.csv, data/tickers.csv)
//     -> a SCRATCH DuckDB file (disk, not RAM: 50M rows must fit an 8 GB VPS)
//        daily_prices / splits / dividends  = Database.*Source (the v1 ingest's own SQL)
//        ticker_reference                   = the previous v2 list UPSERTED with tickers.csv (v1 never
//                                             deletes a ticker: the API drops delisted/reclassified names,
//                                             and the research universe must keep their history)
//        split_corrections, daily_adjusted  = sql/schema/materialized/02_, 03_ UNCHANGED
//     -> <out>.building/*.parquet + manifest.json -> swapped in as <out>/ (the old one kept as <out>.prev/)
//
// The whole set is rebuilt every run, as v1 rebuilds daily_adjusted: splits and dividends are
// corrected retroactively and n / cum_div are cumulative, so an append would leave old rows stale.
// Guards: duplicate keys refuse (v1's primary keys would have caught them); splits / dividends
// shrinking > 10 % against the previous build refuse (v1's retireAbsentIds guard).
// =============================================================================

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open DuckDB.NET.Data
open TradingEdge.Database

type V2Config =
    { RawDir: string                 // holds daily_aggregates/, splits.csv, dividends.csv, tickers.csv
      OutDir: string                 // e.g. data/v2
      MemoryLimit: string            // DuckDB memory_limit, e.g. "4GB"
      Threads: int option            // DuckDB threads (default: all cores); the RSS grows with it beyond memory_limit
      TempDir: string option         // DuckDB spill directory
      SeedTickers: string option }   // the FIRST build's ticker_reference (a parquet exported from v1)

/// the tables a v2 build writes, in the order they are written
let tables = [ "daily_prices"; "splits"; "dividends"; "ticker_reference"; "split_corrections"; "daily_adjusted" ]

let private sortKey = function
    | "daily_prices" | "daily_adjusted" -> "ticker, date"
    | "splits" | "dividends" -> "id"
    | "ticker_reference" -> "ticker, type"
    | _ -> "ticker, execution_date"

let private exec (c: DuckDBConnection) (sql: string) =
    use cmd = c.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteNonQuery() |> ignore

let private scalar<'T> (c: DuckDBConnection) (sql: string) : 'T =
    use cmd = c.CreateCommand()
    cmd.CommandText <- sql
    match cmd.ExecuteScalar() with
    | null -> Unchecked.defaultof<'T>
    | v -> Convert.ChangeType(v, typeof<'T>) :?> 'T

let private sha256 (path: string) =
    use f = File.OpenRead path
    Convert.ToHexString(SHA256.HashData f).ToLowerInvariant()

let private q (p: string) = Path.GetFullPath(p).Replace("'", "''")

/// the previous build's row count of a table (the shrink guard), from its manifest
let private previousCount (outDir: string) (table: string) : int64 option =
    let m = Path.Combine(outDir, "manifest.json")
    if not (File.Exists m) then None
    else
        use doc = JsonDocument.Parse(File.ReadAllText m)
        match doc.RootElement.TryGetProperty "rows" with
        | true, rows ->
            match rows.TryGetProperty table with
            | true, v -> Some (v.GetInt64())
            | _ -> None
        | _ -> None

let build (cfg: V2Config) : int =
    let sw = Diagnostics.Stopwatch.StartNew()
    let say (s: string) = printfn "[%6.1f s] %s" sw.Elapsed.TotalSeconds s
    let dailyGlob = Path.Combine(cfg.RawDir, "daily_aggregates", "*.csv.gz")
    let splitsCsv = Path.Combine(cfg.RawDir, "splits.csv")
    let dividendsCsv = Path.Combine(cfg.RawDir, "dividends.csv")
    let tickersCsv = Path.Combine(cfg.RawDir, "tickers.csv")
    for f in [ splitsCsv; dividendsCsv; tickersCsv ] do
        if not (File.Exists f) then failwithf "v2: missing raw input %s" f
    let dayFiles = Directory.GetFiles(Path.Combine(cfg.RawDir, "daily_aggregates"), "*.csv.gz") |> Array.sort
    if dayFiles.Length = 0 then failwithf "v2: no day aggregates under %s" (Path.Combine(cfg.RawDir, "daily_aggregates"))
    let previousTickers = Path.Combine(cfg.OutDir, "ticker_reference.parquet")
    let tickerBase =
        if File.Exists previousTickers then previousTickers
        else
            match cfg.SeedTickers with
            | Some s when File.Exists s -> s
            | Some s -> failwithf "v2: --seed-tickers %s does not exist" s
            | None -> failwithf "v2: the first build needs --seed-tickers (v1's ticker_reference exported to parquet): tickers.csv alone has lost delisted names"

    let staging = cfg.OutDir.TrimEnd('/') + ".building"
    if Directory.Exists staging then Directory.Delete(staging, true)
    Directory.CreateDirectory staging |> ignore
    let scratch = Path.Combine(staging, "scratch.duckdb")
    say (sprintf "v2 build: %d day files (%s .. %s) -> %s" dayFiles.Length (Path.GetFileName dayFiles.[0]) (Path.GetFileName dayFiles.[dayFiles.Length - 1]) staging)

    let rows = Collections.Generic.Dictionary<string, int64>()
    do
        use c = new DuckDBConnection($"Data Source={scratch}")
        c.Open()
        exec c (sprintf "SET memory_limit='%s'" cfg.MemoryLimit)
        exec c "SET preserve_insertion_order=false"
        cfg.Threads |> Option.iter (fun n -> exec c (sprintf "SET threads=%d" n))
        cfg.TempDir |> Option.iter (fun t -> Directory.CreateDirectory t |> ignore; exec c (sprintf "SET temp_directory='%s'" (q t)))

        // a (ticker, date) carried by two day files: the file that OWNS that session wins. Known case: the 2019-08-12
        // file stamps 29 thin ETFs 2019-08-13 (28 of them also in the 08-13 file); v1's upsert kept the stray rows.
        exec c ("CREATE TABLE daily_prices_raw AS " + dailyPricesSourceWith true (q dailyGlob))
        let stray = scalar<int64> c "SELECT count(*) FROM daily_prices_raw WHERE date <> file_date"
        let dupKeys = scalar<int64> c "SELECT count(*) FROM (SELECT ticker, date FROM daily_prices_raw GROUP BY ALL HAVING count(*) > 1)"
        say (sprintf "day files: %d rows dated outside their file's session, %d (ticker, date) in two files (the owning file wins)" stray dupKeys)
        if stray > 1000L then failwithf "v2: %d rows dated outside their day file — a systematic change in the files, not a stray: look first" stray
        // only the stray rows are looked at (a window over all 50M rows does not fit 4 GB): a stray row loses to a
        // row of the same (ticker, date) from the owning file; a stray row alone keeps its stamped date, as in v1
        exec c """DELETE FROM daily_prices_raw r USING (
                      SELECT o.ticker, o.date FROM daily_prices_raw o
                      SEMI JOIN (SELECT ticker, date FROM daily_prices_raw WHERE date <> file_date) s USING (ticker, date)
                      WHERE o.date = o.file_date) owned
                  WHERE r.ticker = owned.ticker AND r.date = owned.date AND r.date <> r.file_date"""
        exec c "ALTER TABLE daily_prices_raw DROP COLUMN file_date"
        exec c "ALTER TABLE daily_prices_raw RENAME TO daily_prices"
        exec c ("CREATE TABLE splits AS " + splitsSource (q splitsCsv))
        exec c ("CREATE TABLE dividends AS " + dividendsSource (q dividendsCsv))
        // ticker_reference: the previous list, upserted with the snapshot (a duplicate row in the snapshot collapses)
        exec c ($"""CREATE TABLE ticker_reference AS
                    WITH snap AS (SELECT ticker, any_value(name) AS name, type FROM ({tickersSource (q tickersCsv)}) GROUP BY ticker, type)
                    SELECT ticker, name, type FROM snap
                    UNION ALL
                    SELECT p.ticker, p.name, p.type FROM '{q tickerBase}' p ANTI JOIN snap s USING (ticker, type)""")
        say "raw files loaded"

        // the guards v1's primary keys and retireAbsentIds gave us
        let dup (t: string) (key: string) = scalar<int64> c (sprintf "SELECT count(*) FROM (SELECT %s FROM %s GROUP BY ALL HAVING count(*) > 1)" key t)
        for t, key in [ "daily_prices", "ticker, date"; "splits", "id"; "dividends", "id"; "ticker_reference", "ticker, type" ] do
            let n = dup t key
            if n > 0L then failwithf "v2: %s has %d duplicate keys (%s) — v1's primary key would have refused them" t n key
        for t in [ "splits"; "dividends" ] do
            let now = scalar<int64> c (sprintf "SELECT count(*) FROM %s" t)
            match previousCount cfg.OutDir t with
            | Some before when float now < 0.9 * float before ->
                failwithf "v2: %s shrank %d -> %d (> 10%%) — a narrow-range download? The file must be FULL RANGE (docs/massive_cli_gotchas.md)" t before now
            | _ -> ()

        // the derived tables: the v1 SQL, unchanged
        executeNamedResource c "02_split_corrections.sql"
        executeNamedResource c "03_daily_adjusted.sql"
        say "split_corrections + daily_adjusted materialized"

        for t in tables do
            let out = Path.Combine(staging, t + ".parquet")
            exec c (sprintf "COPY (SELECT * FROM %s ORDER BY %s) TO '%s' (FORMAT PARQUET, COMPRESSION zstd)" t (sortKey t) (q out))
            rows.[t] <- scalar<int64> c (sprintf "SELECT count(*) FROM '%s'" (q out))
            say (sprintf "%-18s %12d rows  %8.1f MB" t rows.[t] (float (FileInfo(out).Length) / 1e6))
    File.Delete scratch
    File.Delete (scratch + ".wal") |> ignore

    let lastDate =
        use c = new DuckDBConnection("Data Source=:memory:")
        c.Open()
        scalar<string> c (sprintf "SELECT strftime(max(date), '%%Y-%%m-%%d') FROM '%s'" (q (Path.Combine(staging, "daily_adjusted.parquet"))))
    let manifest =
        {| built_utc = DateTime.UtcNow.ToString "o"
           last_date = lastDate
           rows = rows
           inputs =
               {| day_files = dayFiles.Length
                  first_day_file = Path.GetFileName dayFiles.[0]
                  last_day_file = Path.GetFileName dayFiles.[dayFiles.Length - 1]
                  splits_sha256 = sha256 splitsCsv
                  dividends_sha256 = sha256 dividendsCsv
                  tickers_sha256 = sha256 tickersCsv
                  ticker_base = Path.GetFullPath tickerBase |}
           outputs = Collections.Generic.Dictionary<string, string>(tables |> List.map (fun t -> Collections.Generic.KeyValuePair(t, sha256 (Path.Combine(staging, t + ".parquet"))))) |}
    File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, JsonSerializerOptions(WriteIndented = true)))

    // swap: <out> -> <out>.prev (one generation kept: the next build's guards and change detection), staging -> <out>
    let prev = cfg.OutDir.TrimEnd('/') + ".prev"
    if Directory.Exists cfg.OutDir then
        if Directory.Exists prev then Directory.Delete(prev, true)
        Directory.Move(cfg.OutDir, prev)
    Directory.Move(staging, cfg.OutDir)
    say (sprintf "v2 built: last date %s -> %s (previous build in %s)" lastDate cfg.OutDir prev)
    0
