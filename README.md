# Jellyfin Book Enhancer Plugin

<p align="center">
  <img alt="status" src="https://img.shields.io/badge/status-alpha-red?labelColor=black" />
  <img alt="Jellyfin" src="https://img.shields.io/badge/Jellyfin-10.11%2B-00A4DC?logo=jellyfin&logoColor=white&labelColor=black" />
  <img alt=".NET" src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white&labelColor=black" />
  <img alt="License" src="https://img.shields.io/badge/license-GPL--3.0-blue?labelColor=black" />
</p>

> **This plugin is in active alpha development. Expect breaking changes between releases.**

Unified metadata enrichment for ebooks, audiobooks, and comics in Jellyfin. Extracts metadata directly from book files and enriches via online APIs — no Calibre, Audiobookshelf, or Komga dependencies required.

## How it works

Jellyfin scans your local book files normally. This plugin intercepts the scan and:

1. **Extracts** metadata directly from the file (title, authors, ISBN, series, etc.)
2. **Enriches** via online APIs in a cascade: Hardcover → Google Books → OpenLibrary
3. **Stores** the result as Jellyfin metadata — richer titles, authors, narrators, illustrators, series info, genres, covers, and more

## Features

### File Parsing
- **EPUB** — title, authors, ISBN (3 extraction strategies), publisher, date, language, tags, series, cover image
- **CBZ/CBR/CB7** — full ComicInfo.xml with 8 creative roles (writer, penciller, inker, colorist, letterer, cover artist, editor, translator), age rating, manga flag, page count
- **PDF** — title, author, subject, keywords, page count
- **Audio** (MP3, M4B, FLAC, OGG) — title, artist, album (series), genres, narrators, duration, cover art

### Online Enrichment
- **Hardcover** (primary) — best quality metadata via GraphQL API. Free API key required.
- **Google Books** (fallback) — good metadata coverage. Optional API key for higher rate limits.
- **OpenLibrary** (final fallback) — free, no API key. Covers public domain and older titles.

## Installation

1. Download the latest release from the [Releases page](../../releases)
2. Place the `.dll` in your Jellyfin `plugins` directory
3. Restart Jellyfin
4. Configure the plugin in Dashboard → Plugins → Books

## Configuration

- **Hardcover API Key** — get one free at https://hardcover.app/account/api
- **Google Books API Key** (optional) — get one at https://console.cloud.google.com/apis/credentials
- **Title Match Threshold** — confidence score (0.0–1.0) for fuzzy matching

## Build

```bash
dotnet build -c Release
```

## License

GPL-3.0
