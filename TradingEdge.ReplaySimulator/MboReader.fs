module TradingEdge.ReplaySimulator.MboReader

// Friendly wrapper over Dbn.fs: open a venue file, assert it's MBO, and expose
// a typed stream of trade events. Higher layers (k-way merge, replay engine,
// bar accumulator) sit on top.

open System
open System.IO
open TradingEdge.ReplaySimulator.Dbn

type VenueStream = {
    /// File path that was opened. Useful for error messages.
    Path: string
    /// Venue code derived from filename (e.g. "XNAS", "ARCX").
    Venue: string
    /// Full parsed metadata block from the file.
    Metadata: DbnMetadata
    /// MBO record stream. Single-pass. Disposing the wrapping IDisposable closes
    /// the underlying file + zstd stream.
    Records: seq<MboMsg>
    /// Disposable resource — call when done with the stream.
    Disposable: IDisposable
}

/// Open a venue file, assert it's an MBO file, and return the typed stream wrapper.
/// Raises InvalidDataException if the schema is not MBO.
let openVenue (path: string) : VenueStream =
    let metadata, stream = openDbnFile path
    if metadata.Schema <> SCHEMA_MBO then
        stream.Dispose()
        raise (InvalidDataException(
            sprintf "File %s has schema=%d, expected MBO (0). Use the correct downloader output."
                path metadata.Schema))
    let venue = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))  // strip both .dbn and .zst
    {
        Path = path
        Venue = venue
        Metadata = metadata
        Records = readMboRecords stream
        Disposable = stream
    }
