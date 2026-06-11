# Jellyfin Book Enhancer Plugin

<p align="center">
  <img alt="status" src="https://img.shields.io/badge/status-alpha-red?labelColor=black" />
  <img alt="Jellyfin" src="https://img.shields.io/badge/Jellyfin-10.11%2B-00A4DC?logo=jellyfin&logoColor=white&labelColor=black" />
  <img alt=".NET" src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white&labelColor=black" />
  <img alt="License" src="https://img.shields.io/badge/license-GPL--3.0-blue?labelColor=black" />
</p>

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
- **CBZ/CBR/CB7** — full ComicInfo.xml with 8 creative roles (writer, penciller, inker, colorist, letterer, cover artist, editor, translator), age rating, manga flag, page count
- **PDF** — title, author, subject, keywords, page count
- **Audio** (MP3, M4B, FLAC, OGG, OPUS, M4A, WMA, AIFF) — title, artist, album/series, genres, narrators, duration, cover art

### Online Enrichment Cascade
1. **Hardcover** (tier 1) — highest quality via GraphQL API. Free API key required.
2. **Google Books** (tier 2) — good coverage. Optional API key for higher rate limits.
3. **OpenLibrary** (tier 3) — free, no key. Covers public domain and older titles.

Unified enrichment can be toggled on/off per source directory. When off, only raw file metadata is used.

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
- **Grouping Preview** — dry-run view of how files will be grouped before processing
- **Repair Format Paths** — cross-references `book_formats` against Jellyfin library items, populates `JellyfinItemId`, reports stale paths
- **Post-processing** — after ingestion, groups formats and links them to Jellyfin library items

### Library Cleanup
- **Reorganize files** — moves files to match the current organize template if the source directory structure has changed
- **Metadata enrichment pass** — files with missing template fields (author, publisher, series) are queued for online enrichment before reorganization
- **Empty directory removal** — automatically cleans up empty directories left after moves
- **Deduplication** — when target file already exists with identical content, the stale source is removed silently

### Config Page (Dashboard → Plugins → BookEnhancers)
- **Main** — managed directories table with inline status, library selection, organize templates, create directory buttons, per-row source path validation
- **Ingestion** — format priority drag-reorder, file extension filters, copy/move toggle, test API key buttons
- **Grouping** — Preview, Process, and Repair Format Paths buttons with results display
- **Validation** — source paths show red **Not found** or green **OK** status inline

### Logging
- **Per-task log files** — each scheduled task writes to its own log file in the Jellyfin Logs directory (visible in Dashboard → Logs)
- **Ingestion scan** — daily log file appended across same-day runs (`IngestionScan-{yyyyMMdd}.log`)
- **Library Cleanup** — log files prefixed with `log_` so they are retained by the server's log retention policy

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
5. Run the scheduled task **Ingestion Scan** from Dashboard → Scheduled Tasks

### Scheduled Tasks
- **Ingestion Scan** — scans source directories, extracts metadata, organizes files into library paths
- **Grouping Process** — groups book formats by ISBN, links to Jellyfin library items
- **Full Maintenance** — runs both ingestion and grouping in sequence
- **Library Cleanup** — reorganizes files to match current templates, enriches missing metadata, removes stale duplicates and empty directories

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

## License

GPL-3.0
