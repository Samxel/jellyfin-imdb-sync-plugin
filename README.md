# Jellyfin IMDb Sync

Syncs every Jellyfin user's watch history and watchlist with **their own IMDb account**.

## Features

- **Watch history → IMDb:** finished movies and episodes are marked as watched on IMDb right away, and a daily task catches up on the rest.
- **Watchlist ↔ playlist:** each user's IMDb watchlist is kept in sync with their own private Jellyfin playlist *Watchlist*, in both directions, including removals.
  - A series appears in the playlist as its first episode only, not every episode.
  - A safety stop holds back mass removals.
- **Per user:** each user connects their own IMDb account on a self-service page.
- **Activity log:** shows every title sent, added or removed, and every error.

Requires **Jellyfin 12.1**.

## Installation

1. Jellyfin Dashboard → **Plugins** → **Repositories** → add:
   ```
   https://raw.githubusercontent.com/Samxel/jellyfin-imdb-sync-plugin/master/manifest.json
   ```
2. Install **IMDb Sync** from the catalog and restart Jellyfin.

## Setup (per user)

1. Open `https://<your-jellyfin>/ImdbSync/Page`.
2. On [imdb.com](https://www.imdb.com), while logged in, press F12 → **Network**, reload the page, click the `www.imdb.com` request, and copy the value of the `cookie` request header.
3. Paste it on the page, tick the options you want, then **Save** → **Test cookie** → **Sync now**.

If the cookie expires, the page says so. Paste a new one to continue.

---

## Disclaimer

**For personal use only.**

- This plugin was made for my own server and was written **entirely by AI**, with no human code review.
- It uses IMDb's unofficial API with your IMDb session cookie. That cookie gives full access to your IMDb/Amazon account and is stored unencrypted on the Jellyfin server.
- The plugin is not affiliated with IMDb, Amazon or Jellyfin.

**Use at your own risk. Feature requests are currently not being accepted.**
