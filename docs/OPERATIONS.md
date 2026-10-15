# Betrieb und Konfiguration

Stand 2026-07-30

---

## 1. Geheimnisse

Secrets (`Jwt:Key`, `ConnectionStrings:DefaultConnection`, `DefaultAccount:Password`) are never
stored in versioned files. They are read at runtime from environment variables or
`dotnet user-secrets`. Anyone who knows `Jwt:Key` can forge arbitrary tokens, including
administrator rights.

Die Anwendung prüft beim Start und **verweigert den Start**, wenn

- `Jwt:Key` fehlt oder kürzer als 32 Byte ist (Anforderung von HMAC-SHA256),
- `DefaultAccount:Password` den Platzhalter `CHANGE_ME` trägt,
- `ConnectionStrings:DefaultConnection` fehlt,
- `Cors:AllowedOrigins` außerhalb der Entwicklung leer ist,
- `Routing:Sovereign:Enabled` gesetzt ist, ohne dass `LegalBasis` angegeben wurde.

> The installation-specific checks (Valhalla URL, CORS origin format, proxy addresses, OpenAPI
> links) are listed in [README.md → Self-hosting and configuration](README.md#self-hosting-and-configuration).

### Ablage

Doppelter Unterstrich ersetzt den Doppelpunkt.

```powershell
# Entwicklung
dotnet user-secrets set "Jwt:Key" "<mindestens 32 Byte>"
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=...;Database=...;User=...;Password=..."
dotnet user-secrets set "DefaultAccount:Password" "<neues Passwort>"

# Produktion (Umgebungsvariablen)
setx Jwt__Key "<...>"
setx ConnectionStrings__DefaultConnection "<...>"
setx DefaultAccount__Password "<...>"
```

Schlüssel erzeugen:

```powershell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
```

> `DefaultAccount` wirkt **ausschließlich**, solange die Tabelle `UserAccounts` leer ist. Nach dem
> Anlegen des ersten echten Kontos ist der Abschnitt wirkungslos und kann entfernt werden.

---

## 2. Reverse Proxy — sonst ist die Ratenbegrenzung wirkungslos

Anonyme Aufrufer werden nach `RemoteIpAddress` partitioniert. Hinter einem Reverse Proxy ist das die
IP **des Proxys**: Alle anonymen Aufrufer teilen sich dann eine Partition von 60 Anfragen pro
Stunde — die Anmeldung fällt für alle aus, sobald ein Einzelner das Kontingent verbraucht.

Forwarded headers are configured, not coded. List the address of every reverse proxy that may set
`X-Forwarded-For` / `X-Forwarded-Proto`:

```json
{ "Security": { "TrustedProxyAddresses": [ "10.0.0.5" ] } }
```

or `Security__TrustedProxyAddresses__0=10.0.0.5` as an environment variable. Only listed addresses
are trusted (the loopback defaults are removed), so callers cannot spoof `X-Forwarded-For` to mint
new rate-limit partitions. With an empty list, forwarded headers are not evaluated at all. Details:
[README.md → Self-hosting and configuration](README.md#self-hosting-and-configuration).

### Kontingente

Drei verkettete Fenster; der strengste Wert gilt. Angemeldete Aufrufer werden nach Benutzerkennung
partitioniert, anonyme nach IP.

| | anonym | angemeldet | Administrator |
|---|---|---|---|
| pro Sekunde | 1 | 6 | 10 |
| pro Minute | 10 | 60 | 300 |
| pro Stunde | 60 | 750 | 2500 |

Lesende Listen-Endpunkte tragen `[ForgivingRateLimit]` und erhalten deutlich größere Kontingente
(bis 450/Minute für angemeldete Aufrufer), damit das Frontend Listen zügig laden kann.

---

## 3. Datenbank

### Migration anwenden

Die Anwendung führt beim Start `Database.Migrate()` aus (`MigrationManager`). Ein Start genügt.

### Zur Migration `ConsolidateDomainModel`

Sie ist **datenerhaltend gestaltet**, aber sie entfernt Tabellen und Spalten.

> **Vor der Anwendung eine Sicherung anlegen.** Der Rückwärtslauf stellt das Schema wieder her,
> nicht die Daten.

Sie führt aus:

- Übernahme der Zuordnung Fraktion ↔ Abladestelle in die neue Verknüpfungstabelle
- Übernahme der Fahrzeiten aus `Steps.Duration` nach `TravelTimeFromStart`
- Übernahme der Strecken aus `Steps.Distance` nach `DistanceFromStartMeters`
- Umrechnung von `Steps.LoadJson` nach `LoadUnitsAfter`
- **Neubelegung der Zahlenwerte von `Steps.StepType`** (`Pickup` 2 → `Collection` 1 usw.)
- Nachtragen von `Steps.Sequence` je Route
- Rekonstruktion von `Steps.JobId` / `DeliveryDestinationId` über Koordinatenvergleich
- Eindeutigmachen doppelter Anmeldenamen vor dem Anlegen des eindeutigen Index

> **Hinweis zu Altbeständen:** Vor der Umstellung gespeicherte Halte konnten in `LoadJson` um Faktor
> 1000 vergrößerte Werte enthalten. Diese werden unverändert übernommen und erscheinen daher zu
> groß. Betroffene Routen sind neu zu berechnen. Bei einem Neuaufbau der Datenbank entfällt das.

### Vollständiger Neuaufbau

Da die Anwendung noch nicht im Wirkbetrieb ist, ist der Neuaufbau der einfachere Weg:

```sql
DROP DATABASE SmartCityRouting;
CREATE DATABASE SmartCityRouting CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
```

Anschließend die Anwendung starten; alle Migrationen laufen der Reihe nach durch.

### Serverversion

Nicht mehr über `ServerVersion.AutoDetect` ermittelt, sondern aus `Database:ServerVersion`
(Vorgabe `8.0.36`).

> `AutoDetect` stellte während der Diensteregistrierung — also synchron im Anwendungsstart — eine
> echte Verbindung her. War die Datenbank in diesem Moment nicht erreichbar, startete die Anwendung
> nicht, und das erzeugte Modell hing von der Version des gerade erreichbaren Servers ab.

```json
{ "Database": { "ServerVersion": "8.0.36" } }
```

### Migrationen erzeugen

Der Entwurfszeit-Kontext (`RoutingDBContextFactory`) benötigt **keine** erreichbare Datenbank.

```powershell
dotnet ef migrations add <Name> --project SmartRoutingAPI.csproj
```

> Die Meldung *„An error occurred while accessing the Microsoft.Extensions.Hosting services“* ist
> hier **erwartet**: Die Werkzeuge versuchen zunächst, die vollständige Anwendung aufzubauen; deren
> Startprüfungen greifen ohne Geheimnisse. Anschließend wird die Fabrik verwendet.
>
> Das lokale Werkzeugmanifest (`.config/dotnet-tools.json`) legt `dotnet-ef` 8.0.7 mit
> `rollForward: false` fest und lässt sich mit einem neueren SDK nicht starten. Entweder das
> Manifest anheben oder das global installierte Werkzeug verwenden
> (`~/.dotnet/tools/dotnet-ef.exe`).

**Jede erzeugte Migration ist vor dem Übernehmen zu lesen.** Die Werkzeuge erkennen Umbenennungen
allein über Spaltenreihenfolge und kennen die Bedeutung eines Feldes nicht. Bei
`ConsolidateDomainModel` schlugen sie vor, `Steps.Distance` in `Steps.JobId` umzubenennen — aus der
gefahrenen Strecke wäre ein Fremdschlüssel geworden. Der Kommentarkopf jener Migration listet alle
vier gefundenen Fehler.

---

## 4. Valhalla

```json
{
  "Routing": {
    "Valhalla": {
      "BaseUrl": "http://127.0.0.1:8002",
      "TimeoutSeconds": 120,
      "MaxMatrixLocationsPerRequest": 50,
      "MaxRouteLocationsPerRequest": 50
    }
  }
}
```

`MaxMatrixLocationsPerRequest` muss zu `max_matrix_locations` der Valhalla-Konfiguration passen.
Größere Matrizen werden automatisch in Blöcke zerlegt.

> **Leistungshinweis:** Die Blöcke werden **streng nacheinander** abgefragt — bei N Punkten und
> Blockgröße B sind das ⌈N/B⌉² Aufrufe in Folge. Bei 400 Punkten und Blockgröße 50 sind das 64
> Anfragen. Eine Erhöhung von `max_matrix_locations` in Valhalla auf 100 viertelt diese Zahl und ist
> die wirksamste Einzelmaßnahme für die Laufzeit.

Verfügbarkeit prüfen: `GET /api/MapMatching/status`.

---

## 5. Prüfliste vor dem Wirkbetrieb

### Sicherheit

- [ ] `Jwt:Key` erzeugt (≥ 32 Byte) und ausschließlich per Umgebungsvariable bzw. user-secrets hinterlegt
- [ ] Eigener Datenbankbenutzer für die Anwendung (kein `root`)
- [ ] `DefaultAccount:Password` gesetzt, Administratorkonto angelegt, Variable danach entfernt
- [ ] `Cors:AllowedOrigins` auf den Ursprung des Frontends gesetzt
- [ ] `Security:TrustedProxyAddresses` mit Positivliste konfiguriert (falls Proxy)
- [ ] HTTPS erzwungen (`app.UseHttpsRedirection()` ist derzeit auskommentiert)
- [ ] Geprüft, dass `POST /api/Accounts/resetPasswordConfirm` **nicht mehr erreichbar** ist

### Fachliche Voraussetzungen

- [ ] Mindestens eine **Abladestelle** mit realistischer `serviceTimeSeconds` angelegt
- [ ] Fraktionen angelegt (Weißglas, Grünglas, Braunglas)
- [ ] Fahrzeuge mit realistischer `capacity` **und** zugeordneten Fraktionen
- [ ] Geprüft, dass sich Route und Fahrzeug **mindestens eine Fraktion teilen** — sonst gibt es
      keine Kandidaten und jede Berechnung schlägt fehl
- [ ] `Routing:Emissions:PayloadUnitKilograms` kalibriert — siehe unten

### Emissionsfaktoren

> **Die mitgelieferten Werte sind Startwerte, kein Nachweis.**
>
> Besonders `PayloadUnitKilograms = 1.0` bei Sammelmengen in Kubikmetern ergibt eine physikalisch
> falsche Massenbilanz: Ein Container mit 1,2 m³ Altglas wird mit 1,2 kg gerechnet statt mit etwa
> 360 kg. Der lastabhängige Anteil der CO2e-Rechnung ist damit um mehr als zwei Größenordnungen zu
> klein — die Bilanz besteht praktisch nur aus dem Grundverbrauch.
>
> Vor jeder Veröffentlichung von CO2-Kennzahlen zu klären:
>
> - [ ] Was ist eine Kapazitätseinheit — m³, kg oder Behälter?
> - [ ] `PayloadUnitKilograms` entsprechend setzen (bei m³ Altglas etwa 300)
> - [ ] `BaseGramsPerKm` gegen den eigenen Fuhrpark kalibrieren
> - [ ] `FactorSource` auf die tatsächlich verwendete Quelle setzen — der Wert wird je Route
>       mitgespeichert und ist die Grundlage der Nachprüfbarkeit

### Betrieb

- [ ] Datenbanksicherung eingerichtet
- [ ] Valhalla-Instanz erreichbar und mit aktuellen Kartendaten versorgt
- [ ] Zeitplaner für den nächtlichen Import eingerichtet, **Rückgabewert wird ausgewertet**
- [ ] Bekannt: Auftragsstatus liegt im Arbeitsspeicher — kein Mehr-Instanz-Betrieb ohne
      gemeinsamen Speicher
