# Jellyfin IMDb Sync

A Jellyfin plugin that marks everything a user watches in Jellyfin as **watched on their own IMDb account**.

- **Right away:** when a movie/episode finishes (or is marked played), it is sent to IMDb immediately.
- **Daily catch-up:** the scheduled task *Sync watch history to IMDb* (04:00 by default) sends every played item that is not on IMDb yet, e.g. the history from before the plugin was installed.
- **Per user:** every Jellyfin user connects their own IMDb account by pasting their IMDb cookie on a self-service page. Admins never have to handle other people's cookies.

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
4. Press **Sync now** to push your existing history once.

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

### API

All endpoints need a Jellyfin user session (not an API key) and act on the calling user.

| Method | Path | |
| --- | --- | --- |
| GET | `/ImdbSync/Page` | self-service page (anonymous, authenticates in the browser) |
| GET / POST / DELETE | `/ImdbSync/Me` | read / update / delete own settings |
| POST | `/ImdbSync/Me/Test` | check the cookie against IMDb |
| POST | `/ImdbSync/Me/Sync` | start a full sync in the background |
| POST | `/ImdbSync/Me/Reset` | forget what was already sent |
| GET | `/ImdbSync/Users` | admin overview (admins only) |

## Build

```sh
dotnet publish Jellyfin.Plugin.ImdbSync.slnx -c Release
```

Copy `Jellyfin.Plugin.ImdbSync/bin/Release/net10.0/publish/Jellyfin.Plugin.ImdbSync.dll` to `<jellyfin-data>/plugins/ImdbSync/` and restart Jellyfin.
