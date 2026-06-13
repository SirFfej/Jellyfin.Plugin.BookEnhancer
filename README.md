# Jellyfin Book Enhancer Plugin

<p align="center">
  <img alt="status" src="https://img.shields.io/badge/status-alpha-red?labelColor=black" />
  <img alt="Jellyfin" src="https://img.shields.io/badge/Jellyfin-10.11%2B-00A4DC?logo=jellyfin&logoColor=white&labelColor=black" />
  <img alt=".NET" src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white&labelColor=black" />
  <img alt="License" src="https://img.shields.io/badge/license-GPL--3.0-blue?labelColor=black" />
</p>

## License

**GPL-3.0** — free to use, modify, and distribute. See [LICENSE](LICENSE) for full terms.

> **This plugin is in active development. Expect breaking changes between releases.**

Unified metadata enrichment for ebooks, audiobooks, and comics in Jellyfin. Parses metadata directly from book files and enriches via online API cascade — no Calibre, Audiobookshelf, or Komga dependencies required.

## Installation

### Via Jellyfin Plugin Repository

1. In Jellyfin Dashboard → Plugins → Repositories, add:
   - **URL**: `https://raw.githubusercontent.com/SirFfej/Jellyfin.Plugin.BookEnhancer/master/manifest.json`
   - **Name**: `BookEnhancers`
2. Go to Catalog → find **BookEnhancers** → Install
3. Restart Jellyfin

### Direct Download

1. Download the latest `Jellyfin.Plugin.BookEnhancer_{version}.zip` from the [Releases page](../../releases)
2. Extract the ZIP contents into a new folder `BookEnhancers_0.x.x.x` inside your Jellyfin `plugins` directory
   - **Linux**: `/var/lib/jellyfin/plugins/`
   - **Windows**: `%LOCALAPPDATA%\jellyfin\plugins\`
   - **Docker**: `/config/plugins/`
3. Restart Jellyfin

### Verify

After restart, the plugin appears in Dashboard → Plugins. If it shows **NotSupported**, you likely have a Jellyfin version mismatch — check that your server is 10.11+.

## Features

### File Parsing
- **EPUB** — title, authors, ISBN (3 extraction strategies), publisher, date, language, tags, series, cover image
- **CBZ/CBR/CB7** — full ComicInfo.xml with 8 creative roles (writer, penciller, inker, colorist, letterer, cover artist, editor, translator), age rating, manga flag, page count. **Filename fallback** — when ComicInfo.xml is missing or unreadable (e.g. RAR5 CBRs), series name, issue number, and year are parsed from common filename patterns like `Series Name 014 (2013)` to enable enrichment via the comic cascade.
- **PDF** — title, author, subject, keywords, page count
- **Audio** (MP3, M4B, FLAC, OGG, OPUS, M4A, WMA, AIFF) — title, artist, album/series, genres, narrators, duration, cover art

### Online Enrichment Cascade
1. **Hardcover** (tier 1) — highest quality via GraphQL API. Free API key required.
2. **Google Books** (tier 2) — good coverage. Optional API key for higher rate limits.
3. **OpenLibrary** (tier 3) — free, no key. Covers public domain and older titles.
4. **Comic Vine** (tier 4) — comic-specific enrichment via search by series + issue number. Free API key required.
5. **Metron** (tier 5) — community-run comic database, searched by series name + issue number. Free API token required.
6. **VerseDB** (tier 6) — modern comic database with general search. Free account required for API token.

Unified enrichment can be toggled on/off per source directory. When off, only raw file metadata is used.

Enriched metadata and dashboard edits are written back to file tags (ComicInfo.xml, OPF, ID3) during library scans when `EnableMetadata Writing` is enabled, creating self-documenting files that preserve edits across library re-scans.

### Jellyfin DB Metadata Fallback (v0.8.0.0)
- **Dashboard edits preserved** — when file tags are incomplete or null, metadata is filled from Jellyfin's `library.db`, allowing user edits in the Edit Metadata dialog to survive library scans
- **Write-back pipeline** — DB edits are picked up during the next library scan and written to file tags (ComicInfo.xml, OPF, ID3) when `EnableMetadata Writing` is on
- **Null-only fill** — DB values never overwrite existing file tags; only missing/null fields are populated from the DB, preventing circular amplification of the plugin's own writes
- **Covers all standard fields** — Title, Overview, Publisher, SeriesName, IndexNumber, ProductionYear, PremiereDate, ProviderIds (ISBN/ASIN), and Genres

### Network Diagnostics
- **Test Enrichment Connectivity** button on the Metadata config tab — pings all 6 enrichment APIs (Hardcover, Google Books, OpenLibrary, Comic Vine, Metron, VerseDB) and reports reachability, status codes, and error details per service
- **Independent of API key tests** — isolates network-level issues (port blocking, proxy, firewall, DNS) from credential problems

### Enrichment Cascade (v0.7.0.2)
- **Comic filename fallback** — when ComicInfo.xml is missing or unreadable, `SeriesName`, `SeriesNumber`, and `PublishYear` are parsed from common filename patterns (e.g. `All New X-Men 014 (2013)`). Files without ISBN but with filename-derived series/issue can now reach the comic enrichment cascade instead of being skipped.
- **Comic ISBN requirement removed** — comic files (.cbz/.cbr/.cb7) no longer require an ISBN to proceed to the enrichment cascade; the comic tier (Comic Vine → Metron → VerseDB) can run on filename-parsed series + issue number alone.
- **RAR5 fallback** — CBR files using the RAR5 format (not supported by SharpCompress for extraction) now produce fallback metadata from the filename instead of returning null, enabling enrichment where previously "Could not extract metadata" was logged.
- **Metron API** — community-run Comic Vine alternative, searched by series name + issue number; returns writer, penciller, inker, colorist, letterer, cover artist, editor, translator credits; character tags; publisher; cover date; description. Rate limited: 20 req/min, 5,000/day.
- **VerseDB API** — modern comic database with general search fallback; returns creative team, characters, publisher, cover art. API token required.
- **Comic Vine API** — original comic enrichment tier; searched first in the comic cascade by series + issue number
- **Comic cascade ordering** — Comic Vine → Metron → VerseDB; each runs only if the previous returned no results
- **Identify window comic support** — files with `SeriesName`/`SeriesNumber` in ComicInfo.xml trigger the full comic cascade even without ISBN
- **Rate limiting** — 250ms delay between API calls to prevent hitting rate limits on all services
- **Google Books API key guard** — unauthenticated requests no longer sent when no key is configured
- **Accurate match reporting** — cascade returns whether any API actually responded with data, independent of whether local metadata was already complete (fixes "No enrichment found" for files with rich embedded metadata)
- **Ingestion folder protection** — cleanup no longer scans or removes directories from source/ingestion paths, only from library paths

### Scene Tag Cleaning
- **Automatic scene tag stripping** — parenthetical groups (e.g. `(Digital)`, `(Kileko-Empire)`, `(2026)`), bracketed tags, and uppercase release group tags are cleaned from fallback filenames when ComicInfo.xml has no `<Title>` element
- Applied to ingestion, library cleanup, organization, and ComicInfo parser fallback paths

### Ingestion
- **Managed source directories** — configure source folders → organize into library paths
- **Per-source organize templates** — customize folder structure with `{Author}`, `{Series}`, `{Title}`, `{Publisher}` tokens
  - Books/audiobooks default: `{Author}/{Series}/{Title}`
  - Comics default: `{Publisher}/{Series}`
- **Copy mode** — copy files instead of moving (keeps originals in source)
- **Format priority** — when multiple formats of the same book exist, selects the preferred format (e.g., EPUB > MOBI > PDF)
- **File extension filter** — limit which file types are ingested

### Grouping
- **ISBN-based grouping** — same book in multiple formats grouped under a single Jellyfin item
- **Library-direct scanning** — grouping scans actual library directories on each run, not a stale SQLite cache; works independently of whether ingestion has ever run
- **Grouping Preview** — dry-run view of how files will be grouped before processing
- **Repair Format Paths** — cross-references `book_formats` against Jellyfin library items, populates `JellyfinItemId`, reports stale paths

### Library Cleanup
- **Reorganize files** — moves files to match the current organize template if the source directory structure has changed
- **Metadata enrichment pass** — files with missing template fields (author, publisher, series) are queued for online enrichment before reorganization
- **Enrichment safety guard** — if enrichment cannot resolve required template fields, files are left in place and logged to an enrichment-issues file (prevents moving to `Unknown/` paths)
- **Library-wide empty directory sweep** — after reorganization, removes all empty subdirectories across every managed library (deepest-first enumeration)
- **Deduplication** — when target file already exists with identical content, the stale source is removed silently

### Full Maintenance
- **Combined pipeline** — runs library cleanup → scans library directories for groups → processes groups in sequence
- **No longer includes ingestion scan** — ingestion is a separate concern; Full Maintenance handles library maintenance and grouping only

### Metadata Enrichment Report
- **Scan all library files** — iterates all managed directories, extracts metadata, attempts online enrichment cascade
- **Reports unenrichable items** — produces a summary log listing files with no ISBN, no online match, or extraction errors
- **Per-file progress** — main task log shows each file's enrichment status

### Config Page (Dashboard → Plugins → BookEnhancers)
- **Main** — managed directories table with inline status, library selection, organize templates, create directory buttons, per-row source path validation
- **Ingestion** — format priority drag-reorder, file extension filters, copy/move toggle, test API key buttons
- **Grouping** — Preview, Process, and Repair Format Paths buttons with results display
- **Metadata** — Test Enrichment Connectivity button with per-service reachability results; Comic Vine, Metron, and VerseDB toggles and API key/token inputs; **Metadata Guide button** opens a modal explaining the write-back pipeline, template tokens, and field mappings
- **Validation** — source paths show red **Not found** or green **OK** status inline

### Logging
- **Per-task log files** — each scheduled task writes to its own log file in the Jellyfin Logs directory (visible in Dashboard → Logs)
- **Ingestion scan** — daily log file appended across same-day runs (`IngestionScan-{yyyyMMdd}.log`)
- **Library Cleanup** — log files prefixed with `log_` so they are retained by the server's log retention policy
- **Enrichment issues** — files with metadata gaps that online enrichment could not resolve are logged to `log_LibraryCleanup-{yyyyMMdd}-enrichment-issues.log`

## Configuration

1. Open Dashboard → Plugins → BookEnhancers
2. Go to the **Main** tab
3. Add a managed directory:
   - **Source Path**: where your book files live (e.g., `/media/books/import`)
   - **Target Library**: pick the Jellyfin library to organize into (auto-fills Library Path)
   - **Organize Template**: customize folder structure (optional)
   - Click **Create** to create the target directory if it doesn't exist
 4. Go to the **Ingestion** tab to configure format priority and enrichment settings:
    - **Hardcover API Key** (required for enrichment) — get one free at https://hardcover.app/account/api
    - **Google Books API Key** (optional) — get one at https://console.cloud.google.com/apis/credentials
    - **Comic Vine API Key** (optional) — enable on the **Metadata** tab; get a key at https://comicvine.gamespot.com/api (signup may require Google/Twitter login)
    - **Metron API Token** (optional) — enable on the **Metadata** tab; sign up at https://metron.cloud and get a token from your profile settings
    - **VerseDB API Token** (optional) — enable on the **Metadata** tab; sign up at https://versedb.com and generate a token in API settings
5. Run the scheduled task **Ingestion Scan** from Dashboard → Scheduled Tasks

## Scheduled Tasks

| Task | Description |
|------|-------------|
| **Ingestion Scan** | Scans source directories, extracts metadata, organizes files into library paths |
| **Grouping Process** | Scans library directories and groups book formats by ISBN |
| **Full Maintenance** | Runs library cleanup → library scan → grouping (no ingestion) |
| **Library Cleanup** | Reorganizes files to match current templates, enriches missing metadata, removes stale duplicates and empty directories |
| **Metadata Enrichment** | Scans all library files, attempts online enrichment, reports items that could not be enriched |

## Build from Source

```bash
dotnet build Jellyfin-Plugin-BookEnhancer/Jellyfin-Plugin-BookEnhancer.csproj -c Release --no-incremental
```

Output is in `Jellyfin-Plugin-BookEnhancer/bin/Release/net9.0/`. Bundle `*.dll` + `meta.json` into a ZIP for deployment.

## Dependencies

Third-party NuGet packages are bundled in the release ZIP (included automatically by the build system):
- `UglyToad.PdfPig` — PDF parsing
- `SharpCompress` — CBR/CB7 extraction
- `TagLibSharp` — audio metadata extraction
- `Microsoft.Data.Sqlite` — book formats database
- `Newtonsoft.Json` — internal serialization

Jellyfin server assemblies (`Microsoft.Extensions.*`, `Jellyfin.Model`, `Jellyfin.Controller`) are NOT bundled — they resolve from the server at runtime.