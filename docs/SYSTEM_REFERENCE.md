# SmartRouting API — Systemreferenz

**Dies ist das maßgebliche Dokument zum Datenmodell und zur Funktionsweise.**
Stand: 2026-07-30 · Gültig ab Migration `20260730073419_ConsolidateDomainModel`

---

## Inhalt

1. [Was das System tut](#1-was-das-system-tut)
2. [Die sechs Begriffe](#2-die-sechs-begriffe)
3. [Warum es zwei „Delivery“-Konzepte gab](#3-warum-es-zwei-delivery-konzepte-gab)
4. [Datenmodell im Detail](#4-datenmodell-im-detail)
5. [Wie eine Route entsteht](#5-wie-eine-route-entsteht)
6. [Die Regler und ihre Wirkung](#6-die-regler-und-ihre-wirkung)
6.1 [Fahrer-Navigation](#61-fahrer-navigation) · 7. [Endpunkte](#7-endpunkte)
8. [Rechte](#8-rechte)
9. [Bekannte Grenzen](#9-bekannte-grenzen)

---

## 1. Was das System tut

Die Anwendung plant Sammeltouren für Glascontainer. Sie beantwortet die Frage:

> *Welche Container soll dieses Fahrzeug heute in welcher Reihenfolge anfahren, und wann muss es
> zwischendurch wohin entleeren?*

Zwei Eigenschaften unterscheiden das von gewöhnlicher Tourenplanung:

- **Die Zielgröße ist CO2e, nicht Fahrzeit.** Die Bogenkosten des Optimierers sind Emissionen. Die
  Schichtdauer ist eine *Nebenbedingung*, keine Zielgröße.
- **Die Abladestelle ist eine Entscheidung, keine Vorgabe.** Das Fahrzeug muss mehrfach je Schicht
  entleeren und darf dazu jede geeignete Abladestelle wählen. Der Optimierer entscheidet je Ladung.

Die zweite Eigenschaft ist der Grund für den Wechsel des Optimierers von VROOM zu Google OR-Tools:
VROOM kennt keine Alternativstandorte für Aufgaben. Fachlich ist das Problem ein
*Capacitated Vehicle Routing Problem with Intermediate Facilities*.

### Technischer Aufbau

| Baustein | Rolle |
|---|---|
| .NET 8 / ASP.NET Core | Web-API, JWT-Authentifizierung |
| MySQL (Pomelo EF Core 8) | Persistenz |
| **Valhalla** (selbst gehostet) | Fahrzeit- **und** Entfernungsmatrix mit LKW-Kostenmodell, Geometrie |
| **Google OR-Tools** | Lösung des Tourenproblems |

Beide Routing-Bausteine sind kostenfrei und laufen lokal. Es gibt keine externe Abhängigkeit zu
einem kommerziellen Kartendienst.

---

## 2. Die sechs Begriffe

Die Klassennamen sind teils historisch. Diese Tabelle ist die Übersetzung zwischen Code und Fachlichkeit.

| Code | Tabelle | Fachlich | Beispiel |
|---|---|---|---|
| `Job` | `Jobs` | **Sammelpunkt** | Glasplatz Gagarinstraße, Container Weißglas |
| `DeliveryDestination` | `DeliveryDestinations` | **Abladestelle** | Glaswerk, Umschlagplatz Langenberg |
| `Skill` | `Skills` | **Fraktion** | Weißglas, Grünglas, Braunglas |
| `Vehicle` | `Vehicles` | **Fahrzeug** | 26-t-Sammelfahrzeug |
| `Route` | `Routes` | **berechnete Tour** | „Gera Nord, Dienstag“ |
| `FrequencyRoute` | `FrequencyRoutes` | **Tourenvorlage** | „jeden 2. Montag“ |
| `Step` | `Steps` | **Halt einer Tour** | Ankunft 08:14, Sammelpunkt #412 |

> **Warum heißt ein Sammelpunkt „Job“?**
> Das ist VROOM-Vokabular aus der ersten Generation. Der Name wurde bewusst **nicht** umbenannt: Er
> steht in jedem Endpunktpfad (`/api/Jobs`), in der Frontend-Anbindung und in den externen
> Kennungen. Der Nutzen einer Umbenennung wiegt die Kosten nicht auf. Wo der Name in der
> Dokumentation auftaucht, ist „Sammelpunkt“ gemeint.

---

## 3. Warum es zwei „Delivery“-Konzepte gab

Dies war die größte Verständnishürde des Projekts und ist vollständig aufgelöst. Der Abschnitt
bleibt, weil die Frage bei jedem Blick in ältere Datenbestände oder in `git log` erneut auftaucht.

### Der Zustand vor der Bereinigung

Es existierten **zwei** Tabellen für Ablieferungsorte:

| | `Deliveries` (Klasse `DeliveryJob`) | `DeliveryDestinations` (Klasse `DeliveryDestination`) |
|---|---|---|
| Angelegt | 2024-06, erste Migration | 2025-12 |
| Bedeutung | fester Ablieferungsort **je Sammelpunkt** | Abladestelle als **gemeinsame Ressource** |
| Erreichbar über | `Job.DeliveryDestination` | `/api/DeliveryDestinations` |
| **Wirkung auf die Planung** | **keine** | maßgeblich |

Und der eigentliche Fehler:

```csharp
// Models/Jobs/Job.cs — vor der Bereinigung
public virtual DeliveryJob? DeliveryDestination { get; set; }
//                ^^^^^^^^^^^  ^^^^^^^^^^^^^^^^^^^
//                der TYP      der FELDNAME
```

Das Feld hieß `DeliveryDestination`, war aber vom Typ `DeliveryJob`. Wer
`GET /api/Jobs/1` aufrief, erhielt ein Feld `deliveryDestination`, das **keine**
`DeliveryDestination` war. Beide Namen für zwei verschiedene Dinge, gegenseitig vertauscht.

### Warum es den festen Ablieferungsort je Punkt gab

Er war eine Notlösung. Solange VROOM der Optimierer war, konnte das System keine Auswahl zwischen
Abladestellen abbilden. Also trug jeder Sammelpunkt sein Ziel selbst — 1200 Container mit 1200 mal
derselben Koordinate.

Mit OR-Tools ist diese Notlösung nicht nur unnötig, sondern **schädlich**: Eine feste Zuordnung
verhindert genau die Optimierung, die Kilometer spart.

### Was zusätzlich passierte

`PUT /api/Jobs/{id}` erzeugte bei jedem Aufruf eine **neue** Zeile in `Deliveries` und hängte den
Fremdschlüssel um. Die alte Zeile blieb liegen; einen Löschweg gab es nicht — es existierte kein
Controller für `Deliveries`. Der nächtliche Import aktualisiert jeden Container jede Nacht. Das
ergab eine verwaiste Zeile pro Container pro Nacht, dauerhaft.

### Der heutige Zustand

- `DeliveryJob`, `Deliveries` und `Job.DeliveryDestination` sind **entfernt**.
- Es gibt genau **ein** Ablieferungskonzept: `DeliveryDestination`.
- Ein Halt vom Typ `Disposal` verweist über `Step.DeliveryDestinationId` auf die tatsächlich
  genutzte Abladestelle — dort steht die Information, die vorher niemand hatte.

---

## 4. Datenmodell im Detail

```
                    ┌──────────────┐
                    │    Skill     │  Fraktion (Weißglas, Grünglas, ...)
                    └──────┬───────┘
        ┌──────────┬───────┼────────┬──────────────┐
        │ n:m      │ n:m   │ 1:n    │ n:m          │ n:m
   ┌────▼────┐ ┌───▼───┐ ┌─▼──┐ ┌───▼──────────┐ ┌─▼──────────────┐
   │ Vehicle │ │ Route │ │Job │ │FrequencyRoute│ │DeliveryDestination│
   └────┬────┘ └───┬───┘ └─┬──┘ └──────────────┘ └─────┬──────────┘
        │ 1:n      │ n:m   │                            │
        └──────────┤◀──────┘                            │
                   │ 1:n                                │
              ┌────▼────┐  0..1                         │
              │  Step   │──────────────────────────────▶┘
              └─────────┘   (Disposal-Halt)
```

### `Job` — Sammelpunkt

| Feld | Typ | Bedeutung |
|---|---|---|
| `Name` | string(256) | Anzeigename |
| `Description` | string(512)? | üblicherweise Adresse + Zusatz |
| `ExternalReference` | string(512)? | **Kennung im Fremdsystem — der Abgleichsschlüssel des Imports.** Indexiert. |
| `Capacity` | double | Sammelmenge in Kapazitätseinheiten |
| `Latitude` / `Longitude` | double | Standort |
| `Priority` | int 0–100 | Nutzen des Anfahrens — **siehe [Regler](#6-die-regler-und-ihre-wirkung), der Bereich 0–25 ist wirkungsgleich** |
| `Skill` | `Skill?` | erforderliche Fraktion — **harter Filter** |
| `ServiceTimeSeconds` | int? | Bedienzeit; `null` = globale Vorgabe (420 s) |

**Entfernt:** `AllowManualAssignment`, `DeliveryDestination`. Siehe unten und [§3](#3-warum-es-zwei-delivery-konzepte-gab).

> #### Das Rätsel `AllowManualAssignment`
>
> Dieses Feld hatte **vier** Bedeutungen und **keine** Wirkung:
>
> | Quelle | Behauptete Bedeutung |
> |---|---|
> | Migrationsname `20240829085848_ManualPriorityFieldJob` | manuelle **Priorität** |
> | Feldbeschreibung im Modell | Berechtigung der **Fahrer-Oberfläche** |
> | tatsächlich gefüllter Wert | Container hat **keinen Füllstandssensor** |
> | Feldname | manuelle **Zuordnung** |
>
> Es wurde an genau einer Stelle geschrieben (`UpdateJob`) und an genau einer Stelle gelesen (in das
> Antwort-DTO). Kein Planungsschritt hat es je ausgewertet. Zudem wurde es in `CreateJob`
> überhaupt nicht gesetzt — ein `POST` mit `allowManualAssignment: true` speicherte `false`.
>
> Das Feld ist **entfernt**. Wird die Information „kein Sensor vorhanden“ benötigt, ist sie als
> `HasNoFillLevelSensor` neu einzuführen — mit einem Namen, der sie beschreibt.

### `DeliveryDestination` — Abladestelle

| Feld | Typ | Bedeutung |
|---|---|---|
| `MaximumCapacity` | double | Aufnahmefähigkeit; **≤ 0 bedeutet unbegrenzt**. Begrenzt die Zahl der Ladungen je Schicht. |
| `Skills` | n:m | angenommene Fraktionen — **leere Liste = nimmt alles an** |
| `ServiceTimeSeconds` | int? | feste Bedienzeit einer Entleerung; `null` = 900 s |
| `ServiceSecondsPerUnit` | double? | zusätzliche Zeit je abgeladener Einheit |

> **Behobener Modellfehler.** `Skills` war eine **1:n**-Beziehung mit dem Fremdschlüssel auf der
> Fraktion (`Skills.DeliveryDestinationId`), weil die Gegen-Navigation fehlte und EF Core daraus
> eine 1:n-Beziehung ableitete. Folgen:
>
> - Eine Fraktion konnte nur an **einer** Abladestelle angenommen werden.
> - „Weißglas“ Standort B zuzuordnen entfernte es stillschweigend von Standort A.
> - `PUT` auf eine Abladestelle löste die Zuordnung **global**.
>
> Der Fehler war in keiner API-Antwort sichtbar. Weil der Planer Abladestellen ohne Fraktion als
> universell nutzbar behandelt, sahen die Ergebnisse plausibel aus — sie waren nur falsch.
> Abgesichert durch `DomainModelShapeTests`.

### `Vehicle` — Fahrzeug

| Feld | Typ | Bedeutung |
|---|---|---|
| `Capacity` | **double** | Nutzlast in Kapazitätseinheiten |
| `Skills` | n:m | Fraktionen, die es fahren darf |

> War `long`, während `Job.Capacity` `double` ist. Zwei Zahlentypen für dieselbe physikalische
> Größe; 12,5 Einheiten waren nicht darstellbar. Jetzt beides `double`.

### Die Kapazitätseinheit

Die Einheit ist **systemweit frei gewählt, aber einheitlich**. `Job.Capacity`,
`Vehicle.Capacity` und `DeliveryDestination.MaximumCapacity` verwenden denselben Maßstab.

Die **einzige** Stelle, an der die Einheit eine physikalische Bedeutung erhält, ist
`Routing:Emissions:PayloadUnitKilograms` — die Umrechnung in Kilogramm für die CO2e-Bilanz.

> **Empfehlung: Kubikmeter.** Sammelfahrzeuge für Altglas sind volumen- und nicht
> gewichtsbegrenzt, und Füllstandssensoren messen ein Volumen. Wird in Kubikmetern gearbeitet, ist
> `PayloadUnitKilograms` auf die Schüttdichte von Altglas zu setzen (Größenordnung 300 kg/m³).
> Bleibt der Wert bei 1,0, rechnet die Massenbilanz mit 1 kg je m³ und der lastabhängige Anteil der
> CO2e-Rechnung ist praktisch wirkungslos — die Bilanz besteht dann nur aus dem Grundverbrauch.
> Diese Einstellung ist vor jeder Veröffentlichung von CO2-Kennzahlen zu klären.

### `Route` — berechnete Tour

| Feld | Typ | Bedeutung |
|---|---|---|
| `StartDate` / `EndDate` | DateTimeOffset | **Schichtfenster.** Harte Nebenbedingung. |
| `StartLatitude/-Longitude`, `EndLatitude/-Longitude` | double | Betriebshof am Anfang und Ende |
| `Vehicle`, `Skills` | | bestimmen, welche Sammelpunkte in Frage kommen |
| `FillShift` | bool | **der wichtigste Schalter** — siehe [Regler](#6-die-regler-und-ihre-wirkung) |
| `Jobs` | n:m | bediente Sammelpunkte, **ungeordnete Menge** |
| `Steps` | 1:n | **die Fahrtreihenfolge — maßgeblich** |
| `Geometry` | string? | Encoded Polyline, **Präzision 1e-6** (polyline6) |
| `Meters`, `Duration`, `Service` | | gefahrene Strecke, reine Fahrzeit, Summe der Bedienzeiten |
| `Co2eGrams`, `CollectedUnits`, `EmissionFactorSource` | | die Bilanz samt Herkunft der Faktoren |

**Entfernt:** `JobsOrderIdList` (die Reihenfolge wird aus `Steps` abgeleitet), `WaitingTime` (immer null).

> **Warum `JobsOrderIdList` entfiel.** Dieselbe Information lag dreifach vor: als Menge in `Jobs`,
> als Reihenfolge in `JobsOrderIdList` (JSON in einer `longtext`-Spalte) und als Folge in `Steps`.
> Nichts stellte sicher, dass die drei übereinstimmen. Bei einer abgebrochenen Neuberechnung
> konnten sie auseinanderlaufen, und welche Quelle dann die Wahrheit war, ließ sich nicht mehr
> feststellen. `jobOrder` bleibt im Antwortvertrag, wird aber aus `Steps` berechnet.

### `Step` — Halt einer Tour

| Feld | Typ | Bedeutung |
|---|---|---|
| `Sequence` | int | **Position in der Fahrtreihenfolge**, ab 0. Eindeutig je Route. |
| `StepType` | enum | `Start` \| `Collection` \| `Disposal` \| `End` |
| `JobId` | int? | der bediente Sammelpunkt — nur bei `Collection` |
| `DeliveryDestinationId` | int? | die genutzte Abladestelle — nur bei `Disposal` |
| `Arrival` | DateTimeOffset | geplante Ankunft |
| `TravelTimeFromStart` | TimeSpan | **kumulierte Fahrzeit** ab Schichtstart, ohne Bedienzeiten |
| `ServiceTime` | TimeSpan | Bedienzeit an diesem Halt |
| `DistanceFromStartMeters` | int | **kumulierte Strecke** ab Schichtstart |
| `LoadUnitsAfter` | double | Ladung **nach** diesem Halt, echte Einheiten. An einer Abladestelle 0. |

**Umbenannt:** `Duration` → `TravelTimeFromStart`, `Service` → `ServiceTime`,
`Distance` → `DistanceFromStartMeters`, `LoadJson` → `LoadUnitsAfter`.
**Entfernt:** `VroomId`, `WaitingTime`, `Violations`.

> **`Duration` war das gefährlichste Feld des Modells.** Der Name legt „Dauer dieses Halts“ nahe;
> gespeichert war die **kumulierte Fahrzeit seit Schichtbeginn**. Jede clientseitige Auswertung, die
> dem Namen glaubte, war still falsch.
>
> **`LoadJson`** war eine Zeichenkette mit einem JSON-Array, das immer genau ein Element enthielt.
> Ältere Datensätze enthielten darin um Faktor 1000 vergrößerte Werte, die im Frontend geteilt
> werden mussten. Jetzt ist es eine Zahl in echten Einheiten. Die Ganzzahl-Skalierung, die der
> Optimierer braucht, verlässt `OrToolsCollectionPlanner` nicht.

### Entfernte Tabellen

| Tabelle | Warum entfernt |
|---|---|
| `Deliveries` | fester Ablieferungsort je Punkt; ohne Wirkung, ohne Löschweg, verwaiste Zeilen ([§3](#3-warum-es-zwei-delivery-konzepte-gab)) |
| `Violations` | Nebenbedingungsverletzungen des VROOM-Modells. **Kein Codepfad hat je eine Zeile geschrieben**; die Liste war in jeder Antwort leer. |
| `Stats` | Kacheltabelle für das Dashboard. **Kein Schreibpfad im gesamten Projekt** — dauerhaft leer. `/api/Stats` ist entfallen. |

Ebenfalls entfallen sind die Aufzählungswerte `StepType.Job` und `StepType.Break`: Die Abbildung
vom Planer konnte sie nicht erzeugen.

---

## 5. Wie eine Route entsteht

```
POST /api/Routes  ──▶  202 Accepted { jobId }
                            │
                            ▼
                  RouteOptimizationQueue  (In-Memory-Channel)
                            │
                            ▼
                  RouteOptimizationWorker  (eigener DI-Scope je Auftrag)
                            │
        ┌───────────────────┼───────────────────┐
        ▼                   ▼                   ▼
  Kandidaten          Abladestellen        Valhalla-Matrix
  auswählen           laden                (Zeit + Strecke)
        │                   │                   │
        └───────────────────┴─────────┬─────────┘
                                      ▼
                             OR-Tools: CVRP-IF
                          Ziel = CO2e, Grenze = Schicht
                                      │
                                      ▼
                        Valhalla-Geometrie über die Halte
                                      │
                                      ▼
                     CO2e-Bilanz mit echtem Lastverlauf
                                      │
                                      ▼
                          Route + Steps speichern

GET /api/Routes/jobs/{jobId} ──▶ Status, am Ende die Route
```

**Die Berechnung ist asynchron.** Schreibende Routen-Endpunkte antworten mit `202 Accepted` und
einer Auftrags-Kennung; das Ergebnis wird über `GET /api/Routes/jobs/{jobId}` abgefragt, bis der
Status `Succeeded` oder `Failed` lautet. Grund: Ein Optimierungslauf dauert bis zu 40 Sekunden
(zweimal `SolverTimeSeconds`) und darf keinen Anfrage-Thread belegen.

> **Betriebliche Einschränkung:** Der Auftragsstatus liegt im Arbeitsspeicher des Prozesses. Ein
> Neustart verwirft laufende und abgeschlossene Aufträge; bei mehreren Instanzen findet ein Client
> seinen Auftrag nur auf der Instanz, die ihn angenommen hat. Für Mehr-Instanz-Betrieb ist ein
> gemeinsamer Speicher erforderlich.

### Welche Sammelpunkte in Frage kommen

Zwei Filter, beide in `RouteOptimizationService.LoadEligibleJobsAsync`:

1. **Fraktion.** `nutzbar = Route.Skills ∩ Vehicle.Skills`. Ein Sammelpunkt kommt nur in Frage, wenn
   seine Fraktion darin liegt.
   → **Ist die Schnittmenge leer, gibt es keine Kandidaten und die Berechnung schlägt fehl.** Das
   ist die häufigste Ursache für „Route not found or could not be calculated“.
2. **Doppelbelegung.** Sammelpunkte, die bereits in einer **zeitlich überlappenden** Route stehen,
   werden ausgeschlossen — unabhängig vom Fahrzeug.

Anschließend begrenzt `Routing:Planner:MaxContainersConsidered` (Vorgabe 400) auf die
höchstpriorisierten Punkte, um die Matrixgröße zu beschränken.

### Zwei Durchgänge

Der erste Durchgang entscheidet gleichzeitig **welche** Punkte bedient werden und **in welcher
Reihenfolge**. Bei mehreren hundert Kandidaten verbraucht die Auswahl den Großteil des Zeitbudgets.
Der zweite Durchgang (`RefineSelection`, Vorgabe an) optimiert die nun **feste** Menge erneut und
wird nur übernommen, wenn er messbar besser ist.

> Das erklärt eine frühere Beobachtung: Das Neuberechnen derselben Route lieferte regelmäßig ein
> besseres Ergebnis als die Erstberechnung — weil bei der Neuberechnung die Menge schon feststand.

---

## 6. Die Regler und ihre Wirkung

Alle Werte stehen im Abschnitt `Routing` der Konfiguration und werden beim Start geprüft
(`ValidateOnStart`). Es gibt keine im Code versteckten Zahlen.

### `Route.FillShift` — der wichtigste Schalter

| | `false` (Vorgabe) | `true` |
|---|---|---|
| Kriterium | Ein Punkt wird angefahren, wenn sein Nutzen die zusätzlich verursachten Emissionen übersteigt | So viele Punkte wie in die Schicht passen |
| Ergebnis | emissionsgünstigste Tour, ggf. kurze Schicht | ausgefüllte Schicht |
| Reihenfolge | emissionsoptimal | **ebenfalls emissionsoptimal** |

Der Schalter verändert **ausschließlich** die Straffunktion für ausgelassene Punkte, nicht das
Modell. „Schicht füllen“ ist damit keine Verschlechterung der Route, sondern eine betriebliche
Entscheidung.

### `Planner:CollectionValueGramsPerUnit` (3000)

Der Schattenpreis der Sammlung: Wert in Gramm CO2e, den das Einsammeln **einer Kapazitätseinheit**
rechtfertigt. Bei `FillShift = false` ist das der zentrale Effizienzregler.

- **höher** → mehr Punkte werden angefahren
- **niedriger** → nur noch dicht liegende Punkte

### `Job.Priority` und `Planner:MinimumPriorityFactor` (0,25)

```
priorityFactor = max(MinimumPriorityFactor, Priority / 100)
Straffunktion  = Menge × CollectionValueGramsPerUnit × priorityFactor
```

> **⚠ Wichtig für die Datenpflege:** Wegen der Untergrenze sind **alle Prioritäten von 0 bis 25
> wirkungsgleich.** Die Untergrenze ist beabsichtigt — ohne sie würden neu angelegte Standorte mit
> Priorität 0 nie bedient. Wer feiner abstufen will, muss den Bereich **oberhalb von 25** nutzen.
>
> Der liefernde Dienst sollte seine Dringlichkeit daher auf den Bereich 25–100 abbilden und nicht
> auf 0–100. Vorgehen und Formel in [EXTERNAL_IMPORTER.md](EXTERNAL_IMPORTER.md#6-priorität-zuweisen).

### Bedienzeiten — der Hebel gegen unnötige Entleerungen

| Einstellung | Vorgabe | Wirkung |
|---|---|---|
| `ServiceTimes:ContainerBaseSeconds` | 420 | Zeit je Sammelhalt |
| `ServiceTimes:FacilitySeconds` | 900 | Zeit je Entleerung |
| `Emissions:GramsPerStop` | 250 | CO2e je Sammelhalt |
| `Emissions:GramsPerDisposalStop` | 1500 | CO2e je Entleerung |

Je teurer eine Entleerung in Zeit **und** Emissionen ist, desto eher füllt der Optimierer das
Fahrzeug aus, statt einen zusätzlichen Halt einzuplanen. Ist der Wert zu niedrig, erscheint eine
Zwischenentleerung nahezu kostenlos.

Punktgenaue Angaben (`Job.ServiceTimeSeconds`, `DeliveryDestination.ServiceTimeSeconds`) haben
Vorrang vor den globalen Werten.

### `Planner:UsableShiftFraction` (0,9)

Anteil des Schichtfensters, der verplant werden darf. Der Rest bleibt als Rüstzeit- und Pausenpuffer
unverplant. Eine bewusste Betriebsreserve, keine Effizienzvorgabe.

### `Sovereign` — Sonderrechte („Hoheitlich-Modus“)

Erlaubt dem Router, Verkehrsregeln zu übergehen. Standardmäßig **aus**. `LegalBasis` ist
Pflichtangabe, sobald `Enabled` gesetzt ist — der Start wird sonst verweigert.

> `IgnoreRestrictions` umgeht auch **Gewichts- und Höhenbeschränkungen** und kann ein 26-t-Fahrzeug
> über eine 7,5-t-Brücke führen. Es erfordert eine zusätzliche ausdrückliche Bestätigung. Die
> sichere Variante ist `IgnoreNonVehicularRestrictions`.

### Emissionsfaktoren

Die mitgelieferten Werte sind **Startwerte in HBEFA-Größenordnung, kein Nachweis**. Sie müssen gegen
den eigenen Fuhrpark kalibriert werden. `FactorSource` wird je Route mitgespeichert, damit eine
Bilanz nachträglich prüfbar bleibt.

---

## 6.1 Fahrer-Navigation

Abschnitt `Routing:Navigation`. Client-Anleitung: [DRIVER_NAVIGATION.md](DRIVER_NAVIGATION.md).

### Ein Kostenmodell für Planung *und* Navigation

`ValhallaCostingProfile` ist die einzige Stelle, an der das Valhalla-Kostenmodell entsteht — genutzt
von der Matrixberechnung, der Geometrieberechnung **und** den Fahrhinweisen.

> **Warum das eine eigene Klasse ist.** Zuvor wurde das Modell privat in
> `ValhallaRoadNetworkService` aufgebaut. Mit der Fahrer-Navigation als zweitem Aufrufer wäre eine
> private Kopie die gefährlichste denkbare Lösung: Der Fahrer würde über Straßen geführt, die der
> Planer nie bewertet hat. Geplante und gefahrene Kilometer wären nicht mehr vergleichbar, und die
> Abweichungserkennung würde dauerhaft Fehlalarm auslösen, weil geplante Linie und angewiesene
> Strecke systematisch verschieden wären.

Der Unterschied zwischen beiden Aufrufen ist ausschließlich `directions_type`: die Planung nutzt
`none` (nur Geometrie und Kennzahlen), die Navigation `instructions` (Abbiegehinweise samt Sprache).

> **Ein Test nagelt zusätzlich die Schlüsselschreibweise fest** (`ValhallaCostingProfileTests`).
> Valhalla **ignoriert unbekannte Kostenoptionen stillschweigend**: Würde die Serialisierung
> `hgv_no_access_penalty` zu `hgvNoAccessPenalty` umformen, gäbe es keine Fehlermeldung und keinen
> fehlgeschlagenen Aufruf — die Routen wären nur nicht mehr die eines 26-Tonners und sähen weiterhin
> plausibel aus.

### `Navigation:ApplySovereignRights` (true)

Fahrhinweise verwenden dieselbe Kostenbasis wie die Planung, einschließlich Sonderrechten.

> **Eine Haftungsentscheidung des Betreibers.** Solange `Sovereign:Enabled` aus ist — der
> Auslieferungszustand —, hat der Schalter keine Wirkung. Wird der Hoheitlich-Modus eingeschaltet,
> weist die Navigation einen **Menschen** an, von Verkehrsregeln abzuweichen. Das ist von der reinen
> Wegekostenrechnung zu unterscheiden: Der Start wird daher verweigert, solange nicht
> `Navigation:AcknowledgeGuidanceAgainstTrafficRules` ausdrücklich gesetzt ist — dieselbe
> Governance-Regel wie bei `Sovereign:AcknowledgeSafetyRiskOfIgnoreRestrictions`.
>
> Alternative: `ApplySovereignRights: false` erteilt StVO-konforme Hinweise, die dann bewusst von der
> geplanten Strecke abweichen. Die Antwort weist den gewählten Zustand in
> `directions.sovereignRightsApplied` aus; die Fahreroberfläche muss ihn sichtbar kennzeichnen.

### `Navigation:OffRouteToleranceMeters` (35) und `OffRouteConfirmationFixes` (3)

Die Position wird senkrecht auf die **gespeicherte Geometrie der Route** projiziert; der Querabstand
ist die Messgröße. 35 m deckt getrennte Richtungsgeometrien und übliches Stadtrauschen ab, ohne eine
echte Abweichung in die Nebenstraße zu verschlucken.

> **Eine einzelne abweichende Ortung ist kein Beweis.** GPS springt in Häuserschluchten und an
> Tunnelausfahrten um mehrere Dutzend Meter. Erst nach `OffRouteConfirmationFixes`
> aufeinanderfolgenden Messungen ist eine Abweichung belegt. Der Wert wird dem Client in **jeder**
> Antwort mitgeteilt, damit die Entscheidungsregel serverseitig konfigurierbar bleibt und nicht in
> jeder App-Version erneut geraten wird.

### Erdmodell der Projektion

Je Streckenabschnitt wird eine lokale, längentreue Ebene an der **Mittelbreite dieses Abschnitts**
aufgespannt (WGS84-Reihenentwicklungen für Meter je Breiten- und Längengrad). Bei den bei polyline6
üblichen Abschnittslängen liegt der Fehler unter einem Zentimeter.

> Eine **einzelne** Bezugsbreite für die ganze Route wäre unzulässig: Über 50 km
> Nord-Süd-Ausdehnung schwankt `cos φ` um etwa 0,9 %, was sich in der Längensumme zu mehreren
> hundert Metern aufaddiert. Die zwei trigonometrischen Funktionen je Abschnitt fallen genau einmal
> beim Aufbau an; jede Projektion ist danach reine Arithmetik ohne Wurzel im inneren Durchlauf.

### Monotone Haltezuordnung

Die Lage der Halte auf der Geometrie wird **monoton** bestimmt: Die Suche beginnt je Halt beim
Abschnitt des Vorgängers.

> Eine Sammeltour befährt dieselbe Straße mehrfach — nach einer Zwischenentleerung führt der Weg
> regelmäßig über bereits gefahrene Abschnitte zurück. Ohne Monotonie fiele der Lotfußpunkt eines
> späteren Halts auf eine **frühere** Befahrung, und alle abgeleiteten Restdistanzen wären negativ.
> Ein Fehler, der in der Fahreroberfläche als „minus 3 Kilometer bis zum Ziel“ auffällt — aber erst
> im Feld.

---

## 7. Endpunkte

Alle Endpunkte außer `/api/Login` erfordern `Authorization: Bearer <token>`.
Fehlerantworten sind RFC-7807-`ProblemDetails`.

### Anmeldung

| | |
|---|---|
| `POST /api/Login` | `{username, password}` → `{token}`, gültig 12 Stunden |

### Stammdaten — klassisches CRUD, Listen **paginiert**

| Pfad | Besonderheit |
|---|---|
| `/api/Jobs` | zusätzlich `GET /api/Jobs/by-external-reference/{ref}` |
| `/api/DeliveryDestinations` | |
| `/api/Skills` | |
| `/api/Vehicles` | |
| `/api/RecurringRoutes` | Tourenvorlagen |

**Alle Listen-Endpunkte antworten mit einer Seiten-Hülle, nicht mit einem Array:**

```json
{
  "items": [ ... ],
  "page": 1, "pageSize": 50, "totalCount": 1183,
  "totalPages": 24, "hasNextPage": true, "hasPreviousPage": false
}
```

`?page=` ab 1, `?pageSize=` 1–200 (Vorgabe 50).

### Routen

| Endpunkt | Antwort | Zweck |
|---|---|---|
| `GET /api/Routes` | `200` Seite von Kurzfassungen | Liste; ohne Sammelpunkte und Halte. Filtert auf `EndDate >= gestern`. |
| `GET /api/Routes/{id}` | `200` vollständige Route | inkl. Sammelpunkte, Halte, Fahrzeug |
| `POST /api/Routes` | **`202`** + `{jobId}` | neue Route berechnen |
| `PATCH /api/Routes/{id}/jobs` | **`202`** | Punkte hinzufügen/entfernen, **speichernd** |
| `POST /api/Routes/{id}/preview` | **`202`** | dasselbe, **nicht speichernd** |
| `PUT /api/Routes/{id}/metadata` | **`202`** | Stammdaten ändern **und neu berechnen** |
| `GET /api/Routes/jobs/{jobId}` | `200` | Status/Ergebnis eines Auftrags |
| `DELETE /api/Routes/{id}` | `204` | |

> Ausdrücklich zugeordnete Punkte (`PATCH .../jobs`) gelten als **verbindlich**: Der Planer nimmt
> sie in jedem Fall auf, soweit die Schicht es hergibt, und meldet zurück, was nicht passte.

### Dienste

| | |
|---|---|
| `POST /api/Services/RecurringRoutes` | Erzeugt aus allen fälligen Tourenvorlagen die konkreten Routen für **morgen**. Für einen Zeitplaner gedacht. `200` alle erstellt, `207` teilweise. |

### Map-Matching

| | |
|---|---|
| `POST /api/MapMatching/match` | GPS-Spur → gesnappte Punkte |
| `POST /api/MapMatching/route` | GPS-Spur → gesnappte Route mit Kennzahlen |
| `GET /api/MapMatching/status` | Verfügbarkeit der Valhalla-Instanz |

> Eigenständige Funktion für den Abgleich **aufgezeichneter** GPS-Spuren gegen das gesamte
> Straßennetz; nicht Teil der Tourenplanung. Bis zur Bereinigung waren `[Authorize]` und die
> Rechteprüfung **auskommentiert** — bis zu 5000 Spurpunkte je Anfrage waren unauthentifiziert an die
> Routing-Infrastruktur weiterleitbar.

> **Abgrenzung zur Live-Navigation:** Map-Matching beantwortet „welche Straßen wurden befahren?“ und
> ist damit die Grundlage der nachträglichen Bewertung einer Tour. Es beantwortet **nicht** „ist
> dieses Fahrzeug noch auf Route X?“ — dafür ist der Lotfußpunkt auf die bereits berechnete
> Streckengeometrie exakt und ohne Netzaufruf verfügbar, siehe [§7.1](#71-fahrer-navigation-live-routing).

#### Behobener Ausfall: `speed_limit` ist polymorph

`POST /api/MapMatching/match` antwortete mit
`{"error":"Die Antwort der Valhalla-Instanz konnte nicht verarbeitet werden."}` (HTTP `502`),
obwohl der Abgleich erfolgreich war. Ursache war ein einziges Feld:

```cpp
// Valhalla, src/tyr/trace_serializer.cc
if (edge.speed_limit() == kUnlimitedSpeedLimit) {
  writer("speed_limit", "unlimited");           // ZEICHENKETTE
} else {
  writer("speed_limit", serialize_speed(...));  // ZAHL
}
```

`edge.speed_limit` ist je Kante **entweder Zahl oder Zeichenkette**. Das Wire-Modell band es auf
`double?`; sobald eine Spur eine Autobahn ohne Tempolimit berührte (`maxspeed=none`, in Deutschland
der Regelfall), warf `System.Text.Json` und die **gesamte** Antwort war verloren.

Behoben durch drei Maßnahmen:

| Maßnahme | Wirkung |
|---|---|
| Duldsame Leser für alle Zahlenfelder der Valhalla-Antworten | Ein unerwarteter Einzelwert ergibt `null`, nicht den Verlust der ganzen Antwort |
| `matchedEdge.speedLimitUnlimited` (**neu**, additiv) | „unbeschränkt“ ist nun von „unbekannt“ unterscheidbar — bisher war beides `null` |
| Antwort wird gepuffert, `JsonException.Path` protokolliert | Die Fehlermeldung nennt jetzt das schuldige Feld statt „konnte nicht verarbeitet werden“ |

Ebenfalls gehärtet: `matched_points[].edge_index` wird von Valhalla als `uint64` geschrieben;
einzelne Fassungen geben dort den Kennwert `kInvalidEdgeIndex` (2^64-1) aus, der in kein `int` passt.

Zusätzlich **neu und additiv**: `matchMatchResult.confidenceScore` — die von Meili bewertete Güte des
Abgleichs. Vor jeder Auswertung prüfen: Meili legt *jede* Spur auf Straßen, auch eine mit Lücken oder
zu grober Abtastung. Ohne diese Kennzahl ist ein guter Abgleich von einem plausibel aussehend
falschen nicht zu unterscheiden.

Abgesichert durch `ValhallaMapMatchingTests` — einschließlich eines Tests, der belegt, dass die
frühere strenge Bindung tatsächlich scheitert.

### Fahrer-Navigation (Live-Routing)

Siehe die vollständige Client-Anleitung in [DRIVER_NAVIGATION.md](DRIVER_NAVIGATION.md).

| Endpunkt | Ruft Valhalla? | Zweck |
|---|:---:|---|
| `GET /api/Navigation/routes/{id}/plan` | nein | Geometrie, Haltelagen auf der Geometrie, Schwellwerte |
| `POST /api/Navigation/routes/{id}/progress` | **nein** | Fortschritt, Abweichung, Restdistanz und -zeit |
| `POST /api/Navigation/routes/{id}/guidance` | ja | Abbiegehinweise von der IST-Position zum nächsten Halt |
| `POST /api/Navigation/directions` | ja | freie Fahrt (Betriebshof, Pause, Werkstatt) |

Diese Endpunkte schließen die Lücke zwischen der geplanten Tour und dem Fahrzeug. Bis dahin lieferte
die API eine fertige Reihenfolge samt einer zusammenhängenden Polyline — aber keine Antwort auf die
Fragen, die im Fahrerhaus entstehen: *Bin ich noch auf der Route? Wie weit ist es noch zu Halt 3? Wo
muss ich abbiegen? Und was gilt, wenn ich zur Pause fahre oder eine Straße gesperrt ist?*

### Konten

`/api/Accounts` — CRUD, Bulk-Operationen, Suche, Rechteverwaltung. Details in Swagger.

**Entfallen:**

| Endpunkt | Grund |
|---|---|
| `GET /api/Stats` | Die Tabelle hat keinen Schreibpfad; die Antwort war stets leer. |
| `POST /api/Accounts/resetPasswordRequest` | Nie umgesetzt (lieferte `501`). |
| `POST /api/Accounts/resetPasswordConfirm` | **Sicherheitslücke** — siehe [§9](#9-bekannte-grenzen). |

---

## 8. Rechte

Zwei unabhängige `[Flags]`-Aufzählungen, beide im JWT:

- **`ApplicationRights`** — fachliche Sichten: `ViewJobs`, `EditJobs`, `ViewRoutes`, `EditRoutes`,
  `ViewVehicles`, `EditVehicles`, `ViewSkills`, `EditSkills`, `ViewAdminPanel`, `EditAdminPanel`
- **`AdministrativeRights`** — Benutzerverwaltung: `CreateUsers`, `EditUsers`, `DeleteUsers`,
  `ViewUsers`, `ViewUserActivity`, `Administrate`

`All` und `Administrate` sind `int.MaxValue` und umfassen damit alles.

### Die Regel für Eingriffe in fremde Konten

> Der Aufrufer muss **jedes einzelne** Recht selbst besitzen, das das Zielkonto besitzt oder
> erhalten soll.

Umgesetzt in `SecurityService.CanActOnAdministrativeRights` und `CanGrantApplicationRights` als
Teilmengenprüfung `(gefordert & ~vorhanden) == 0`.

> **Warum das erwähnenswert ist.** Zuvor wurde `callerRights < targetRights` verglichen — ein
> Zahlenvergleich auf Bitfeldern. Bitfelder haben keine Ordnung: Ein Nutzer mit ausschließlich
> `DeleteUsers` (4) galt gegenüber einem Konto mit `CreateUsers | EditUsers` (3) als höherwertig,
> obwohl er keines der beiden Rechte hat. Neun Stellen waren betroffen. Abgesichert durch
> `PermissionSubsetTests`.

### Ratenbegrenzung

Drei verkettete Fenster (Sekunde, Minute, Stunde); der strengste Wert gilt. Partitioniert nach
Benutzerkennung (angemeldet) bzw. IP (anonym). Lesende Listen-Endpunkte tragen
`[ForgivingRateLimit]` und erhalten größere Kontingente.

> **Betriebshinweis:** Hinter einem Reverse Proxy ist `ForwardedHeaders` zu konfigurieren, sonst
> teilen sich **alle** anonymen Aufrufer eine Partition (60 Anfragen/Stunde insgesamt). Siehe
> [OPERATIONS.md](OPERATIONS.md).

---

## 9. Bekannte Grenzen

Ehrliche Liste dessen, was noch offen ist.

| Thema | Zustand |
|---|---|
| **Emissionsfaktoren** | Startwerte, **nicht kalibriert**. `PayloadUnitKilograms = 1.0` bei Mengen in m³ ergibt eine physikalisch falsche Massenbilanz. Vor jeder Verwendung der CO2-Zahlen zu klären. |
| **Auftragsstatus** | Im Arbeitsspeicher. Kein Mehr-Instanz-Betrieb, kein Überleben eines Neustarts. |
| **Passwort-Zurücksetzung** | **Nicht vorhanden.** Der frühere anonyme Endpunkt setzte zu einer beliebigen E-Mail-Adresse ein neues Passwort — ohne Token, ohne Prüfung. Er ist entfernt; ein Passwort wird ausschließlich über `POST /api/Accounts/{id}/changePassword` gesetzt. |
| **Benutzer-Aktivitätsprotokoll** | Nicht vorhanden; der Endpunkt liefert `501`. Zuvor lieferte er eine leere Liste und behauptete damit „keine Aktivität“. |
| **Valhalla-Matrix** | Blöcke werden **streng nacheinander** abgefragt: ⌈N/B⌉² Aufrufe. Bei 400 Punkten und Blockgröße 50 sind das 64 Anfragen in Folge. |
| **Testabdeckung** | Routing-Kern, Rechteprüfung, Abbildung, Modellform, Map-Matching-Wire-Verträge, Kostenmodell und Navigations-Geodäsie sind abgedeckt (102 Tests). **Für die Controller gibt es keine Tests** — dafür wäre `Microsoft.AspNetCore.Mvc.Testing` erforderlich. |
| **Fahrzeugspur wird nicht gespeichert** | Die Navigations-Endpunkte sind zustandslos und schreiben nichts: keine Ist-Ankunftszeiten, keine gefahrene Spur, keine Ankunftsmeldung. Ein Soll-Ist-Vergleich oder eine Nachweisführung ist eigenständig zu spezifizieren. |
| **Keine Fahrspurführung** | Valhalla kann Spurhinweise (`turn_lanes`); sie werden nicht angefordert. Für Autobahnknoten mit LKW wäre das eine sinnvolle Erweiterung. |
| **Keine Verkehrslage** | Die Fahrzeiten sind die des Kostenmodells, nicht der aktuellen Verkehrslage. Restzeiten sind daher **geplante**, keine vorhergesagten Werte; sie werden innerhalb eines Abschnitts linear nach der Strecke interpoliert. |
| **Historische Ladungswerte** | Vor der Umstellung gespeicherte Halte konnten um Faktor 1000 vergrößerte Ladungen enthalten. Die Migration übernimmt die Werte unverändert; betroffene Routen sind neu zu berechnen. |
| **`Job`-Benennung** | Bleibt aus Kompatibilitätsgründen. Fachlich ist ein Sammelpunkt gemeint. |
