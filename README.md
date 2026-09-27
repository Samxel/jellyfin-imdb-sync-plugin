# Jellyfin IMDb Sync

> [!WARNING]
> **Personal project, fully AI-generated. Use with caution.**
>
> - I built this only for my own Jellyfin server. It is not meant or maintained for anyone else.
> - The entire code, including this README, was written by an AI (Claude Code). It has had no human code review, and the only testing was on my own server.
> - It uses IMDb's **unofficial, undocumented** API with your personal IMDb session cookie. That may be against IMDb's terms of use, and it can stop working at any time.
> - The IMDb cookie gives **full access to your IMDb/Amazon account**. It is stored in plain text on the Jellyfin server.
> - Not affiliated with IMDb, Amazon or Jellyfin. No warranty, no support. Use it at your own risk.

A Jellyfin plugin that marks everything a user watches in Jellyfin as **watched on their own IMDb account**.

- **Right away:** when a movie/episode finishes (or is marked played), it is sent to IMDb immediately.
- **Daily catch-up:** the scheduled task *Sync watch history to IMDb* (04:00 by default) sends every played item that is not on IMDb yet, e.g. the history from before the plugin was installed.
- **Per user:** every Jellyfin user connects their own IMDb account by pasting their IMDb cookie on a self-service page. Admins never have to handle other people's cookies.

- **Watchlist ↔ playlist (optional):** each user's IMDb watchlist is kept in sync with their own private Jellyfin playlist *Watchlist*, in both directions, including removals.

Requires Jellyfin **12.1**.

## Install

Dashboard → Plugins → **Repositories** → add

```
https://raw.githubusercontent.com/Samxel/jellyfin-imdb-sync-plugin/master/manifest.json
```

then install **IMDb Sync** from the catalog and restart Jellyfin.

New versions are released by pushing a tag (`git tag v1.0.1 && git push origin v1.0.1`) or by running the *Release Plugin* workflow manually with a version; the workflow builds the zip, creates the GitHub release and adds it to `manifest.json`. Bump `version`/`changelog` in `build.yaml` alongside.

## Setup for users

1. Open `https://<your-jellyfin>/ImdbSync/Page` (the page uses your existing Jellyfin web login, or asks you to sign in).
2. Sign in on [imdb.com](https://www.imdb.com), open the browser developer tools (F12) → **Network**, reload, click the `www.imdb.com` request and copy the value of the `cookie` request header.
   Paste the whole cookie; it is sent to IMDb the same way your browser does. (`at-main` starts with `Atza|` or `Atna|`.)
3. Paste it, tick **Sync my watch history to IMDb**, **Save**, then **Test cookie**.
4. Press **Sync now** to push your existing history once. Progress is shown live; every title sent (or refused, and why) appears in the **activity log** under *Advanced*.

When IMDb stops accepting the cookie (e.g. you signed out of IMDb), the page shows *cookie expired* and syncing pauses until you paste a new one.

## Setup for admins

Dashboard → Plugins → **IMDb Sync**:

- link to the user page (you can add it to the web client's side menu through `menuLinks` in `config.json`),
- toggle the immediate sync and the delay between IMDb requests,
- overview of which users are set up, how many titles were synced, and their last error.

## How it works

- Only items with an IMDb id (`tt…`) in their metadata are synced. Episodes need their own episode IMDb id.
- The IMDb GraphQL API (`addWatchedTitle`) is called with the user's IMDb cookie, the same way the IMDb website does.
- Per-user data is stored in `<config>/plugins/configurations/ImdbSync/users/<userId>.json`. The cookie is never returned by any API endpoint (only a masked version) and is not part of the plugin XML configuration.
- Titles already sent are remembered and not sent again. Titles IMDb refuses are not retried automatically; **Resend everything** on the user page resets both lists.

### Watchlist ↔ playlist

- Every user who enables it gets their **own private** playlist *Watchlist* (owner = that user, not public). If they already have a playlist with that name, it is reused.
- **Two-way sync.** The plugin remembers both lists as they were after the last sync. Anything added or removed on one side since then is applied to the other side. If a title was removed on one side and added on the other, the addition wins.
- **First sync** (and after *Re-merge*, a new cookie, or re-enabling): both lists are merged and nothing is removed.
- **Movies** appear as themselves. **Series** appear as their **first episode** (S01E01) instead of every episode. Any episode of a series in the playlist counts as "this series is on the watchlist"; removing the series on IMDb removes all of its episodes from the playlist.
- Titles that are not in the Jellyfin library stay on IMDb only. If they are added to the library later, they show up in the playlist.
- **When:** changes to the playlist are synced about 10 seconds later. Changes on IMDb are picked up by the task *Sync IMDb watchlist with playlist*, every 15 minutes by default. The user page also has a *Sync watchlist now* button.
- **Safety stop:** if a sync would remove more than 10 titles and more than half of a list at once, those removals are held back and reported, for example when a list suddenly looks empty.

### Security

- All `/ImdbSync/Me*` endpoints need a valid Jellyfin user session and only ever touch the calling user's data. `/ImdbSync/Users` is admin-only. Only the static page `/ImdbSync/Page` is anonymous, and it contains no data.
- Jellyfin authenticates with a header token, not a cookie, so cross-site request forgery does not apply.
- The page is served with a strict Content-Security-Policy: it can only talk to the Jellyfin origin, cannot be framed by other sites, and sends no referrer.
- Cookies are never returned by the API; only a masked `Atza|…abcd` is shown. Cookies are also never written to the Jellyfin log.
- Requests to IMDb use a dedicated HTTP handler with no cookie jar, so no IMDb cookies are shared between users, and with redirects disabled, so the cookie is never forwarded to another host.
- Settings files are readable by the Jellyfin user only (`0600`). They are deleted when the Jellyfin user is deleted.
- Pasted cookies are length-limited and rejected if they contain control characters, which prevents header injection.
- **Not protected:** the cookie is stored **unencrypted** on disk. Anyone with access to the Jellyfin config directory (server admins, backups) can read it. The cookie also passes through your reverse proxy, e.g. Cloudflare, when it is saved.

### API

All endpoints need a Jellyfin user session (not an API key) and act on the calling user.

| Method | Path | |
| --- | --- | --- |
| GET | `/ImdbSync/Page` | self-service page (anonymous, authenticates in the browser) |
| GET / POST / DELETE | `/ImdbSync/Me` | read / update / delete own settings |
| POST | `/ImdbSync/Me/Test` | check the cookie against IMDb |
| POST | `/ImdbSync/Me/Sync` | start a full sync in the background |
| POST | `/ImdbSync/Me/Reset` | forget what was already sent |
| GET / DELETE | `/ImdbSync/Me/Log` | activity log (newest first, last 500 entries) / clear it |
| POST | `/ImdbSync/Me/Watchlist/Sync` | run the watchlist ↔ playlist sync now |
| POST | `/ImdbSync/Me/Watchlist/Reset` | re-merge: forget the last state, next sync merges without removing |
| POST | `/ImdbSync/Users/{id}/Watchlist/Sync?enable=` | admin: run (and optionally enable) a user's watchlist sync |
| GET | `/ImdbSync/Users/{id}/Log` | admin: a user's activity log |
| GET | `/ImdbSync/Users` | admin overview (admins only) |

## Build

```sh
dotnet publish Jellyfin.Plugin.ImdbSync.slnx -c Release
```

Copy `Jellyfin.Plugin.ImdbSync/bin/Release/net10.0/publish/Jellyfin.Plugin.ImdbSync.dll` to `<jellyfin-data>/plugins/ImdbSync/` and restart Jellyfin.
