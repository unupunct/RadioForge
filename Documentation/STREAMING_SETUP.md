# Connecting RadioForge to your streaming server

You need three things from your hosting provider (control panel or welcome e-mail): **server address**, **port** and the **source password**. Use the *source / DJ / broadcaster* password, **not** the admin password.

## Icecast 2
| Field | Value |
|---|---|
| Host | the server name, e.g. `stream.example.com` (no `http://`) |
| Port | the Icecast port, often `8000` |
| Username | `source` (unless your host says otherwise) |
| Password | the **source** password |
| Mount point | e.g. `/stream` or `/live` (must start with `/`) |

RadioForge streams with an HTTP `PUT` request (Icecast 2.4+) and falls back to the classic `SOURCE` request for older servers.

## SHOUTcast v1 / v2
| Field | Value |
|---|---|
| Host | server address |
| Port | the **listener** port shown in your panel (for example `8000`). RadioForge connects to **port + 1** for the source, as SHOUTcast requires |
| Password | the source / DJ password |
| Stream ID (v2) | usually `1`; sent as `password:#ID` when it is not 1 |

**Scope note:** SHOUTcast v2 servers are driven through the legacy source protocol that DNAS 2 servers still accept (with stream ID). The newer framed "v2 source protocol" (in-band metadata, artwork) is not implemented; metadata goes through the admin web interface instead.

## Test connection
*Streaming Servers → Test connection* checks: configuration, encoder availability, reachability (TCP/TLS), authentication, protocol/server type, mount point (content type accepted) and whether listener statistics are exposed. For Icecast the mount is opened and closed immediately. No audio is sent, but a monitoring tool may notice a source connecting for a moment.

## Metadata (now playing)
* Icecast: `/admin/metadata?mode=updinfo` with the source credentials.
* SHOUTcast: `/admin.cgi?mode=updinfo` with the password (plus `sid=` for v2).

If a host blocks these URLs the server log shows *Metadata update failed* and RadioForge keeps streaming. Metadata can be switched off per server.

## Listener counts
Icecast: `/status-json.xsl`. SHOUTcast: `/stats?json=1` or `/7.html`. Hosts that hide these pages show **n/a**. RadioForge never invents a number.

## Several destinations
Add as many servers as you like. Each has its own codec, bitrate, sample rate and channels, and its own reconnect policy. One failing server never interrupts the others. Typical bitrates are 64 / 128 / 192 / 256 kbps; your host's plan decides the maximum.

## What has been verified
Protocol code is covered by automated tests against local test servers that speak the Icecast and SHOUTcast source protocols (see the repository README). It has **not** been run against real Icecast or SHOUTcast (DNAS) servers or commercial hosts. Use *Test connection* with your own host and report problems.
