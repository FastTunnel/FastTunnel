<div align="center">
  
<img src="images/logo.png" width="150" align=center />

## FastTunnel
[![License](https://img.shields.io/badge/license-Apache%202-green.svg)](https://www.apache.org/licenses/LICENSE-2.0)
[![Build](https://github.com/FastTunnel/FastTunnel/actions/workflows/ci.yml/badge.svg?branch=v3)](https://github.com/FastTunnel/FastTunnel/actions/workflows/ci.yml)
[![Nuget](https://img.shields.io/nuget/v/FastTunnel.Core)](https://www.nuget.org/packages/FastTunnel.Core/)
[![Nuget](https://img.shields.io/nuget/dt/FastTunnel.Core)](https://www.nuget.org/packages/FastTunnel.Core/)

[README](README.md) | [中文文档](README_zh.md)
  
  ***This project supports any commercial and secondary development activities, but seriously despises plagiarizing and copying the code, implementation scheme or architecture of this project and repackaging them into their own open source works.***
  
</div>


## What is FastTunnel？
- FastTunnel is a high-performance cross-platform intranet penetration tool. With it, you can expose intranet services to the public network for yourself or anyone to access.
- Unlike other penetration tools, the FastTunnel project is committed to creating an easy-to-extensible and easy-to-maintain intranet penetration framework.
- You can build your own penetration application by referencing the nuget package of `FastTunnel.Core`, and target the business extension functions you need.


***

Official website : https://suidao.io  

The penetration platform developed based on this framework, if you need intranet penetration, you can register and use it directly, eliminating the cost of building and maintaining yourself.
But do not use this service for important items.

OpenSource：

GitHub : [FastTunnel](https://github.com/FastTunnel/FastTunnel)  
Gitee: [FastTunnel](https://gitee.com/Hgui/FastTunnel)

**If helpful, click on ⭐Star to support this project, please submit an issue if you have needs and bugs, and welcome coder to PR**

## Get GVP

![img1](images/gvp.png)
***

## What can FastTunnel do?
- [x] Reach intranet machines (Windows / Linux / macOS) from anywhere
- [x] Expose intranet web services via subdomains or custom domains (WeChat development, public previews)
- [x] TCP / UDP port forwarding for any intranet service (MySQL, Redis, RDP, ...)
- [x] Bind multiple domains to one client
- [x] Custom domain validation: rejects malformed hosts, platform-subdomain squatting and hijacking another client's mapping
- [x] Server-side custom domain blacklist (`FastTunnel:WebDomainBlacklist`)
- [x] Single-port multiplexing (MUX): all forwards share one long-lived connection instead of reconnecting per request
- [x] TLS for the client channel (on by default) with SHA-256 certificate fingerprint pinning
- [x] Control-channel heartbeat / stale-connection detection; structured protocol stays compatible with older clients
- [x] MUX data plane over TCP or WebSocket (traverses HTTP(S)-only firewalls)
- [x] Dual-stack IPv4 / IPv6 for listeners, port forwards and intranet targets
- [x] Client authentication (Token); the matched credential is bound to the session for auditing / bans
- [x] Visitor IP allowlist (`FastTunnel:WebAllowAccessIps`, IP or CIDR)
- [x] Real client IP recovery: forwarding headers trusted only from `FastTunnel:TrustedProxyIps`
- [x] Abuse takedown: banned hosts/tokens get `451`, mappings are revoked, sessions disconnected, audit written
- [x] Disposition API (`api/Abuse/*`): ban / unban / human review / anonymous report; automation cannot permanently ban
- [x] One token per account (`FastTunnel:Clients`): stable subject, domain authorization, single-session enforcement, account-level bans
- [x] Ops endpoints: `/healthz`, `/metrics` (Prometheus text), `api/Ops/Traffic` per-proxy JSON
- [x] Per-proxy traffic accounting and token-bucket rate limiting (`FastTunnel:Bandwidth`), all four data paths covered
- [x] Multi-format config files: the server loads `appsettings.json` / `appsettings.ini`
      under `config/`, the client the same variants in its app directory; later files override
      earlier ones, all hot-reloadable
- [ ] Content inspection (out-of-band review, active crawling): requires user-agreement consent first
- [ ] P2P / STCP / XTCP
- [ ] Dashboard / Admin UI (metrics via `/metrics`, health via `/healthz` are already available)

> Two different "whitelists": the "domain name whitelist" claimed by older docs was never
> implemented — `WebAllowAccessIps` had no reader and was removed in v3.3.0. It is now
> **wired up for real** as a visitor IP allowlist. `WebDomainBlacklist` instead refuses
> registration of certain custom domains.
>
> When running behind a reverse proxy, always set `TrustedProxyIps` to your own proxy's
> source IP / CIDR. Without it, forwarding headers are ignored and the TCP peer IP is used;
> setting `0.0.0.0/0` would let visitors forge their own IP.

> **One token per account**: add one `{ Id, Token }` per user to server-side
> `FastTunnel:Clients`, and put that token in the client's `FastTunnel:Token`. Optional
> `SubDomain` / `AllowedHosts` restrict registerable hosts; `EnforceSingleSession` (default
> true) makes a new login replace an older session of the same subject. Legacy shared
> `Tokens` still work but are labelled `legacy:*` — banning such a token hits every user
> sharing it, and single-session enforcement does not apply. Once the `Clients` section
> exists, token validation is mandatory even if every entry is disabled.
>
> **Ops endpoints** (use the server's own address + `BinPort`, not a mapped business host):
> `GET /healthz` (JSON overview + config warnings), `GET /metrics` (Prometheus text),
> `GET /api/Ops/Traffic` (JWT, per-proxy bytes / connections / throttle delay).
> `FastTunnel:Bandwidth` sets byte/sec limits with precedence
> `PerProxy` > `PerClient` > `Clients[].BandwidthLimit` > `DefaultBytesPerSecond`; 0 = unlimited.
> In production, restrict `/healthz` and `/metrics` by source IP at the gateway, or set
> `Ops:PublicEndpoints` to `false`.

> **Multi-format config**: the server loads every existing `appsettings.{ext}` under `config/`
> in order `json → ini` (later files override earlier ones), then appends environment variants
> `appsettings.{Environment}.{ext}`; same rules apply to the client, whose JSON is loaded by the
> default pipeline while other formats are picked up automatically. All files hot-reload.
> INI has no native arrays — use indexed sections (`[FastTunnel:Clients:0]`) for lists, and keep
> deeply nested structures in JSON. See `FastTunnel.Server/config/appsettings.sample.ini` and
> `FastTunnel.Client/appsettings.sample.ini` for examples.

## Quickstart
1. Download the corresponding program on the [releases](https://github.com/FastTunnel/FastTunnel/releases) page
2. Modify the client and server configuration files according to your needs`appsettings.json`
3. Run FastTunnel.Server
4. Run FastTunnel.Cient

## Install FastTunel.Sever using Docker Engine
Configuration files and log files are mounted through volume. If this image has been run before, docker may not update to the latest image. Please delete the existing image manually, and then execute the following command

```
docker run --detach \
  --publish 1270:1270 \
  --name FastTunnel \
  --restart always \
  --volume /var/FastTunnel/config:/app/config \
  --volume /var/FastTunnel/Logs:/app/Logs \
  springhgui/fasttunnel:latest
```

> The server listens only on `FastTunnel:BinPort` (1270 by default). `EnableForward` is
> `false` by default: raw TCP/UDP forwarding bypasses the HTTP parsing layer entirely, so
> neither the real visitor IP nor any content audit is possible. Enable it explicitly only
> when needed, and add `--publish <port>:<port>` for every `RemotePort`. Port 1271 from
> older docs no longer exists.

## Run on Linux/Mac os？
#### Windows
Double click directly `FastTunnel.Client.exe` to run
#### Linux
`chmod +x FastTunnel.Client`  
`./FastTunnel.Client`
#### Mac
click directly `FastTunnel.Client` to run

## Configuration example
### 1. Use a custom domain name to access intranet web services
- For example, you have a server with a public IP address of `110.110.110.110`, and you have a domain name with a top-level domain name of `abc.com`, you want to visit a website on the intranet by visiting `test.abc.com`
- You need to add a DNS resolution for the domain name address, the type is `A`, the name is `*`, and the ipv4 address is `110.110.110.110`, so that all domain names of `*.abc.com` will point to `110.110.110.110`’s server, because the default http port of `FastTunnel` is 1270, so you need to visit`http://test.abc.com:1270`

- #### If you don't want to bring the port number every time you visit, you can use `nginx` forwarding.
```
http {
    # add resolver 
    resolver 8.8.8.8;

    # set *.abc.com to 1270 port
    server {
      server_name  *.abc.com;
      location / {
         proxy_pass http://$host:1270;
         proxy_set_header   Host             $host;
         proxy_set_header   X-Real-IP        $remote_addr;
         proxy_set_header   X-Forwarded-For  $proxy_add_x_forwarded_for;
      }

      # 可选
      error_log /var/log/nginx/error_ft.log error;
    }
}
```

- If the domain name configured on the server is `ft.suidao.io`, then access the local site through the subdomain name `test.ft.suidao.io:1270`, the IIS configuration is as follows:
![img1](images/iis-web.png)

### 2. Client configuration (v3)

The v3 configuration root is `FastTunnel`; the v2 `ClientSettings` / `SSH` layout is obsolete. Example with an intranet Linux host and a Windows Remote Desktop host:
appsettings.json
```
{
  "FastTunnel": {
    "Server": { "ServerAddr": "x.x.x.x", "ServerPort": 1270, "Protocol": "wss" },
    "Token": "TOKEN_FOR_CLIENT_AUTHENTICATION",
    "EnableMux": true,
    "MuxTransport": "tcp",
    "Webs": [
      {
        "LocalIp": "127.0.0.1",
        "LocalPort": 8090,
        "SubDomain": "test",
        "WWW": [ "www.abc.com" ]
      }
    ],
    "Forwards": [
      { "LocalIp": "192.168.0.100", "LocalPort": 22, "RemotePort": 12701, "Protocol": "TCP" },
      { "LocalIp": "192.168.0.101", "LocalPort": 3389, "RemotePort": 12702, "Protocol": "TCP" }
    ]
  }
}
```
> Custom domains in `WWW` must resolve to the server and must not be subdomains of the server's
> `WebDomain`; the server validates the host format and refuses to overwrite a mapping owned by
> another client. DNS ownership verification (TXT challenge) and ICP filing checks are not
> implemented yet — validate them in your own onboarding flow. Port forwarding is off by
> default: set `EnableForward: true` explicitly and map every `RemotePort` in the firewall / container.
>
> Two more server-side access controls live in `appsettings.json`: `WebAllowAccessIps`
> (visitor IP / CIDR allowlist, empty means no restriction) and `TrustedProxyIps`
> (trusted reverse-proxy source IP / CIDR). Decisions use the recovered real client IP,
> never the visitor-supplied `X-Forwarded-For` alone. When nginx terminates TLS in front of
> FastTunnel it must pass through `Host` and `X-Forwarded-For`, and its source address must be
> listed in `TrustedProxyIps`; see appendix A of `docs/content-compliance-plan-v0.1.md`.

#### remote intranet linux host by ssh (ip:192.168.0.100)

Assuming that the user name of the intranet host is root, the server ip is x.x.x.x, and the two hosts that access the intranet are as follows
```
ssh -oPort=12701 root@x.x.x.x
```

#### remote desktop Windows host by mstsc (ip:192.168.0.101)
#### Controlled terminal setting
- Open cmd and enter the command `sysdm.cpl` in the pop-up dialog box and select Allow remote connection to this computer

![img1](images/setallow.png)
#### Control terminal settings
- Open cmd and enter the command `mstsc`, open the remote dialog box, enter `x.x.x.x:12702` in the computer input box of the dialog box, and then specify the user name and password to remote the windows host of the intranet
![img1](images/remote.png)
    
## Development/PR
- Install the .NET 10 SDK and Visual Studio 2022 / VS Code (.NET 5 / VS2019 from v2 docs no longer apply)
- Add `test.test.cc 127.0.0.1` to the hosts file (the server defaults to `WebDomain=test.cc`)
- `dotnet run --project FastTunnel.Server` to start the server
- `dotnet run --project FastTunnel.Client` to start the client
- `dotnet test FastTunnel.Core.Tests` to run the unit tests

## contributors
<a href = "https://github.com/FastTunnel/FastTunnel/graphs/contributors">
  <img src = "https://contrib.rocks/image?repo=FastTunnel/FastTunnel"/>
</a>

## Join QQ Group

<div align="center"><img src="images/qqgroup.png" width="150" align=center /></div>  

## License
Apache License 2.0
