# SmartRouting API — Dokumentation

Stand: 2026-07-30 · Gültig ab Migration `20260730073419_ConsolidateDomainModel`

> Running your own installation? → [Self-hosting and configuration](#self-hosting-and-configuration)

---

## Die drei Dokumente

| Datei | Beantwortet | Zielgruppe |
|---|---|---|
| **[SYSTEM_REFERENCE.md](SYSTEM_REFERENCE.md)** | Was ist das System, welche Felder gibt es und was bewirken sie? | alle |
| **[EXTERNAL_IMPORTER.md](EXTERNAL_IMPORTER.md)** | Wie werden Bestand, Mengen und Prioritäten geliefert — und wie entsteht daraus eine Mengenprognose? | vorgelagerter Datendienst |
| **[OPERATIONS.md](OPERATIONS.md)** | Wie konfiguriere und betreibe ich die Anwendung? | Betrieb |

Jedes Dokument hat **genau eine** Aufgabe. Steht eine Aussage in zwei Dokumenten, ist eine davon
falsch — bitte melden statt beide anzupassen.

---

## Regeln für diese Dokumentation

Diese Regeln sind der Grund, weshalb die vorherigen fünf Dokumente
(`API_DOCUMENTATION.md`, `API_CHANGES_FOR_FRONTEND.md`, `API_CHANGES_ROUTING_ENGINE.md`,
`API_REMEDIATION_PLAN.md`, `IMPLEMENTATION_CHANGES.md`) **gelöscht** und nicht überarbeitet wurden.
Sie beschrieben denselben Sachverhalt mehrfach, widersprachen sich gegenseitig und keines war als
maßgeblich gekennzeichnet. Eine Dokumentation, der man nicht trauen kann, ist schädlicher als keine:
Sie kostet Lesezeit und führt zu falschen Entscheidungen.

1. **Der Code ist die Wahrheit über das *Wie*. Diese Dokumente sind die Wahrheit über das *Warum*.**
   Feldlisten, Wertebereiche und Endpunkte gehören als XML-Kommentar an den Code und erscheinen
   automatisch in Swagger. Hier steht nur, was aus dem Code nicht hervorgeht.

2. **Kein Änderungsprotokoll.**
   Wer wissen will, was sich geändert hat, liest `git log`. Ein handgepflegtes Protokoll veraltet
   ab dem ersten Tag.

3. **Keine Pläne.** `API_REMEDIATION_PLAN.md` beschrieb Absichten. Ein Plan im `docs/`-Ordner ist
   nach der Umsetzung nicht mehr von einer Beschreibung des Ist-Zustands zu unterscheiden — und
   genau daraus entstand die Verwirrung, welches Dokument den tatsächlichen Zustand beschreibt.
   Vorhaben gehören in Issues.

4. **Die maschinenlesbare Schnittstellenbeschreibung ist Swagger, nicht Markdown.**
   `GET /swagger/v1/swagger.json` im laufenden Betrieb, oder in einem Debug-Build
   `POST /api/dev/schema/export`.

---

## Der 60-Sekunden-Überblick

Die Anwendung plant **Sammeltouren für Glascontainer** beim Abfallwirtschaftszweckverband
Ostthüringen.

```
                    ┌──────────────────────────────────────┐
  Nächtlicher       │            SmartRouting API          │
  Import      ─────▶│  (.NET 8, MySQL, JWT)                │
  (Füllstände)      │                                      │
                    │   Sammelpunkte ─┐                    │
  Frontend    ─────▶│   Abladestellen ├─▶ OR-Tools ──▶ Route
  (Disposition)     │   Fahrzeuge     │      │             │
                    │   Fraktionen  ──┘      ▼             │
                    │                    Valhalla          │
                    │              (Matrix + Geometrie)    │
                    └──────────────────────────────────────┘
```

Fünf Sätze, die den Rest erklären:

1. Ein **Sammelpunkt** (`Job`) ist ein Containerstandort mit einer Menge und einer Priorität.
2. Eine **Abladestelle** (`DeliveryDestination`) ist ein Ort, an dem das Fahrzeug entleert wird.
   Es gibt mehrere, und der Optimierer wählt **je Fahrzeugladung** die günstigste.
3. Eine **Fraktion** (`Skill`) verbindet alles: Ein Sammelpunkt wird nur bedient, wenn Route und
   Fahrzeug seine Fraktion beherrschen.
4. Eine **Route** wird nicht eingegeben, sondern **berechnet**. Zielgröße ist CO2e, Nebenbedingung
   ist die Schichtdauer.
5. Das Ergebnis sind **Halte** (`Step`) in Fahrtreihenfolge — inklusive der Entleerungen, die der
   Optimierer selbst eingeplant hat.

Details in [SYSTEM_REFERENCE.md](SYSTEM_REFERENCE.md).

---

## Self-hosting and configuration

The source contains **no host names, domains or IP addresses of any particular operator**. Everything
that differs per installation — database, Valhalla endpoint, frontend origin, token issuer, proxy
addresses, the operator details shown in Swagger — is configuration.

The committed `appsettings.json` is a **demo configuration**. It is complete enough to start in
`Development` (after setting the secrets below), but deliberately **refuses to start in
`Production`** until you have supplied your own values. Every startup check names the exact key or
environment variable that is missing or malformed.

### Prerequisites

| Component | Requirement | Notes |
|---|---|---|
| Runtime | ASP.NET Core **8.0**, **x64** (Windows or Linux) | x64 is required by the native Google OR-Tools solver. Build with the .NET 8 SDK. |
| Database | **MySQL 8.0** (developed against 8.0.36), `utf8mb4` | Create an empty database. Besides read/write access the application user needs DDL rights (`CREATE`, `ALTER`, `DROP`, `INDEX`, `REFERENCES`): migrations run automatically on every start. |
| Routing engine | Your own **[Valhalla](https://github.com/valhalla/valhalla)** instance with OSM data for your region | Planning and navigation use `/route` and `/sources_to_targets` with the `truck` costing; map matching uses `/trace_route` and `/trace_attributes` (default costing `auto`); availability is read from `/status`. Valhalla's service limits for `truck` must allow at least `Routing:Valhalla:MaxRouteLocationsPerRequest` route locations and `MaxMatrixLocationsPerRequest` × `MaxMatrixLocationsPerRequest` matrix pairs — otherwise lower those two settings. |
| Frontend | Any client served from a known origin | Its origin goes into `Cors:AllowedOrigins`. |
| Reverse proxy | Optional (nginx, IIS, Plesk, …) | TLS must be terminated there: the API does not redirect to HTTPS. |

The API makes **no outbound connections other than to your database and your Valhalla instance**.

### How configuration is loaded

Later sources override earlier ones:

1. `appsettings.json` — demo values, versioned
2. `appsettings.{Environment}.json` — `appsettings.Development.json` is versioned;
   `appsettings.Production.json`, `appsettings.Staging.json` and `appsettings.*.local.json` are
   **git-ignored** so real installation values cannot be committed by accident
3. User secrets — `Development` only (`dotnet user-secrets`)
4. Environment variables
5. Command-line arguments

A configuration key `Section:Key` becomes the environment variable `Section__Key` (double
underscore). Array entries are indexed: `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1`, …

**Secrets never go into a versioned file** — use environment variables or user secrets.

### Required settings

| Key | Environment variable | Example | Notes |
|---|---|---|---|
| `ConnectionStrings:DefaultConnection` | `ConnectionStrings__DefaultConnection` | `Server=db.example.internal;Port=3306;Database=smartrouting;User=smartrouting;Password=…` | **Secret.** |
| `Jwt:Key` | `Jwt__Key` | 48 random bytes, Base64-encoded | **Secret.** At least 32 bytes (HMAC-SHA256). |
| `Jwt:Issuer` | `Jwt__Issuer` | `routing-api.example.com` | Also used as audience. Demo value `SmartRoutingAPI`. Changing it invalidates all tokens already issued (users sign in again). |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0`, `__1`, … | `https://routing.example.com` | Origin of your frontend: `scheme://host[:port]`, no path, no trailing slash. Wildcards are rejected. Empty outside `Development` = startup error. |
| `Routing:Valhalla:BaseUrl` | `Routing__Valhalla__BaseUrl` | `http://valhalla.example.internal:8002` | Root URL only — a path prefix is rejected because the API calls Valhalla with absolute paths. The demo value `http://127.0.0.1:8002` only fits Valhalla on the same host. |
| `DefaultAccount:Password` | `DefaultAccount__Password` | — | **Secret.** Needed only for the very first sign-in, see [First start](#first-start). |

Generate a JWT key with a cryptographically secure generator, e.g.:

```bash
openssl rand -base64 48
```

### Optional settings

| Key | Default | Purpose |
|---|---|---|
| `Database:ServerVersion` | `8.0.36` | MySQL version the SQL is generated for. Set it to your server version. |
| `Security:TrustedProxyAddresses` | empty | IP addresses (single addresses, IPv4 or IPv6 — no host names, no CIDR ranges) of reverse proxies allowed to set `X-Forwarded-For` / `X-Forwarded-Proto`. **Set this when running behind a proxy**, otherwise all anonymous callers share the proxy's rate-limit quota and one caller can block sign-in for everybody. Only the nearest proxy is evaluated. Leave empty for direct Kestrel or IIS in-process hosting. |
| `Valhalla:BaseUrl` | value of `Routing:Valhalla:BaseUrl` | Separate Valhalla instance for map matching only. |
| `Valhalla:TimeoutSeconds` | `30` | Timeout of map-matching calls. |
| `Routing:Valhalla:TimeoutSeconds` | `120` | Timeout of planning and navigation calls. |
| `Routing:Valhalla:MaxMatrixLocationsPerRequest`, `MaxRouteLocationsPerRequest` | `50` | Block sizes; must fit your Valhalla service limits (see prerequisites). |
| `OpenApi:Title` | `SmartRouting API` | Title in Swagger UI. |
| `OpenApi:Description`, `TermsOfServiceUrl`, `ContactName`, `ContactEmail`, `ContactUrl`, `LicenseName`, `LicenseUrl` | empty | Operator details in Swagger UI. Empty fields are omitted. URLs must be absolute `http(s)`; `LicenseUrl` requires `LicenseName`. |
| `DefaultAccount:Username` | `admin` | User name of the bootstrap administrator. |
| `DefaultAccount:Email` | `admin@localhost` | E-mail of the bootstrap administrator. |
| `AllowedHosts` | `*` | Host-header filter. Restricting it to your API host name is recommended. |
| `Routing:Vehicle`, `Routing:ServiceTimes`, `Routing:Emissions`, `Routing:Planner`, `Routing:Navigation`, `Routing:Sovereign` | see `appsettings.json` | Domain tuning (vehicle profile, CO2e model, solver). Read [OPERATIONS.md](OPERATIONS.md) before going live — the emission factors are uncalibrated starting values. |
| `ASPNETCORE_URLS` (environment only) | `http://localhost:5000` | Listening address when self-hosting with Kestrel, e.g. `http://0.0.0.0:8080`. |

### Example: production configuration

Non-secret values in `appsettings.Production.json` next to the published binaries (git-ignored):

```json
{
  "AllowedHosts": "routing-api.example.com",
  "Cors": { "AllowedOrigins": [ "https://routing.example.com" ] },
  "Jwt": { "Issuer": "routing-api.example.com" },
  "Security": { "TrustedProxyAddresses": [ "10.0.0.5" ] },
  "Database": { "ServerVersion": "8.0.36" },
  "Routing": { "Valhalla": { "BaseUrl": "http://valhalla.example.internal:8002" } },
  "OpenApi": {
    "Description": "Collection route planning, operated by Example Waste Services Ltd.",
    "ContactName": "Example IT Service Desk",
    "ContactEmail": "api@example.com"
  }
}
```

Secrets as environment variables of the process (systemd unit, container, IIS application pool or
Plesk settings — not the published `web.config`, which is regenerated on every publish):

```bash
ConnectionStrings__DefaultConnection="Server=db.example.internal;Port=3306;Database=smartrouting;User=smartrouting;Password=<secret>"
Jwt__Key="<output of openssl rand -base64 48>"
DefaultAccount__Password="<initial administrator password>"
```

Every value can equally be supplied as an environment variable instead of the JSON file.

For local development, store the secrets with user secrets (from this project's directory);
`appsettings.Development.json` already allows the frontend at `http://localhost:3000`:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=smartrouting;User=dev;Password=<secret>"
dotnet user-secrets set "Jwt:Key" "<at least 32 bytes>"
dotnet user-secrets set "DefaultAccount:Password" "<initial administrator password>"
```

### First start

1. Create the empty database (`CREATE DATABASE smartrouting CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;`)
   and a user with the rights listed above.
2. Provide the required settings.
3. Start the API. All migrations are applied automatically; a configuration error stops the start
   with a message naming the key.
4. Sign in once with `POST /api/Login` using `DefaultAccount:Username` and
   `DefaultAccount:Password`. This creates the administrator account. From then on the
   `DefaultAccount` section has no effect — remove the password variable.
5. Check Valhalla with `GET /api/MapMatching/status` (requires the token from step 4 and the
   `ViewRoutes` right, which the administrator has).
6. Swagger UI is available at `/swagger`.

### Operational notes

- **HTTPS:** the API itself does not redirect to HTTPS — terminate TLS at your proxy or IIS.
- **Swagger** (`/swagger`) is reachable without authentication in every environment. Restrict it at
  the proxy if your API description must not be public.
- **Single instance:** the route-optimisation queue lives in memory. Do not run several instances
  behind a load balancer.
- **Do not reuse secrets from another installation:** generate your own `Jwt:Key`, database
  credentials and administrator password.
- Secrets storage, database migrations and the pre-production checklist: [OPERATIONS.md](OPERATIONS.md).
