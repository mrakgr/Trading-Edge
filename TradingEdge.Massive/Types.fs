namespace TradingEdge

open System

/// Identity of a Polygon corporate-action record, used verbatim as the PRIMARY KEY.
///
/// Polygon returns `id` as an 'E' prefix plus 64 hex characters — a content hash,
/// e.g. "E003ef6d8431a65d388f7e1c34cae578798978c4e26161c9be986bd1f7af9fd14". It is
/// stable across calls, so re-ingesting the same vendor record lands on the same
/// key and UPSERTs rather than duplicating.
///
/// ⚠ This key exists to DEDUPLICATE ON RE-INGEST, not to join on. An
/// auto-increment surrogate cannot do that job — it assigns a fresh value every
/// download, so ON CONFLICT would never fire and each backfill would append
/// another full copy of the table. Keeping the vendor string also means a row can
/// be traced straight back to the reference API, which is how the reverse/forward
/// split-pair bug was diagnosed in the first place.
module VendorId =

    /// Validate Polygon's record id. Raises on a missing one: a corporate action
    /// with no stable identity cannot be keyed, and inventing a surrogate is what
    /// let reverse/forward split pairs collide to begin with. Fail loudly.
    let require (context: string) (id: string) : string =
        if String.IsNullOrWhiteSpace id then
            failwithf "Polygon returned no `id` for %s — cannot key this record" context
        // Commas would corrupt the CSV round-trip; Polygon ids are hex, so this
        // should never fire, but it is cheap insurance on a primary key.
        if id.Contains "," then
            failwithf "Polygon `id` %s for %s contains a comma" id context
        id

/// Configuration for Massive API access
type MassiveConfig = {
    ApiKey: string
    S3AccessKey: string
    S3SecretKey: string
}

/// Stock split information from Massive API.
/// ⭐ `Id` is Polygon's own stable record id and is the PRIMARY KEY. A ticker can
/// have MORE THAN ONE split on a single execution_date — a reverse/forward PAIR
/// (the odd-lot squeeze-out used to force out small holders before deregistering)
/// nets to no change in share count at all. Keying on (ticker, execution_date)
/// silently kept one leg and left a naked ratio: TTSH 2025-12-16 became a bare
/// 1:3000, PMD a 1:5000. See docs/price_adjustment.md.
type Split = {
    Id: string
    Ticker: string
    ExecutionDate: DateTime
    SplitFrom: float
    SplitTo: float
    SplitRatio: float
}

/// Dividend information from Polygon API.
/// ⭐ `Id` is Polygon's stable record id and is the PRIMARY KEY — same reason as
/// Split. One ex-date routinely carries SEVERAL payments (a regular plus a
/// special; ADRs like ABEV pay interest-on-capital alongside the dividend), and
/// keying on (ticker, ex_dividend_date) dropped 1.5% of records outright.
type Dividend = {
    Id: string
    Ticker: string
    ExDividendDate: DateTime
    CashAmount: float
    DeclarationDate: DateTime option
    PayDate: DateTime option
    Frequency: int
    DividendType: string
}

/// Daily OHLCV price data
type DailyPrice = {
    Ticker: string
    Date: DateTime
    Open: float
    High: float
    Low: float
    Close: float
    Volume: int64
    Transactions: int64
}

/// Result of a download operation for a single date
type DownloadResult =
    | Downloaded of date: DateTime
    | Skipped of date: DateTime
    | Failed of date: DateTime * error: string
