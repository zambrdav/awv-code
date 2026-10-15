# Datenanbindung und Mengenprognose

Spezifikation für den vorgelagerten Dienst, der Containerstammdaten, Sammelmengen und Prioritäten in
die SmartRouting API überträgt.

Stand 2026-07-31

---

## Inhalt

1. [Aufgabe des Dienstes](#1-aufgabe-des-dienstes)
2. [Grundlagen der Schnittstelle](#2-grundlagen-der-schnittstelle)
3. [Stammdaten abgleichen](#3-stammdaten-abgleichen)
4. [Abladestellen](#4-abladestellen)
5. [Menge zuweisen](#5-menge-zuweisen)
6. [Priorität zuweisen](#6-priorität-zuweisen)
7. [Das Prognosemodell](#7-das-prognosemodell)
8. [Bedienzeiten](#8-bedienzeiten)
9. [Nächtlicher Ablauf](#9-nächtlicher-ablauf)
10. [Referenzumsetzung](#10-referenzumsetzung)
11. [Betriebliche Erwartungen](#11-betriebliche-erwartungen)

---

## 1. Aufgabe des Dienstes

Die SmartRouting API plant Sammeltouren. Sie **berechnet** Routen, sie erfindet aber keine Daten:
Welche Container es gibt, wie viel dort zu erwarten ist und wie dringend ein Standort ist, muss ihr
mitgeteilt werden.

Genau das ist die Aufgabe dieses Dienstes. Er besitzt drei Verantwortlichkeiten:

| # | Verantwortung | API-Feld | Wirkung auf die Planung |
|---|---|---|---|
| 1 | **Bestand abgleichen** — welche Sammelpunkte existieren, wo, welche Fraktion | `name`, `latitude`, `longitude`, `skillId`, … | bestimmt die Menge der planbaren Punkte |
| 2 | **Menge zuweisen** — wie viel wird hier voraussichtlich aufgenommen | `capacity` | bestimmt, wann das Fahrzeug voll ist und entleeren muss |
| 3 | **Dringlichkeit zuweisen** — wie wertvoll ist das Anfahren | `priority` | bestimmt, welche Punkte bei knapper Schicht bedient werden |

Punkt 2 und 3 sind die eigentliche fachliche Leistung. Sie sind **Prognosen**, keine Messwerte — und
darin liegt das Ziel dieses Dienstes:

> **Zielbild.** Ein Sammelpunkt soll nicht danach bewertet werden, wie voll er *heute* ist, sondern
> wie voll er zum *Zeitpunkt der Abholung* sein wird. Ein Container mit 40 % Füllstand am 30.
> Dezember ist dringender als einer mit 65 %, der sich kaum verändert — weil an Neujahr das
> Dreifache einer normalen Woche anfällt. Ein scheinbar leerer Standort kann der wichtigste der Tour
> sein.

Die API ist darauf vorbereitet: `capacity` und `priority` sind je Sammelpunkt frei setzbar und
werden bei jedem Abgleich neu geschrieben. Der Optimierer verarbeitet, was er bekommt. Die Güte der
Touren hängt damit unmittelbar an der Güte dieser beiden Zahlen.

```
   ┌──────────────────┐      ┌─────────────────────┐      ┌────────────────────┐
   │  Sensordaten     │      │                     │      │                    │
   │  Stammdaten      ├─────▶│  Prognosedienst     ├─────▶│  SmartRouting API  │
   │  Kalender/Events │      │  (dieser Dienst)    │      │                    │
   └──────────────────┘      │                     │      │  capacity          │
                             │  Menge + Dringlich- │      │  priority          │
   ┌──────────────────┐      │  keit je Punkt      │      │        │           │
   │  Rückmeldung:    │◀─────┤                     │      │        ▼           │
   │  Touren, Zeiten  │      └─────────────────────┘      │   Optimierer       │
   └──────────────────┘                                   └────────────────────┘
```

---

## 2. Grundlagen der Schnittstelle

### Anmeldung

```http
POST /api/Login
Content-Type: application/json

{ "username": "...", "password": "..." }
```

```json
{ "token": "eyJ..." }
```

Das Token wird als `Authorization: Bearer <token>` an jede weitere Anfrage gehängt und ist zwölf
Stunden gültig. Benötigte Berechtigungen: `ViewJobs`, `EditJobs` sowie — falls der Dienst die
Routenerzeugung anstößt — `EditRoutes`.

### Listen sind paginiert

Alle Listen-Endpunkte liefern eine Seiten-Hülle, **kein** Array:

```json
{
  "items": [ ... ],
  "page": 1,
  "pageSize": 200,
  "totalCount": 1183,
  "totalPages": 6,
  "hasNextPage": true,
  "hasPreviousPage": false
}
```

`page` beginnt bei 1. `pageSize` liegt zwischen 1 und **200**, Vorgabe 50. Wer den Gesamtbestand
braucht, läuft über `hasNextPage`.

### Der Anfragekörper wird streng geprüft

Unbekannte Felder führen zu `400` mit einer Meldung, die das beanstandete Feld nennt. Für
schreibende Aufrufe ist daher genau der dokumentierte Anfragetyp zu senden — nicht ein
zurückgeschriebenes Antwortobjekt, das zusätzliche Felder wie `id` oder `skill` enthält.

Fehlerantworten folgen RFC 7807 (`application/problem+json`) und benennen bei Eingabefehlern das
betroffene Feld.

### Ratenbegrenzung

Die API begrenzt Anfragen pro Sekunde, Minute und Stunde. Wird ein Kontingent überschritten,
antwortet sie mit `429` und einem `Retry-After`-Kopf. Dieser Kopf ist auszuwerten: Er nennt die
tatsächlich erforderliche Wartezeit. Pauschale Wartezeiten zwischen den Datensätzen sind unnötig
und verlängern den Lauf ohne Nutzen.

### Die genutzten Endpunkte

| Zweck | Endpunkt |
|---|---|
| Fraktionen lesen | `GET /api/Skills` |
| Sammelpunkt über Fremdkennung finden | `GET /api/Jobs/by-external-reference/{ref}` |
| Gesamtbestand lesen | `GET /api/Jobs?page=&pageSize=200` |
| Sammelpunkt anlegen | `POST /api/Jobs` → `201` |
| Sammelpunkt aktualisieren | `PUT /api/Jobs/{id}` → `200` |
| Sammelpunkt entfernen | `DELETE /api/Jobs/{id}` → `204` |
| Abladestelle anlegen | `POST /api/DeliveryDestinations` → `201` |
| Fällige Routen erzeugen | `POST /api/Services/RecurringRoutes` → `200` / `207` |
| Tour mit Halten lesen | `GET /api/Routes/{id}` |

---

## 3. Stammdaten abgleichen

### Der Abgleichsschlüssel

Jeder Sammelpunkt trägt ein Feld `externalReference`. Darin steht die Kennung des Containers im
Quellsystem. Sie ist der **einzige** stabile Bezug zwischen beiden Welten: Die `id` der API wird
beim Anlegen vergeben und ist im Quellsystem unbekannt.

Für den Abgleich je Container steht ein eigener Endpunkt bereit:

```http
GET /api/Jobs/by-external-reference/GC-004711
    → 200  der Sammelpunkt
    → 404  existiert nicht
```

Das Feld ist indexiert; der Aufruf ist unabhängig von der Bestandsgröße. Damit ergibt sich je
Container ein Ablauf aus zwei Anfragen:

```
GET  /api/Jobs/by-external-reference/{ref}
  ├─ 404 ──▶ POST /api/Jobs            (anlegen)
  └─ 200 ──▶ PUT  /api/Jobs/{id}       (aktualisieren)
```

### Der Anfragekörper

```json
{
  "name": "Glasplatz Gagarinstraße",
  "description": "07545 Gera, Gagarinstraße Hinterhof",
  "externalReference": "GC-004711",
  "capacity": 0.94,
  "latitude": 50.8917,
  "longitude": 12.0839,
  "priority": 78,
  "skillId": 1,
  "serviceTimeSeconds": 540
}
```

| Feld | Pflicht | Anmerkung |
|---|---|---|
| `name` | ja | max. 256 Zeichen, wird dem Fahrer angezeigt |
| `description` | nein | max. 512 Zeichen; sinnvoll: Adresse und Zufahrtshinweis |
| `externalReference` | faktisch ja | max. 512 Zeichen; ohne diesen Wert ist kein Abgleich möglich |
| `capacity` | ja | erwartete Sammelmenge, siehe [§5](#5-menge-zuweisen) |
| `latitude` / `longitude` | ja | WGS84 |
| `priority` | ja | 0–100, siehe [§6](#6-priorität-zuweisen) |
| `skillId` | ja | bestehende Fraktions-ID, ≥ 1 |
| `serviceTimeSeconds` | nein | `null` übernimmt die globale Vorgabe, siehe [§8](#8-bedienzeiten) |

### Fraktionen

Fraktionen (Weißglas, Grünglas, Braunglas) sind eigenständige Stammdaten und werden **nicht** vom
Dienst angelegt. Er liest sie einmal je Lauf und bildet Name auf ID ab:

```http
GET /api/Skills?pageSize=200
```

Die Fraktion ist ein harter Filter: Ein Sammelpunkt kommt für eine Tour nur in Frage, wenn seine
Fraktion sowohl der Tour als auch dem Fahrzeug zugeordnet ist. Fehlt eine erwartete Fraktion in der
API, ist der Lauf abzubrechen, bevor der erste Sammelpunkt geschrieben wird — sonst entstehen
teilweise abgeglichene Bestände.

### Entfernte Container

Ein Container, der im Quellsystem verschwindet, bleibt in der API bestehen, bis er dort gelöscht
wird. Empfohlener Ablauf am Ende des Abgleichs:

1. Gesamtbestand über `GET /api/Jobs?pageSize=200` seitenweise lesen.
2. Sammelpunkte ermitteln, deren `externalReference` im Quellsystem nicht mehr vorkommt.
3. `DELETE /api/Jobs/{id}`.

Halte bereits gefahrener Touren bleiben dabei erhalten; ihr Verweis auf den Sammelpunkt wird auf
`null` gesetzt. Historische Touren und deren Bilanzen bleiben damit vollständig auswertbar.

---

## 4. Abladestellen

Eine **Abladestelle** ist der Ort, an dem das Fahrzeug entleert wird — Glaswerk, Umschlagplatz,
Betriebshof. Sie ist eine eigenständige, gemeinsam genutzte Ressource und **nicht** an einen
Sammelpunkt gebunden.

Das ist wesentlich für das Verständnis der Planung:

> Der Optimierer entscheidet **je Fahrzeugladung**, welche Abladestelle angefahren wird. Er darf
> innerhalb einer Schicht mehrfach und an unterschiedlichen Stellen entleeren. Ein Sammelpunkt sagt
> nichts darüber aus, wohin sein Inhalt gebracht wird.

Abladestellen ändern sich selten und werden üblicherweise einmalig eingerichtet:

```http
POST /api/DeliveryDestinations
```

```json
{
  "name": "Glaswerk Gera",
  "description": "Umschlagplatz Langenberg, Waage Tor 2",
  "externalReference": "GLASWERK-GERA",
  "latitude": 50.91627,
  "longitude": 12.04552,
  "maximumCapacity": -1,
  "skillsIdList": [],
  "serviceTimeSeconds": 900,
  "serviceSecondsPerUnit": null
}
```

| Feld | Bedeutung |
|---|---|
| `maximumCapacity` | Aufnahmefähigkeit in Kapazitätseinheiten. **Werte ≤ 0 bedeuten unbegrenzt.** Begrenzt sonst, wie viele Fahrzeugladungen die Stelle je Schicht annimmt. |
| `skillsIdList` | Angenommene Fraktionen. **Leere Liste bedeutet: nimmt alle an.** Nur füllen, wenn ein Standort tatsächlich eingeschränkt ist. |
| `serviceTimeSeconds` | Zeit für Anmelden, Wiegen, Kippen, Rangieren. Bestimmt maßgeblich, wie oft der Optimierer zwischenentleert. |
| `serviceSecondsPerUnit` | Zusätzliche Zeit je abgeladener Einheit. Erlaubt ein Modell wie „5 Minuten fest zuzüglich 6 Sekunden je m³“. |

Ohne mindestens eine Abladestelle kann nur **eine** Fahrzeugladung geplant werden: Die Tour endet,
sobald das Fahrzeug voll ist.

---

## 5. Menge zuweisen

`capacity` beschreibt, **wie viel an diesem Sammelpunkt voraussichtlich aufgenommen wird**.

Der Optimierer summiert diese Werte entlang der Tour und plant eine Entleerung ein, sobald die
Fahrzeugnutzlast erreicht ist. Die Menge steuert damit unmittelbar die Zahl und Lage der
Zwischenentleerungen — den teuersten Bestandteil einer Sammeltour.

### Die Kapazitätseinheit

Die Einheit ist **frei wählbar, aber systemweit einheitlich**. Derselbe Maßstab gilt für:

- `Job.capacity` — erwartete Sammelmenge je Punkt
- `Vehicle.capacity` — Nutzlast des Fahrzeugs
- `DeliveryDestination.maximumCapacity` — Aufnahmefähigkeit der Abladestelle

**Empfehlung: Kubikmeter.** Altglasfahrzeuge sind volumenbegrenzt, nicht gewichtsbegrenzt, und
Füllstandssensoren messen ein Volumen. Ein 3-m³-Iglu bei 80 % ergibt `capacity = 2.4`.

Es gibt genau eine Stelle, an der die Einheit eine physikalische Bedeutung erhält: Die API rechnet
sie über `Routing:Emissions:PayloadUnitKilograms` in Kilogramm um, um den lastabhängigen Anteil der
CO2e-Bilanz zu bestimmen. Wird in Kubikmetern gearbeitet, ist dort die Schüttdichte von Altglas
einzutragen (Größenordnung 300 kg/m³). Das ist eine **Betriebseinstellung der API**, keine Aufgabe
dieses Dienstes — sie muss aber zur gewählten Einheit passen, sonst ist die Emissionsbilanz nicht
belastbar.

### Von der Prognose zur Menge

```
capacity = erwarteter Füllstand zum Abholzeitpunkt (%) / 100 × Nennvolumen des Standorts (m³)
```

Das Nennvolumen ist die Summe der Behälter des Standorts derselben Fraktion. Ein Glasplatz mit zwei
Weißglas-Iglus zu je 2,5 m³ hat ein Nennvolumen von 5 m³; bei prognostizierten 62 % ergibt sich
`capacity = 3.1`.

> **Warum das mehr bringt als ein Pauschalwert.** Ein konstanter Wert für alle Punkte lässt die
> Ladungsrechnung raten. Der Optimierer plant dann Entleerungen an der falschen Stelle: entweder zu
> früh — das Fahrzeug fährt halbleer zum Werk — oder zu spät, sodass ein Punkt nicht mehr
> vollständig aufgenommen werden kann. Beides kostet Kilometer, die keine Reihenfolgeoptimierung
> zurückholen kann.

---

## 6. Priorität zuweisen

`priority` (0–100) drückt aus, **wie wertvoll das Anfahren dieses Punkts ist**. Der Optimierer wägt
diesen Wert gegen die zusätzlich verursachten Emissionen ab und entscheidet, ob sich der Umweg
lohnt. Bei knapper Schicht bestimmt die Priorität, welche Punkte entfallen.

### Der wirksame Wertebereich

Die API dämpft niedrige Prioritäten über einen Faktor:

```
priorityFactor = max(Routing:Planner:MinimumPriorityFactor, priority / 100)
```

Mit der Vorgabe `MinimumPriorityFactor = 0.25` folgt daraus:

> **Alle Prioritäten von 0 bis 25 sind wirkungsgleich.**

Die Untergrenze ist beabsichtigt: Sie verhindert, dass ein Standort dauerhaft nie bedient wird. Für
den Dienst bedeutet sie, dass der nutzbare Bereich **25 bis 100** ist. Eine Abbildung, die bei 0
beginnt, verschenkt ein Viertel ihrer Auflösung.

```csharp
/// <summary>Bildet eine Dringlichkeit von 0..1 auf den wirksamen Prioritätsbereich ab.</summary>
private static int ToPriority(double urgency)
    => 25 + (int)Math.Round(75.0 * Math.Clamp(urgency, 0.0, 1.0));
```

### Dringlichkeit ist eine Prognose

Der naheliegende Ansatz — `priority` = aktueller Füllstand — ist systematisch zu spät. Eine Tour
wird in der Nacht erzeugt und am Folgetag gefahren; die Entscheidung über die Reihenfolge fällt also
bis zu 36 Stunden vor der Abholung. Maßgeblich ist der Füllstand **zum Abholzeitpunkt**, nicht der
zum Zeitpunkt der Planung.

Zwei Größen bilden die Dringlichkeit:

| Größe | Bedeutung |
|---|---|
| **erwarteter Füllstand** | Wie voll ist der Standort voraussichtlich bei Ankunft? |
| **Überlaufwahrscheinlichkeit** | Wie wahrscheinlich läuft er über, wenn er heute *nicht* bedient wird? |

Die zweite Größe ist betrieblich oft die wichtigere: Ein überfüllter Standort mit Beistellungen
verursacht Beschwerden, Nacharbeit und Sonderfahrten. Ein Standort, der mit 70 % statt 95 % geleert
wird, kostet dagegen fast nichts.

```
urgency = max( erwarteterFüllstand , w × Überlaufwahrscheinlichkeit )
```

Mit `w` als betrieblichem Gewicht (Startwert 1,0). Die Verwendung des Maximums stellt sicher, dass
ein akuter Überlaufkandidat unabhängig von seinem Absolutfüllstand nach oben durchschlägt.

---

## 7. Das Prognosemodell

Dies ist der Kern des Dienstes.

### 7.1 Was vorhergesagt wird

Für jeden Sammelpunkt *i* und einen Zielzeitpunkt *t* (die geplante Abholung):

| Ausgabe | Verwendung |
|---|---|
| `ExpectedFillFraction` ∈ [0, 1] | erwarteter Füllstand bei Ankunft → `capacity` und `urgency` |
| `OverflowProbability` ∈ [0, 1] | P(Füllstand ≥ 100 % vor der nächsten Gelegenheit) → `urgency` |
| `ExpectedUnits` | erwartete Menge in Kapazitätseinheiten → `capacity` |

Alle drei folgen aus **einer** zugrunde liegenden Größe: der **Füllrate** eines Standorts, also dem
Zuwachs in Kapazitätseinheiten pro Tag. Die Rate ist die eigentliche Modellgröße; Füllstand und
Überlauf sind daraus abgeleitet.

### 7.2 Datengrundlage

| Quelle | Merkmale | Anmerkung |
|---|---|---|
| **Füllstandssensoren** | Zeitreihe je Container | die zentrale Quelle; verfügbar für einen Teil des Bestands |
| **Entleerungsereignisse** | starker Abfall der Zeitreihe | liefert Intervalle und die Füllung bei Abholung |
| **Kalender** | Wochentag, Monat, Feiertage, Schulferien, Brückentage | treibt die Saisonalität |
| **Ereignisse** | Silvester, Weihnachten, Ostern, Stadtfeste, Märkte | die eigentlichen Ausschläge |
| **Standortmerkmale** | Bebauungsdichte, Einwohner im Umfeld, Gastronomie in der Nähe, Fraktion, Behälterzahl | erklärt Unterschiede zwischen Standorten |
| **Wetter** (optional) | Temperatur, Niederschlag | schwacher, aber messbarer Einfluss im Sommer |
| **Touren der API** | `GET /api/Routes/{id}` → `steps[].arrival`, `steps[].jobId` | wann wurde geplant abgeholt |

### 7.3 Aufbau: Grundrate und Ereignisfaktoren

Ein zweischichtiges Modell ist hier dem einstufigen Regressionsansatz vorzuziehen, weil es die
beiden Fragen trennt, die unterschiedlich gut datenbelegt sind: *Wie schnell füllt sich dieser
Standort normalerweise?* und *Wie stark weicht die Woche vom Normalfall ab?*

**Schicht 1 — Grundrate je Standort.**
Aus den Entleerungsintervallen der Historie: Zuwachs geteilt durch Dauer, robust aggregiert
(Median über die letzten 8–12 Intervalle, damit einzelne Sondereffekte nicht dominieren). Ergebnis:
`baseRate_i` in Einheiten pro Tag, wahlweise wochentagsaufgelöst.

**Schicht 2 — Kalenderfaktor.**
Ein Multiplikator je Kalendertag, gemeinsam über alle Standorte einer Fraktion gelernt:

```
rate_i(d) = baseRate_i × f(d)
```

`f(d)` ist bei einem gewöhnlichen Werktag ≈ 1 und steigt zu Ereignissen deutlich an. Für Altglas
sind die Neujahrstage der stärkste Effekt des Jahres.

> Weil `f(d)` über den gesamten Bestand gelernt wird, gilt es auch für Standorte **ohne Sensor**.
> Das ist der wesentliche Hebel: Der Kalendereffekt lässt sich aus dem sensorbestückten Teil des
> Bestands ableiten und auf den unbestückten übertragen.

**Prognose des Füllstands.**

```
level_i(t) = level_i(t₀) + Σ           rate_i(d)
                          d ∈ (t₀, t]
```

mit `level_i(t₀)` als letztem bekannten Wert — bei Sensorstandorten die aktuelle Messung, sonst der
Stand nach der letzten Entleerung (also 0) fortgeschrieben.

### 7.4 Überlaufwahrscheinlichkeit

Die Punktprognose allein genügt nicht: Zwei Standorte mit identisch erwarteten 85 % sind
unterschiedlich dringend, wenn einer stark schwankt und der andere gleichmäßig zuläuft.

Aus der Streuung der historischen Raten (`σ_i`) ergibt sich unmittelbar:

```
OverflowProbability = P( level_i(t_next) ≥ 100 % )
```

wobei `t_next` die **nächste realistische Gelegenheit** ist, nicht der aktuelle Planungshorizont —
also der Zeitpunkt, an dem der Standort ohne heutige Berücksichtigung wieder anfahrbar wäre. Bei
angenommener Normalverteilung der Restmenge:

```
OverflowProbability = Φ( (level_i(t_next) − 100) / σ_i(t_next) )
```

Standorte mit wenig Historie erhalten ein größeres `σ`. Das ist erwünscht: Unsicherheit erhöht die
Dringlichkeit und führt dazu, dass unbekannte Standorte eher mitgenommen und dadurch beobachtbar
werden.

### 7.5 Standorte ohne Historie

Kein Standort darf ohne Prognose bleiben. Zwei Rückfallebenen, in dieser Reihenfolge:

1. **Gruppenmittel.** Median der Grundrate vergleichbarer Standorte — gleiche Fraktion, ähnliche
   Bebauungsdichte, ähnliche Behälterzahl, ähnliches Umfeld.
2. **Fraktionsmittel.** Median über die gesamte Fraktion.

Ein neu angelegter Standort erhält damit von Beginn an eine plausible Menge und Priorität und wird
über die ersten Entleerungen automatisch in Schicht 1 überführt.

### 7.6 Rückkopplung

Die Prognose ist nur so gut wie ihre Nachprüfung. Drei Rückflüsse stehen zur Verfügung:

| Signal | Quelle | Verwendung |
|---|---|---|
| **geplanter Abholzeitpunkt** | `GET /api/Routes/{id}` → `steps[].arrival` mit `steps[].jobId` | Zielzeitpunkt der Prognose; Grundlage des Vergleichs |
| **tatsächliche Füllung bei Abholung** | Sensorzeitreihe: Wert unmittelbar vor dem Abfall | die eigentliche Zielgröße für Schicht 1 |
| **tatsächlich abgeladene Menge** | Waage der Abladestelle je Tour | Kalibrierung der Summe über eine ganze Tour |

Damit ergibt sich ein geschlossener Kreis: prognostizieren → in `capacity`/`priority` schreiben →
Tour wird gefahren → Sensor und Waage liefern die Ist-Werte → Modell nachziehen.

> Optional lässt sich der **tatsächliche** Abholzeitpunkt über `POST /api/MapMatching/route`
> präzisieren: Der Endpunkt gleicht eine GPS-Spur des Fahrzeugs gegen das Straßennetz ab und liefert
> die tatsächlich befahrene Route. Das verbessert die zeitliche Zuordnung von Entleerungsereignissen
> gegenüber den geplanten Ankunftszeiten.

### 7.7 Bewertung

Kennzahlen, die den fachlichen Nutzen abbilden — nicht nur den Modellfehler:

| Kennzahl | Aussage |
|---|---|
| MAE des Füllstands zum Abholzeitpunkt (%-Punkte) | Grundgüte der Prognose |
| Anteil vermiedener Überläufe | der eigentliche betriebliche Nutzen |
| Anteil Abholungen unter 50 % Füllung | vermeidbare Leerfahrten |
| mittlere Füllung bei Abholung | steigt bei guter Prognose |
| Brier-Score der Überlaufwahrscheinlichkeit | Kalibrierung der Unsicherheit |
| Gramm CO2e je eingesammelte Einheit | `GET /api/Routes/{id}` → `emissions.gramsPerUnit`, die Gesamtwirkung |

Die letzte Kennzahl liefert die API selbst und ist die aussagekräftigste: Sie verbindet
Prognosegüte, Reihenfolgeoptimierung und Fahrzeugauslastung in einer Zahl.

### 7.8 Umsetzungsreihenfolge

Der Nutzen entsteht früh; das vollständige Modell ist nicht die Voraussetzung für den ersten
Fortschritt.

| Stufe | Inhalt | Erwarteter Effekt |
|---|---|---|
| **1** | `capacity` aus dem gemessenen Füllstand statt pauschal | Zwischenentleerungen liegen erstmals richtig |
| **2** | Grundrate je Standort, lineare Fortschreibung auf den Abholtag | behebt den systematischen Zeitverzug |
| **3** | Kalenderfaktor, auch für Standorte ohne Sensor | erfasst Neujahr und Feiertage; deckt den Gesamtbestand ab |
| **4** | Überlaufwahrscheinlichkeit aus der Streuung | priorisiert Risiko statt nur Füllstand |
| **5** | Standortmerkmale, Wetter, laufende Nachkalibrierung | Feinschliff |

Stufe 1 und 2 sind ohne maschinelles Lernen umsetzbar und liefern bereits den größten Teil des
Effekts.

---

## 8. Bedienzeiten

`serviceTimeSeconds` je Sammelpunkt ist optional; `null` übernimmt die globale Vorgabe von 420
Sekunden.

Eine punktgenaue Angabe ist die wirksamste Einzelverbesserung für realistische Schichtpläne. Die
Bedienzeit geht vollständig in die Schichtdauer ein — bei 60 Halten je Tour verschiebt eine Abweichung
von zwei Minuten je Halt den Plan um zwei Stunden.

Liegen im Quellsystem Merkmale des Standorts vor, lässt sich die Zeit ableiten:

```csharp
// Grundzeit für Anfahren und Positionieren, zuzüglich Zeit je Behälter,
// zuzüglich Zuschlag bei erschwerter Zufahrt.
int serviceTimeSeconds =
      180
    + (containerCount * 120)
    + (hasDifficultAccess ? 120 : 0);
```

Analog steuert `serviceTimeSeconds` der Abladestelle, wie oft zwischenentleert wird: Je aufwendiger
eine Entleerung, desto eher füllt der Optimierer das Fahrzeug vorher aus.

---

## 9. Nächtlicher Ablauf

```
1. Anmelden, Fraktionen lesen und prüfen
2. Quelldaten lesen: Container, Standorte, Sensorzeitreihen
3. Prognose je Sammelpunkt für den Abholzeitpunkt des Folgetags
4. Abgleich je Container:  suchen → anlegen oder aktualisieren
5. Entfallene Container entfernen
6. Fällige Touren erzeugen:  POST /api/Services/RecurringRoutes
7. Rückkopplung: gefahrene Touren des Vortags gegen die Ist-Werte prüfen
```

**Die Reihenfolge von Schritt 4 und 6 ist wesentlich.** `POST /api/Services/RecurringRoutes` erzeugt
die Touren für **morgen** aus den Tourenvorlagen und rechnet dabei mit den Mengen und Prioritäten,
die zu diesem Zeitpunkt in der API stehen. Touren auf Basis gestriger Prognosen sind wertlos.

Der Endpunkt antwortet:

| Status | Bedeutung |
|---|---|
| `200` | alle fälligen Touren erzeugt |
| `207` | nur ein Teil erzeugt — als Fehlschlag zu behandeln und zu melden |

Ein Optimierungslauf dauert bis zu 40 Sekunden je Tour. Der Zeitplan ist entsprechend zu legen.

---

## 10. Referenzumsetzung

Ausschnitte in C#. Die Aufteilung ist bewusst so gewählt, dass das Prognosemodell hinter einer
Schnittstelle liegt und unabhängig vom Abgleich entwickelt und getauscht werden kann.

### 10.1 Verträge

```csharp
namespace GlassCollection.Sync;

/// <summary>Ein Sammelpunkt, wie ihn die API annimmt.</summary>
/// <remarks>
/// Spiegelt den Anfragetyp der API genau. Die API weist unbekannte Felder mit HTTP 400 ab; es darf
/// daher kein Antwortobjekt zurückgeschrieben werden, sondern immer dieser Typ.
/// </remarks>
public sealed record CollectionPointPayload(
    string Name,
    string? Description,
    string? ExternalReference,
    double Capacity,
    double Latitude,
    double Longitude,
    int Priority,
    int SkillId,
    int? ServiceTimeSeconds);

/// <summary>Die Seiten-Hülle aller Listen-Endpunkte.</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    bool HasNextPage);

public sealed record JobView(int Id, string? ExternalReference);
public sealed record SkillView(int Id, string Name);

/// <summary>Ein Standort mit allem, was für die Prognose benötigt wird.</summary>
/// <param name="ExternalReference">Kennung im Quellsystem; der Abgleichsschlüssel.</param>
/// <param name="NominalVolumeCubicMetres">
/// Summe der Behälter dieses Standorts in derselben Fraktion. Bezugsgröße, auf die sich jeder
/// Füllstand in Prozent bezieht.
/// </param>
/// <param name="MeasuredFillFraction">
/// Letzter gemessener Füllstand als Anteil, oder <c>null</c> für Standorte ohne Sensor.
/// </param>
/// <param name="MeasuredAt">Zeitpunkt der Messung.</param>
public sealed record CollectionSite(
    string ExternalReference,
    string Name,
    string Location,
    string? AddressExtra,
    double Latitude,
    double Longitude,
    string FractionKey,
    double NominalVolumeCubicMetres,
    int ContainerCount,
    bool HasDifficultAccess,
    double? MeasuredFillFraction,
    DateTimeOffset? MeasuredAt);

/// <summary>Das Ergebnis der Prognose für einen Standort und einen Zielzeitpunkt.</summary>
/// <param name="ExpectedFillFraction">Erwarteter Füllstand bei Ankunft, 0..1 (Werte über 1 möglich).</param>
/// <param name="OverflowProbability">
/// Wahrscheinlichkeit, dass der Standort überläuft, wenn er heute nicht bedient wird.
/// </param>
/// <param name="ExpectedUnits">Erwartete Menge in Kapazitätseinheiten.</param>
/// <param name="Basis">
/// Herkunft der Prognose (eigene Historie, Gruppenmittel, Fraktionsmittel). Wird mitgeführt, damit
/// die Güte je Herkunft getrennt bewertbar bleibt.
/// </param>
public sealed record VolumeForecast(
    double ExpectedFillFraction,
    double OverflowProbability,
    double ExpectedUnits,
    ForecastBasis Basis);

public enum ForecastBasis
{
    /// <summary>Eigene Entleerungshistorie des Standorts.</summary>
    OwnHistory = 0,

    /// <summary>Median vergleichbarer Standorte.</summary>
    PeerGroup = 1,

    /// <summary>Median der Fraktion.</summary>
    FractionAverage = 2
}

/// <summary>Sagt Menge und Überlaufrisiko eines Standorts für einen Zeitpunkt vorher.</summary>
/// <remarks>
/// Die Naht zwischen Abgleich und Modell. Der Abgleich kennt ausschließlich diese Schnittstelle;
/// das Modell darf dahinter beliebig ausgetauscht werden, von der linearen Fortschreibung bis zum
/// gelernten Verfahren.
/// </remarks>
public interface IVolumeForecastModel
{
    /// <summary>Erstellt die Prognose für einen Standort.</summary>
    /// <param name="site">Der Standort samt letztem Messwert.</param>
    /// <param name="collectionTime">Der geplante Abholzeitpunkt.</param>
    /// <param name="nextOpportunity">
    /// Der Zeitpunkt, an dem der Standort erneut anfahrbar wäre, wenn er heute entfällt. Grundlage
    /// der Überlaufwahrscheinlichkeit.
    /// </param>
    VolumeForecast Predict(
        CollectionSite site,
        DateTimeOffset collectionTime,
        DateTimeOffset nextOpportunity);
}
```

### 10.2 Abbildung auf die API-Felder

```csharp
namespace GlassCollection.Sync;

/// <summary>Übersetzt eine Prognose in die beiden Steuergrößen der API.</summary>
public static class ForecastMapping
{
    /// <summary>
    /// Untergrenze des wirksamen Prioritätsbereichs.
    /// </summary>
    /// <remarks>
    /// Die API dämpft die Priorität mit
    /// <c>max(Routing:Planner:MinimumPriorityFactor, priority / 100)</c>. Bei der Vorgabe 0,25 sind
    /// alle Werte von 0 bis 25 wirkungsgleich; der nutzbare Bereich beginnt daher bei 25. Wird die
    /// Einstellung der API geändert, ist dieser Wert anzupassen.
    /// </remarks>
    private const int MinimumEffectivePriority = 25;

    /// <summary>Betriebliches Gewicht des Überlaufrisikos gegenüber dem reinen Füllstand.</summary>
    private const double OverflowWeight = 1.0;

    /// <summary>
    /// Erwartete Sammelmenge in Kapazitätseinheiten.
    /// </summary>
    /// <remarks>
    /// Auf das Nennvolumen begrenzt: Ein Standort kann nicht mehr abgeben, als er aufnimmt. Ein
    /// prognostizierter Füllstand über 100 % bedeutet Überlauf und wirkt über die Priorität, nicht
    /// über die Menge.
    /// </remarks>
    public static double ToCapacity(VolumeForecast forecast, CollectionSite site)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(site);

        double units = Math.Clamp(forecast.ExpectedFillFraction, 0.0, 1.0)
                       * site.NominalVolumeCubicMetres;

        // Der Optimierer benötigt eine von null verschiedene Menge, um den Punkt in der
        // Ladungsrechnung zu berücksichtigen.
        return Math.Round(Math.Max(0.01, units), 3);
    }

    /// <summary>
    /// Dringlichkeit als Priorität im wirksamen Bereich.
    /// </summary>
    /// <remarks>
    /// Das Maximum aus erwartetem Füllstand und gewichtetem Überlaufrisiko: Ein akuter
    /// Überlaufkandidat soll unabhängig von seinem Absolutfüllstand nach oben durchschlagen.
    /// </remarks>
    public static int ToPriority(VolumeForecast forecast)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        double urgency = Math.Max(
            forecast.ExpectedFillFraction,
            OverflowWeight * forecast.OverflowProbability);

        int span = 100 - MinimumEffectivePriority;
        return MinimumEffectivePriority
               + (int)Math.Round(span * Math.Clamp(urgency, 0.0, 1.0));
    }
}
```

### 10.3 Prognose der Stufen 1 bis 4

Eine vollständige Umsetzung ohne maschinelles Lernen. Sie deckt Grundrate, Kalenderfaktor,
Überlaufwahrscheinlichkeit und die Rückfallebenen ab und ist als Ausgangspunkt gedacht.

```csharp
namespace GlassCollection.Forecasting;

/// <summary>Statistik eines Standorts aus der Entleerungshistorie.</summary>
/// <param name="MedianDailyRate">Median des Zuwachses als Anteil des Nennvolumens pro Tag.</param>
/// <param name="RateStandardDeviation">Streuung derselben Größe.</param>
/// <param name="IntervalCount">Zahl der ausgewerteten Intervalle.</param>
public sealed record SiteRateStatistics(
    double MedianDailyRate,
    double RateStandardDeviation,
    int IntervalCount);

/// <summary>Liefert den Kalenderfaktor eines Tages für eine Fraktion.</summary>
/// <remarks>
/// Der Faktor ist gemeinsam über alle Standorte einer Fraktion gelernt und gilt damit auch für
/// Standorte ohne eigenen Sensor. Für Altglas ist der Jahreswechsel der stärkste Effekt.
/// </remarks>
public interface ICalendarFactorProvider
{
    /// <summary>Multiplikator für einen Tag; 1,0 entspricht einem gewöhnlichen Werktag.</summary>
    double GetFactor(DateOnly day, string fractionKey);
}

/// <summary>
/// Fortschreibung des Füllstands über Grundrate und Kalenderfaktor.
/// </summary>
/// <remarks>
/// <para>
/// Zwei Schichten: Die Grundrate beschreibt, wie schnell sich ein Standort normalerweise füllt; der
/// Kalenderfaktor beschreibt, wie stark ein Tag vom Normalfall abweicht. Die Trennung ist wichtig,
/// weil beide Größen unterschiedlich gut belegt sind — die Grundrate stammt aus wenigen Intervallen
/// eines Standorts, der Kalenderfaktor aus dem gesamten Bestand.
/// </para>
/// <para>
/// Die Überlaufwahrscheinlichkeit folgt aus der Streuung der Rate. Standorte mit wenig Historie
/// erhalten dadurch ein größeres Risiko zugewiesen — erwünscht, weil sie so eher bedient und
/// dadurch beobachtbar werden.
/// </para>
/// </remarks>
public sealed class RateBasedForecastModel : IVolumeForecastModel
{
    private readonly IReadOnlyDictionary<string, SiteRateStatistics> _siteStatistics;
    private readonly IReadOnlyDictionary<string, SiteRateStatistics> _peerGroupStatistics;
    private readonly IReadOnlyDictionary<string, SiteRateStatistics> _fractionStatistics;
    private readonly ICalendarFactorProvider _calendar;

    /// <summary>Mindestzahl an Intervallen, ab der die eigene Historie tragfähig ist.</summary>
    private const int MinimumIntervalsForOwnHistory = 3;

    /// <summary>
    /// Zuschlag auf die Streuung, wenn die Prognose nicht auf eigener Historie beruht.
    /// </summary>
    /// <remarks>
    /// Eine aus einer Vergleichsgruppe abgeleitete Rate ist unsicherer als eine gemessene. Ohne
    /// diesen Zuschlag würde das Modell für unbekannte Standorte eine Genauigkeit ausweisen, die es
    /// nicht hat.
    /// </remarks>
    private const double FallbackUncertaintyFactor = 1.5;

    public RateBasedForecastModel(
        IReadOnlyDictionary<string, SiteRateStatistics> siteStatistics,
        IReadOnlyDictionary<string, SiteRateStatistics> peerGroupStatistics,
        IReadOnlyDictionary<string, SiteRateStatistics> fractionStatistics,
        ICalendarFactorProvider calendar)
    {
        _siteStatistics = siteStatistics ?? throw new ArgumentNullException(nameof(siteStatistics));
        _peerGroupStatistics = peerGroupStatistics ?? throw new ArgumentNullException(nameof(peerGroupStatistics));
        _fractionStatistics = fractionStatistics ?? throw new ArgumentNullException(nameof(fractionStatistics));
        _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
    }

    /// <inheritdoc />
    public VolumeForecast Predict(
        CollectionSite site,
        DateTimeOffset collectionTime,
        DateTimeOffset nextOpportunity)
    {
        ArgumentNullException.ThrowIfNull(site);

        var (statistics, basis) = ResolveStatistics(site);

        double startFraction = site.MeasuredFillFraction ?? 0.0;
        DateTimeOffset startTime = site.MeasuredAt ?? collectionTime;

        double atCollection = startFraction
                              + AccumulateGrowth(site, statistics, startTime, collectionTime);

        double atNextOpportunity = startFraction
                                   + AccumulateGrowth(site, statistics, startTime, nextOpportunity);

        double sigma = EffectiveSigma(statistics, basis, startTime, nextOpportunity);

        return new VolumeForecast(
            ExpectedFillFraction: atCollection,
            OverflowProbability: OverflowProbability(atNextOpportunity, sigma),
            ExpectedUnits: Math.Clamp(atCollection, 0.0, 1.0) * site.NominalVolumeCubicMetres,
            Basis: basis);
    }

    /// <summary>Wählt die tragfähigste Statistik und meldet deren Herkunft.</summary>
    private (SiteRateStatistics Statistics, ForecastBasis Basis) ResolveStatistics(CollectionSite site)
    {
        if (_siteStatistics.TryGetValue(site.ExternalReference, out var own)
            && own.IntervalCount >= MinimumIntervalsForOwnHistory)
        {
            return (own, ForecastBasis.OwnHistory);
        }

        string peerKey = PeerGroupKey(site);
        if (_peerGroupStatistics.TryGetValue(peerKey, out var peer))
        {
            return (peer, ForecastBasis.PeerGroup);
        }

        if (_fractionStatistics.TryGetValue(site.FractionKey, out var fraction))
        {
            return (fraction, ForecastBasis.FractionAverage);
        }

        // Letzte Rückfallebene: eine Woche bis zur vollständigen Füllung. Bewusst konservativ, damit
        // ein Standort ohne jede Bezugsgröße nicht dauerhaft unbeachtet bleibt.
        return (new SiteRateStatistics(1.0 / 7.0, 0.05, 0), ForecastBasis.FractionAverage);
    }

    /// <summary>
    /// Bildet den Schlüssel der Vergleichsgruppe.
    /// </summary>
    /// <remarks>
    /// Fraktion und Behälterzahl sind die beiden Merkmale, die den Zulauf am deutlichsten erklären.
    /// Weitere Merkmale — Bebauungsdichte, Gastronomie im Umfeld — lassen sich hier ergänzen, sobald
    /// sie in ausreichender Qualität vorliegen.
    /// </remarks>
    private static string PeerGroupKey(CollectionSite site)
        => $"{site.FractionKey}|{Math.Min(site.ContainerCount, 4)}";

    /// <summary>Summiert den Zuwachs über alle Tage zwischen zwei Zeitpunkten.</summary>
    /// <remarks>
    /// Tagesweise statt als einfache Multiplikation mit der Dauer, weil der Kalenderfaktor je Tag
    /// unterschiedlich ist. Genau darin liegt der Nutzen: Ein Zeitraum, der über Silvester läuft,
    /// erhält den Ausschlag an der richtigen Stelle.
    /// </remarks>
    private double AccumulateGrowth(
        CollectionSite site,
        SiteRateStatistics statistics,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        if (to <= from)
        {
            return 0.0;
        }

        double growth = 0.0;
        DateTimeOffset cursor = from;

        while (cursor < to)
        {
            DateTimeOffset endOfDay = cursor.Date.AddDays(1);
            DateTimeOffset segmentEnd = endOfDay < to ? endOfDay : to;

            double dayFraction = (segmentEnd - cursor).TotalDays;
            double factor = _calendar.GetFactor(DateOnly.FromDateTime(cursor.Date), site.FractionKey);

            growth += statistics.MedianDailyRate * factor * dayFraction;
            cursor = segmentEnd;
        }

        return growth;
    }

    /// <summary>Streuung des prognostizierten Füllstands am Zielzeitpunkt.</summary>
    /// <remarks>
    /// Die Unsicherheit wächst mit der Wurzel der Zeit: Die täglichen Abweichungen sind näherungsweise
    /// unabhängig, ihre Varianzen addieren sich. Eine lineare Fortschreibung würde die Unsicherheit
    /// über längere Horizonte deutlich überschätzen.
    /// </remarks>
    private static double EffectiveSigma(
        SiteRateStatistics statistics,
        ForecastBasis basis,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        double days = Math.Max(0.0, (to - from).TotalDays);
        double sigma = statistics.RateStandardDeviation * Math.Sqrt(days);

        if (basis != ForecastBasis.OwnHistory)
        {
            sigma *= FallbackUncertaintyFactor;
        }

        // Untergrenze: Eine Streuung von null würde jede Wahrscheinlichkeit auf 0 oder 1 setzen.
        return Math.Max(0.01, sigma);
    }

    /// <summary>P(Füllstand ≥ 100 %) bei näherungsweise normalverteilter Restmenge.</summary>
    private static double OverflowProbability(double expectedFraction, double sigma)
        => NormalCdf((expectedFraction - 1.0) / sigma);

    /// <summary>Verteilungsfunktion der Standardnormalverteilung.</summary>
    /// <remarks>
    /// Über die Fehlerfunktion in der Näherung nach Abramowitz und Stegun (7.1.26); der Fehler liegt
    /// unter 1,5e-7 und ist für eine Priorisierung mehr als ausreichend.
    /// </remarks>
    private static double NormalCdf(double z)
    {
        double sign = z < 0 ? -1.0 : 1.0;
        double x = Math.Abs(z) / Math.Sqrt(2.0);

        const double A1 = 0.254829592;
        const double A2 = -0.284496736;
        const double A3 = 1.421413741;
        const double A4 = -1.453152027;
        const double A5 = 1.061405429;
        const double P = 0.3275911;

        double t = 1.0 / (1.0 + (P * x));
        double erf = 1.0 - (((((((((A5 * t) + A4) * t) + A3) * t) + A2) * t) + A1) * t
                            * Math.Exp(-x * x));

        return 0.5 * (1.0 + (sign * erf));
    }
}
```

### 10.4 API-Zugriff

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GlassCollection.Sync;

/// <summary>Zugriff auf die SmartRouting API.</summary>
/// <remarks>
/// Ein einziger <see cref="HttpClient"/> für die Laufzeit des Programms: Die Verbindungen werden
/// wiederverwendet, sodass auch mehrere tausend Aufrufe je Lauf keine Sockets erschöpfen.
/// </remarks>
public sealed class SmartRoutingClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxAttempts = 4;

    private readonly HttpClient _http;
    private readonly string _username;
    private readonly string _password;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public SmartRoutingClient(string baseUrl, string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _username = username;
        _password = password;
    }

    private sealed record LoginResponse(string Token);

    /// <summary>Liefert ein gültiges Token und erneuert es bei Bedarf.</summary>
    /// <remarks>
    /// Die Gültigkeit wird lokal verwaltet und eine Minute vor Ablauf erneuert. Das Tor verhindert,
    /// dass mehrere gleichzeitige Aufrufe dieselbe Erneuerung mehrfach ausführen.
    /// </remarks>
    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
        {
            return _token;
        }

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            {
                return _token;
            }

            using var response = await _http
                .PostAsJsonAsync("/api/Login", new { username = _username, password = _password }, Json, ct)
                .ConfigureAwait(false);

            await EnsureSuccessAsync(response, "Anmeldung", ct).ConfigureAwait(false);

            var payload = await response.Content
                .ReadFromJsonAsync<LoginResponse>(Json, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload?.Token))
            {
                throw new InvalidOperationException("Die Anmeldung lieferte kein Token.");
            }

            _token = payload!.Token;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddHours(12).AddMinutes(-1);
            return _token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>Führt eine Anfrage aus und wiederholt sie bei vorübergehenden Fehlern.</summary>
    /// <remarks>
    /// Wiederholt bei 5xx, 429 und Netzfehlern. Bei 429 wird die von der API im
    /// <c>Retry-After</c>-Kopf genannte Wartezeit verwendet — sie ist genauer als jede lokale
    /// Annahme. Ein 4xx außer 429 beschreibt einen Fehler in der Anfrage selbst und wird sofort
    /// gemeldet.
    /// </remarks>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var request = requestFactory();
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct).ConfigureAwait(false));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            bool transient = response.StatusCode == HttpStatusCode.TooManyRequests
                             || (int)response.StatusCode >= 500;

            if (!transient || attempt >= MaxAttempts)
            {
                return response;
            }

            TimeSpan wait = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
            response.Dispose();
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    /// <summary>Sucht einen Sammelpunkt über seine Fremdkennung.</summary>
    /// <returns>Der Sammelpunkt, oder <c>null</c>, wenn es keinen gibt.</returns>
    public async Task<JobView?> FindByExternalReferenceAsync(string externalReference, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalReference);

        using var response = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/Jobs/by-external-reference/{Uri.EscapeDataString(externalReference)}"),
            ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, $"Suche nach '{externalReference}'", ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<JobView>(Json, ct).ConfigureAwait(false);
    }

    /// <summary>Läuft über alle Seiten eines Listen-Endpunkts.</summary>
    /// <remarks>
    /// Listen-Endpunkte liefern eine Seiten-Hülle mit standardmäßig 50 Einträgen; das Maximum ist
    /// 200. Der Durchlauf endet, sobald <c>hasNextPage</c> nicht mehr gesetzt ist.
    /// </remarks>
    public async IAsyncEnumerable<T> GetAllPagesAsync<T>(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        const int PageSize = 200;

        for (int page = 1; ; page++)
        {
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"{path}?page={page}&pageSize={PageSize}"),
                ct).ConfigureAwait(false);

            await EnsureSuccessAsync(response, $"Seite {page} von {path}", ct).ConfigureAwait(false);

            var result = await response.Content
                .ReadFromJsonAsync<PagedResult<T>>(Json, ct).ConfigureAwait(false);

            if (result is null || result.Items.Count == 0)
            {
                yield break;
            }

            foreach (var item in result.Items)
            {
                yield return item;
            }

            if (!result.HasNextPage)
            {
                yield break;
            }
        }
    }

    /// <summary>Liest alle Fraktionen und bildet Name auf Kennung ab.</summary>
    public async Task<Dictionary<string, int>> GetSkillMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await foreach (var skill in GetAllPagesAsync<SkillView>("/api/Skills", ct).ConfigureAwait(false))
        {
            map[skill.Name] = skill.Id;
        }

        return map;
    }

    public async Task<int> CreateJobAsync(CollectionPointPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/api/Jobs")
            {
                Content = JsonContent.Create(payload, options: Json)
            }, ct).ConfigureAwait(false);

        await EnsureSuccessAsync(response, $"Anlegen von '{payload.ExternalReference}'", ct)
            .ConfigureAwait(false);

        var created = await response.Content.ReadFromJsonAsync<JobView>(Json, ct).ConfigureAwait(false);
        return created?.Id ?? 0;
    }

    public async Task UpdateJobAsync(int id, CollectionPointPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"/api/Jobs/{id}")
            {
                Content = JsonContent.Create(payload, options: Json)
            }, ct).ConfigureAwait(false);

        await EnsureSuccessAsync(response, $"Aktualisieren von '{payload.ExternalReference}'", ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteJobAsync(int id, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"/api/Jobs/{id}"), ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, $"Löschen von Sammelpunkt {id}", ct).ConfigureAwait(false);
    }

    /// <summary>Stößt die Erzeugung der fälligen Touren für den Folgetag an.</summary>
    /// <exception cref="InvalidOperationException">
    /// Auch bei <c>207</c>: Ein Teilerfolg bedeutet, dass mindestens eine fällige Tour fehlt.
    /// </exception>
    public async Task CreateRecurringRoutesAsync(CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/api/Services/RecurringRoutes"), ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.MultiStatus)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Nur ein Teil der fälligen Touren wurde erzeugt (207): {body}");
        }

        await EnsureSuccessAsync(response, "Erzeugen der fälligen Touren", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Wirft mit dem Antwortkörper, wenn die Anfrage nicht erfolgreich war.
    /// </summary>
    /// <remarks>
    /// Der Körper ist der eigentliche Erkenntnisgewinn: Bei einem Eingabefehler nennen die
    /// ProblemDetails der API das beanstandete Feld. Ohne diesen Text bleibt nur eine Statusnummer.
    /// </remarks>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"{what} fehlgeschlagen ({(int)response.StatusCode} {response.StatusCode}): {body}");
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenGate.Dispose();
    }
}
```

### 10.5 Abgleich

```csharp
namespace GlassCollection.Sync;

/// <summary>Ergebnis eines Abgleichslaufs.</summary>
public sealed record SyncOutcome(int Created, int Updated, int Removed, IReadOnlyList<string> Failures);

/// <summary>Überträgt Bestand und Prognose in die API.</summary>
public sealed class CollectionPointSynchroniser
{
    private readonly SmartRoutingClient _client;
    private readonly IVolumeForecastModel _forecast;
    private readonly IReadOnlyDictionary<string, string> _fractionNames;

    /// <param name="client">Zugriff auf die API.</param>
    /// <param name="forecast">Das Prognosemodell.</param>
    /// <param name="fractionNames">
    /// Abbildung des Fraktionsschlüssels des Quellsystems auf den Fraktionsnamen der API.
    /// </param>
    public CollectionPointSynchroniser(
        SmartRoutingClient client,
        IVolumeForecastModel forecast,
        IReadOnlyDictionary<string, string> fractionNames)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _forecast = forecast ?? throw new ArgumentNullException(nameof(forecast));
        _fractionNames = fractionNames ?? throw new ArgumentNullException(nameof(fractionNames));
    }

    /// <summary>Gleicht den Bestand ab und schreibt Menge und Priorität aus der Prognose.</summary>
    /// <param name="sites">Die Standorte des Quellsystems.</param>
    /// <param name="collectionTime">Geplanter Abholzeitpunkt, üblicherweise die Schicht des Folgetags.</param>
    /// <param name="nextOpportunity">Nächste Gelegenheit, falls ein Standort heute entfällt.</param>
    /// <param name="removeMissing">
    /// Ob Sammelpunkte entfernt werden, die im Quellsystem nicht mehr vorkommen.
    /// </param>
    /// <param name="ct">Abbruch-Token.</param>
    public async Task<SyncOutcome> SynchroniseAsync(
        IReadOnlyList<CollectionSite> sites,
        DateTimeOffset collectionTime,
        DateTimeOffset nextOpportunity,
        bool removeMissing,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sites);

        // Fraktionen vorab prüfen. Ein fehlender Eintrag würde sonst mitten im Lauf auffallen und
        // einen teilweise abgeglichenen Bestand hinterlassen.
        var skills = await _client.GetSkillMapAsync(ct).ConfigureAwait(false);

        foreach (string name in _fractionNames.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!skills.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"Die Fraktion '{name}' ist in der API nicht vorhanden. "
                    + "Sie muss unter /api/Skills angelegt werden, bevor abgeglichen werden kann.");
            }
        }

        int created = 0, updated = 0;
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var site in sites)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(site.ExternalReference);

            if (!_fractionNames.TryGetValue(site.FractionKey, out string? fractionName)
                || !skills.TryGetValue(fractionName, out int skillId))
            {
                failures.Add($"{site.ExternalReference}: unbekannte Fraktion '{site.FractionKey}'");
                continue;
            }

            var prediction = _forecast.Predict(site, collectionTime, nextOpportunity);

            var payload = new CollectionPointPayload(
                Name: Truncate(site.Name, 256),
                Description: Truncate($"{site.Location} {site.AddressExtra}".Trim(), 512),
                ExternalReference: site.ExternalReference,
                Capacity: ForecastMapping.ToCapacity(prediction, site),
                Latitude: site.Latitude,
                Longitude: site.Longitude,
                Priority: ForecastMapping.ToPriority(prediction),
                SkillId: skillId,
                ServiceTimeSeconds: EstimateServiceTime(site));

            try
            {
                var existing = await _client
                    .FindByExternalReferenceAsync(site.ExternalReference, ct).ConfigureAwait(false);

                if (existing is null)
                {
                    await _client.CreateJobAsync(payload, ct).ConfigureAwait(false);
                    created++;
                }
                else
                {
                    await _client.UpdateJobAsync(existing.Id, payload, ct).ConfigureAwait(false);
                    updated++;
                }
            }
            catch (InvalidOperationException ex)
            {
                // Ein einzelner Fehlschlag beendet den Lauf nicht, wird aber vollständig berichtet.
                failures.Add($"{site.ExternalReference}: {ex.Message}");
            }
        }

        int removed = removeMissing
            ? await RemoveMissingAsync(seen, failures, ct).ConfigureAwait(false)
            : 0;

        return new SyncOutcome(created, updated, removed, failures);
    }

    /// <summary>Entfernt Sammelpunkte, die im Quellsystem nicht mehr vorkommen.</summary>
    /// <remarks>
    /// Halte bereits gefahrener Touren bleiben erhalten; ihr Verweis wird auf <c>null</c> gesetzt.
    /// Historische Touren und deren Bilanzen bleiben damit auswertbar.
    /// </remarks>
    private async Task<int> RemoveMissingAsync(
        HashSet<string> seen, List<string> failures, CancellationToken ct)
    {
        var obsolete = new List<JobView>();

        await foreach (var job in _client.GetAllPagesAsync<JobView>("/api/Jobs", ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(job.ExternalReference) || !seen.Contains(job.ExternalReference))
            {
                obsolete.Add(job);
            }
        }

        int removed = 0;
        foreach (var job in obsolete)
        {
            try
            {
                await _client.DeleteJobAsync(job.Id, ct).ConfigureAwait(false);
                removed++;
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"Sammelpunkt {job.Id}: {ex.Message}");
            }
        }

        return removed;
    }

    /// <summary>Schätzt die Bedienzeit eines Standorts aus seinen Merkmalen.</summary>
    /// <remarks>
    /// Grundzeit für Anfahren und Positionieren, Zeit je Behälter, Zuschlag bei erschwerter Zufahrt.
    /// Die Werte sind Startwerte und gegen beobachtete Halte zu kalibrieren.
    /// </remarks>
    private static int EstimateServiceTime(CollectionSite site)
        => 180
           + (Math.Max(1, site.ContainerCount) * 120)
           + (site.HasDifficultAccess ? 120 : 0);

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
```

---

## 11. Betriebliche Erwartungen

### Rückgabewert

Der Rückgabewert des Prozesses ist die einzige Information, die ein Zeitplaner auswertet:

| Wert | Bedeutung |
|---|---|
| `0` | Abgleich vollständig, Touren erzeugt |
| `1` | fehlgeschlagen — Einzelheiten auf `stderr` |
| `130` | abgebrochen |

Ein Lauf mit Einzelfehlschlägen ist **kein** Erfolg. Die Touren des Folgetags würden dann auf einem
unvollständigen Bestand beruhen; die Routenerzeugung ist in diesem Fall zu überspringen und der Lauf
als fehlgeschlagen zu melden.

### Wiederholbarkeit

Der Abgleich ist idempotent: Ein zweiter Lauf mit denselben Quelldaten führt zu denselben Werten in
der API. Ein abgebrochener Lauf darf ohne Vorbereitung wiederholt werden.

### Was protokolliert werden sollte

| Größe | Zweck |
|---|---|
| angelegt / aktualisiert / entfernt | Nachvollziehbarkeit des Abgleichs |
| Verteilung der Prognoseherkunft (`ForecastBasis`) | zeigt, welcher Anteil des Bestands auf eigener Historie beruht |
| Verteilung der vergebenen Prioritäten | erkennt eine zusammengeschobene Abbildung, die nicht mehr unterscheidet |
| Summe der vergebenen `capacity` je Fraktion | plausibel gegen die Fahrzeugnutzlast: ergibt sich eine sinnvolle Zahl an Ladungen? |
| Prognose gegen Ist des Vortags | die eigentliche Güte, siehe [§7.7](#77-bewertung) |

Die letzten drei sind die wertvollsten. Eine Prognose, die niemand gegen die Wirklichkeit hält,
wird mit der Zeit schlechter, ohne dass es auffällt.
