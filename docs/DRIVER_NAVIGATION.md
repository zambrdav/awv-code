# Fahrer-Navigation — Anbindung eines JavaScript/MapLibre-Clients

Stand: 2026-09-01 · Gilt ab der Einführung von `/api/Navigation`

Dieses Dokument beschreibt, wie eine mobile Fahreranwendung (JavaScript + MapLibre GL) das Fahrzeug
auf einer geplanten Tour führt: Positionsbestimmung auf der Route, Erkennung einer Abweichung,
Abbiegehinweise zum nächsten Halt und der Umgang mit Pausen und Straßensperrungen.

Maßgeblich für den Vertrag ist Swagger: `GET /swagger/v1/swagger.json`.

---

## 1. Die Arbeitsteilung — und warum sie so ist

| | Server | Fahrer-Client |
|---|---|---|
| Geplante Reihenfolge der Halte | **hält sie** | zeigt sie an |
| Geometrie der Tour | **liefert sie** | rendert sie |
| Position bei jeder Ortung auf die Linie projizieren | kann es (maßgeblich) | **tut es, 1 Hz, örtlich** |
| „Wie viele abweichende Ortungen habe ich gesehen?“ | — | **führt diesen Zustand** |
| „Welchen Halt fahre ich an?“ | leitet ab | **führt diesen Zustand** |
| Abbiegehinweise | **berechnet sie** | spielt sie ab |

**Die API ist in dieser Funktion zustandslos.** Sie führt kein Fahrzeug, keine Sitzung und keinen
Fortschritt mit. Jede Anfrage enthält die Position, jede Antwort ist eine Messung mit Bewertung.

> Das ist keine Bequemlichkeit. Der Auftragsstatus der Routenoptimierung liegt bereits im
> Arbeitsspeicher des Prozesses und ist damit bei mehreren API-Instanzen nicht tragfähig — siehe
> [SYSTEM_REFERENCE.md §9](SYSTEM_REFERENCE.md#9-bekannte-grenzen). Diesen Fehler bei einer
> sicherheitsrelevanten Fahrerfunktion zu wiederholen, wäre nicht vertretbar.

---

## 2. Die vier Endpunkte

Alle erfordern `Authorization: Bearer <token>` und das Recht `ViewRoutes`.

| Endpunkt | Ruft Valhalla? | Zweck | Aufrufhäufigkeit |
|---|:---:|---|---|
| `GET /api/Navigation/routes/{id}/plan` | nein | Geometrie + Haltelagen + Schwellwerte | **einmal je Schicht** |
| `POST /api/Navigation/routes/{id}/progress` | **nein** | Fortschritt, Abweichung, Restdistanz/-zeit | bei Zustandsänderung, sonst ~alle 30 s |
| `POST /api/Navigation/routes/{id}/guidance` | ja | Abbiegehinweise zum nächsten Halt | bei Start eines Abschnitts und bei Abweichung |
| `POST /api/Navigation/directions` | ja | freie Fahrt (Betriebshof, Pause, Werkstatt) | nach Bedarf |

### Warum `progress` nicht im Sekundentakt aufgerufen wird

Ein Standard-Nutzer hat 2500 Anfragen pro Stunde (`[ForgivingRateLimit]`-Endpunkte 6000). Ein
Aufruf je Sekunde sind 3600 pro Stunde: Die Schicht würde nach etwa 100 Minuten in `429` laufen.

Es ist auch unnötig. Der Client hat nach Schritt 1 die vollständige Geometrie und rechnet die
Projektion örtlich — dieselbe Rechnung, dieselbe Toleranz, kein Netz. `progress` ist die
**maßgebliche Gegenprobe**, nicht die Messschleife.

---

## 3. Ablauf

```
 ┌─ einmal je Schicht ────────────────────────────────────────────────────────┐
 │ GET /api/Navigation/routes/88/plan                                         │
 │   → geometry (polyline6), stops[].distanceAlongRouteMeters,                │
 │     offRouteToleranceMeters, confirmationFixesRecommended,                 │
 │     arrivalRadiusMeters                                                    │
 │ Geometrie dekodieren, als MapLibre-Quelle registrieren                     │
 └────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
 ┌─ je Ortung (1 Hz), VOLLSTÄNDIG ÖRTLICH ────────────────────────────────────┐
 │ Lotfußpunkt auf die Geometrie  →  querabstand, distanceAlongRouteMeters    │
 │ Fahrzeugsymbol auf den Lotfußpunkt setzen (nicht auf die Rohposition)      │
 │ querabstand ≤ toleranz ?  ──ja──►  offRouteCount = 0                       │
 │                            └─nein─►  offRouteCount++                       │
 └────────────────────────────────────────────────────────────────────────────┘
                                    │
              offRouteCount ≥ confirmationFixesRecommended
                                    │
                                    ▼
 ┌─ Neuberechnung ────────────────────────────────────────────────────────────┐
 │ POST /api/Navigation/routes/88/guidance                                    │
 │   { position: {latitude, longitude, heading, accuracyMeters},              │
 │     targetStopSequence: 3,                                                 │
 │     excludeLocations: [ …alle in dieser Schicht gemeldeten Sperrungen… ] }  │
 │   → directions.legs[0].maneuvers[]  (Anweisungstexte, Sprachhinweise)      │
 │   → directions.legs[0].shape        (Geometrie ab der IST-Position)        │
 │   → progress                        (dieselbe Messung, maßgeblich)         │
 │ offRouteCount = 0                                                          │
 └────────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Örtliche Projektion im Client

Die Rechnung, die der Server in `progress` durchführt, ist im Client mit Turf.js in vier Zeilen
nachgebildet. **Wichtig: dieselbe Toleranz verwenden**, die `plan` geliefert hat — nicht eine eigene
Konstante.

```js
import { lineString, point } from '@turf/helpers';
import nearestPointOnLine from '@turf/nearest-point-on-line';
import length from '@turf/length';
import lineSliceAlong from '@turf/line-slice-along';
import polyline from '@mapbox/polyline';

// 1x je Schicht: polyline6 dekodieren. Achtung: precision 6, NICHT die Vorgabe 5.
const coords = polyline.decode(plan.geometry, 6).map(([lat, lon]) => [lon, lat]);
const route = lineString(coords);

function project(fix) {
  const snapped = nearestPointOnLine(route, point([fix.longitude, fix.latitude]), {
    units: 'meters',
  });

  return {
    lateralOffsetMeters: snapped.properties.dist,             // Querabstand
    distanceAlongRouteMeters: snapped.properties.location * 1000, // Turf liefert km
    snapped: snapped.geometry.coordinates,                    // [lon, lat]
  };
}
```

### Abweichungserkennung mit Bestätigung

```js
let offRouteCount = 0;

function onPositionFix(fix) {
  const p = project(fix);

  // Fahrzeugsymbol auf den Lotfußpunkt setzen, nicht auf die Rohposition:
  // sonst zappelt es sichtbar neben der Straße.
  vehicleMarker.setLngLat(p.snapped);

  // Rauschen gewichten: 40 m Querabstand bei 60 m gemeldeter Ortungsgenauigkeit
  // sind keine Abweichung.
  const noiseFloor = Math.max(plan.offRouteToleranceMeters, fix.accuracy ?? 0);

  if (p.lateralOffsetMeters <= noiseFloor) {
    offRouteCount = 0;
    return;
  }

  if (++offRouteCount >= plan.confirmationFixesRecommended) {
    offRouteCount = 0;
    requestGuidance(fix);   // → POST .../guidance
  }
}
```

> **Nie bei der ersten abweichenden Ortung neu berechnen.** GPS springt in Häuserschluchten, an
> Tunnelausfahrten und unter Brücken um mehrere Dutzend Meter. Ohne Bestätigung flackert die
> Navigation und die Routing-Infrastruktur trägt Last ohne Nutzen.

### Fortschritt zeichnen

`stops[].distanceAlongRouteMeters` erlaubt, den zurückgelegten Teil der Tour einzufärben und die
Reststrecke zum nächsten Halt ohne Serveraufruf anzuzeigen:

```js
const nextStop = plan.stops.find(
  (s) => s.type !== 'Start' && s.distanceAlongRouteMeters > p.distanceAlongRouteMeters + 5,
);

const remainingMeters = nextStop
  ? nextStop.distanceAlongRouteMeters - p.distanceAlongRouteMeters
  : 0;

// Verbleibender Streckenteil als eigene MapLibre-Quelle:
const ahead = lineSliceAlong(route, p.distanceAlongRouteMeters / 1000, length(route), {
  units: 'kilometers',
});
```

---

## 5. Abbiegehinweise abspielen

```js
async function requestGuidance(fix) {
  const response = await fetch(`/api/Navigation/routes/${routeId}/guidance`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify({
      position: {
        latitude: fix.latitude,
        longitude: fix.longitude,
        heading: fix.heading ?? null,          // siehe Warnung unten
        accuracyMeters: fix.accuracy ?? null,
      },
      targetStopSequence: activeStopSequence,   // den eigenen Zustand mitsenden
      excludeLocations: blockedPoints,          // alle Sperrungen dieser Schicht
    }),
  });

  if (response.status === 409) {
    // Kein Halt steht mehr aus – die Tour ist abgeschlossen.
    return finishShift();
  }

  const result = await response.json();
  legGeometry = polyline.decode(result.directions.legs[0].shape, 6);
  maneuvers = result.directions.legs[0].maneuvers;
  maneuverIndex = 0;
}
```

### `heading` — die wichtigste optionale Angabe

> **Ohne `heading` weist die erste Anweisung regelmäßig eine Wende an.** Valhalla ordnet die
> Position sonst ebenso wahrscheinlich der Gegenrichtung derselben Straße zu. Der Fahrer kann das
> nicht nachvollziehen und ignoriert die Anweisung zu Recht — womit die Navigation ihren Zweck
> verliert.

`GeolocationCoordinates.heading` ist nur bei Bewegung belastbar. **Bei Stillstand `null` senden,
nicht `0`** — `0` bedeutet „exakt nach Norden“.

### Manöver weiterschalten

```js
function advanceManeuver(distanceAlongLegMeters) {
  const next = maneuvers[maneuverIndex + 1];
  if (!next) return;

  const trigger = legGeometry[next.beginShapeIndex];
  if (distanceTo(trigger) < 25) maneuverIndex++;
}
```

Anzeige- und Sprachfelder je Manöver:

| Feld | Verwendung |
|---|---|
| `instruction` | die Zeile auf dem Bildschirm |
| `verbalTransitionAlertInstruction` | frühzeitiger Sprachhinweis („In 400 Metern …“) |
| `verbalPreTransitionInstruction` | Sprachhinweis unmittelbar vor dem Manöver |
| `verbalPostTransitionInstruction` | danach („Der Straße 2 Kilometer folgen.“) |
| `verbalSuccinctTransitionInstruction` | Kurzfassung für kleine Anzeigen |
| `type` | **benannt**, z. B. `Right`, `RoundaboutEnter` — Symbolauswahl |
| `roundaboutExitCount` | „die dritte Ausfahrt nehmen“ |
| `sign.exitNumbers` | Autobahnabfahrt — verlässlicher als der Straßenname |
| `beginShapeIndex` / `endShapeIndex` | Lage des Manövers in `leg.shape` |

> `type` ist **benannt** und nicht die Rohzahl von Valhalla. Die Rohzahl steht zusätzlich in
> `rawType`. Damit muss im Client keine Tabelle mit 44 Zahlenwerten gepflegt werden — die erste
> falsche Zeile darin zeigt dem Fahrer ein Linksabbiegen-Symbol an einer Rechtskurve. Erweitert
> Valhalla die Aufzählung, liefert die API `Unknown` und die unverfälschte Zahl statt eines falschen
> Namens.

---

## 6. Die vier Fälle aus dem Fahrerhaus

### 6.1 „Ich fahre zur Pause und komme danach zurück“

Nichts an der Tour ändert sich. Der Client

1. behält `activeStopSequence` unverändert,
2. **schaltet die Abweichungserkennung ab** oder ignoriert `rerouteRecommended`, solange der Fahrer
   „Pause“ gewählt hat,
3. nutzt für die Fahrt zur Pausenstelle `POST /api/Navigation/directions`,
4. ruft nach der Pause `POST .../guidance` mit **derselben** `targetStopSequence` auf.

> `targetStopSequence` unbedingt mitsenden. Ohne diese Angabe leitet der Server den nächsten Halt
> aus der Position ab — und eine Position abseits der Route lässt sich nicht sinnvoll zuordnen. Das
> Antwortfeld `nextStopWasInferred` zeigt an, ob abgeleitet wurde.

### 6.2 „Ich musste abbiegen / bin falsch gefahren“

Der Regelfall. `guidance` berechnet nicht die Tour neu, sondern den Weg von der **tatsächlichen**
Position zum unveränderten nächsten Halt. Reihenfolge, Zuordnung und CO2-Bilanz der Tour bleiben
unberührt.

### 6.3 „Die Straße ist gesperrt“

```js
blockedPoints.push({ latitude: fix.latitude, longitude: fix.longitude });
await requestGuidance(fix);
```

> **`blockedPoints` muss bei jeder weiteren Neuberechnung dieser Schicht erneut mitgesendet
> werden.** Der Server merkt sich nichts. Wird die Liste vergessen, führt die Engine den Fahrer bei
> der nächsten Neuberechnung erneut in die Absperrung — und zwar zuverlässig, weil es der
> günstigste Weg bleibt.

Für flächige Sperrungen (Baustelle, Veranstaltung, Hochwasser) gibt es `excludePolygons` als Liste
von Ringen:

```json
{ "excludePolygons": [ { "ring": [ {"latitude": 50.89, "longitude": 12.08 }, … ] } ] }
```

### 6.4 „Ich bin am Container angekommen“

`progress` meldet `atNextStop`, sobald die Luftlinie unter `arrivalRadiusMeters` liegt. Der Client
schaltet `activeStopSequence` weiter und ruft `guidance` für den folgenden Halt auf.

`stops[].offsetFromRouteMeters` ist dabei zu beachten: Ein großer Wert bedeutet, dass der Behälter
abseits der befahrenen Strecke liegt (Hinterhof) und der Fahrer ihn nicht von der Fahrbahn aus
erreicht.

---

## 7. Statuscodes

| Code | Bedeutung | Verhalten des Clients |
|---|---|---|
| `200` | Ergebnis liegt vor | verarbeiten |
| `400` | ungültige Eingabe, oder `targetStopSequence` gehört nicht zu dieser Route | Programmfehler, melden |
| `403` | Recht `ViewRoutes` fehlt | Anmeldung prüfen |
| `404` | Route existiert nicht | Tour neu laden |
| `409` | kein Halt steht mehr aus | Schicht abschließen |
| `422` | keine verwertbare Geometrie **oder** für dieses Fahrzeug ist kein Weg befahrbar | Meldung anzeigen, nicht wiederholen |
| `429` | Ratenbegrenzung | `Retry-After` beachten, Takt verringern |
| `499` | Anfrage wurde abgebrochen (Funkloch) | still wiederholen |
| `502` / `504` | Routing-Instanz nicht erreichbar bzw. zu langsam | mit Rückhalt wiederholen, örtliche Projektion weiterführen |

Bei `502`/`504` bleibt die Navigation benutzbar: Die geplante Geometrie liegt im Client, die
Projektion läuft weiter. Nur die *Neuberechnung* ist bis zur Erholung der Instanz nicht möglich.

---

## 8. Was diese Endpunkte ausdrücklich **nicht** tun

- **Sie planen die Tour nicht um.** Reihenfolge und Auswahl der Halte entstehen ausschließlich in
  `/api/Routes`. Ein Umweg ändert den Weg zum nächsten Halt, nicht die Tour.
- **Sie speichern nichts.** Keine Fahrzeugspur, keine Ankunftsmeldung, keine Ist-Zeiten. Wird eine
  Nachweisführung benötigt, ist sie eigenständig zu spezifizieren.
- **Sie ersetzen `/api/MapMatching` nicht.** Map-Matching gleicht *aufgezeichnete* Spuren gegen das
  gesamte Straßennetz ab und beantwortet „welche Straßen wurden befahren?“. Für „ist dieses Fahrzeug
  noch auf Route X?“ ist der Lotfußpunkt auf die bereits berechnete Geometrie exakt, sofort und ohne
  Netzaufruf verfügbar.
- **Sie liefern keine Fahrspurführung.** Valhalla kann Spurhinweise (`turn_lanes`); sie werden
  derzeit nicht angefordert. Für Autobahnknoten mit LKW wäre das eine sinnvolle Erweiterung.

---

## 9. Sonderrechte („Hoheitlich-Modus“) in der Fahreranweisung

Ist `Routing:Sovereign:Enabled` eingeschaltet und `Routing:Navigation:ApplySovereignRights`
(Vorgabe) aktiv, verwenden die Fahrhinweise **dasselbe** Kostenmodell wie die Planung — samt
Sonderrechten. Die Antwort weist das in `directions.sovereignRightsApplied` aus.

> **Ist dieses Kennzeichen `true`, muss die Fahreroberfläche es sichtbar anzeigen.** Der Fahrer trägt
> die Verantwortung für die Fahrzeugführung und muss erkennen können, dass ihm eine Strecke unter
> Sonderrechten angewiesen wird — etwa entgegen der Fahrtrichtung einer Einbahnstraße.

Die Anwendung startet in dieser Konstellation nur, wenn
`Routing:Navigation:AcknowledgeGuidanceAgainstTrafficRules` ausdrücklich gesetzt ist. Alternativ
schaltet `ApplySovereignRights: false` auf StVO-konforme Hinweise um — dann weichen Hinweis und
geplante Strecke bewusst voneinander ab.

Im Auslieferungszustand ist der Hoheitlich-Modus **aus**; das Kennzeichen ist dann stets `false`.

---

## 10. Konfiguration

Alles unter `Routing:Navigation`, geprüft beim Start.

| Schlüssel | Vorgabe | Wirkung |
|---|---|---|
| `Language` | `de-DE` | Sprache der Anweisungstexte |
| `ApplySovereignRights` | `true` | Fahrhinweis auf derselben Kostenbasis wie der Plan |
| `AcknowledgeGuidanceAgainstTrafficRules` | `false` | Pflicht, sobald Sonderrechte in Hinweisen wirken |
| `OffRouteToleranceMeters` | `35` | zulässiger Querabstand |
| `OffRouteConfirmationFixes` | `3` | bestätigende Ortungen vor einer Neuberechnung |
| `ArrivalRadiusMeters` | `40` | Ankunftserkennung |
| `HeadingToleranceDegrees` | `60` | Zuordnung der Fahrtrichtung zur Straße |
| `MaxIntermediateWaypoints` | `10` | Grenze je Anfrage |
| `MaxExcludeLocations` | `50` | Grenze je Anfrage (Valhalla-Obergrenze) |
| `MaxExcludePolygons` | `4` | Grenze je Anfrage |
| `MaxExcludePolygonVertices` | `200` | Grenze je Sperrfläche |
| `MaxDecodedPlanPoints` | `20000` | Obergrenze der serverseitig dekodierten Geometrie |

`OffRouteToleranceMeters`, `OffRouteConfirmationFixes` und `ArrivalRadiusMeters` werden dem Client
in jeder Antwort mitgeteilt. **Diese Werte nicht im Client festschreiben** — sonst existiert die
Entscheidungsregel doppelt und läuft mit der nächsten App-Version auseinander.
