# Logging und Entscheidungsnachweise

Stand: 20.11.2026 · [Issue #24](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/24)

## Standardkonfiguration

[`config/logging.json`](../config/logging.json) ist eine vom Menschen versionierte,
streng validierte Laufzeitkonfiguration:

```json
{
  "version": 1,
  "logSuccessfulSteps": true,
  "logDecisionSummaries": true,
  "minimumLevel": "Information",
  "relativeDirectory": "hami-agent-logs",
  "fileSizeLimitBytes": 1048576,
  "retainedFileCountLimit": 7
}
```

**Die Schalter sind unabhängig voneinander:**

- `logSuccessfulSteps=true` (Standard): erfolgreiche Schritte und Abschlüsse werden zusammen mit Warnungen, Fehlern und Entscheidungen protokolliert. Mit `false` können optionale Erfolgsereignisse deaktiviert werden.
- `logDecisionSummaries=true` (Standard): ausdrücklich erzeugte, knappe
  **Entscheidungszusammenfassungen** als `Information` loggen.
- Bei `logDecisionSummaries=false` bleiben Fehlereinträge erhalten.
- `minimumLevel=Warning` oder `Error` kann die übergeordneten Events
  zusätzlich ausfiltern. Für standardmäßige Entscheidungsnachweise muss
  mindestens `Information` aktiv sein.

Die Einstellung ist **kein Prompt** und kann weder Modell-Tools noch zusätzliche
Dateizugriffe freischalten. Konfigurationsänderungen werden erst mit einem neuen
CLI-Lauf wirksam, nicht mitten in einer laufenden Codierung.

## Technisches Pattern

Der Agent verwendet `Microsoft.Extensions.Logging.ILogger` und
kompilierzeitgenerierte `[LoggerMessage]`-Methoden mit **stabilen EventIds**.
Die C#-Features kennen Serilog nicht. Das einzige Serilog-Setup
(`Runtime/Logging/AgentLogging.cs`) bindet den Microsoft-Logger an einen
rollierenden `Serilog.Sinks.File`-Sink mit `CompactJsonFormatter`.

Ausgabe unter
`<HAMI_AGENT_WORKTREE_ROOT>/hami-agent-logs/agent-<Datum>.clef`:
jede Zeile ist ein einzelnes maschinenlesbares Compact-JSON-Ereignis. Zusaetzlich liegt unter demselben Root eine direkt lesbare Datei `agent-<Datum>.log` mit chronologischen Nachrichten, Zeitstempel und Level. Beide Dateien entstehen aus denselben ILogger-Events und sind unabhaengig nach Groesse/Datum rotiert und in ihrer Aufbewahrung begrenzt. Die
Rotation erfolgt täglich und bei Erreichen des konfigurierten Größenlimits,
mit begrenzter Dateianzahl. Der Logordner liegt außerhalb des produktiven
Checkouts. Die Event-Scope-Eigenschaft `RunId` korreliert Ereignisse eines Laufs.

**Rohdatenverbot:** Keine API-Schlüssel, Rohprompts, komplette Modellantworten,
Quellcode, Test-/Compiler-Rohtexte, Secrets, vollständige Exception-Nachrichten
oder Stacktraces. Verarbeitet werden nur feste Fehlercodes, kurze
Entscheidungsfelder und Versuchsnummern.

## Ereigniskatalog des Coding-Slices

| EventId | Ereignis | Level | Bedingung |
| --- | --- | --- | --- |
| 2400 | `DecisionRecorded` | Information | `logDecisionSummaries=true` |
| 2410 | `ExpressionRejected` | Warning | Validierungsfehler |
| 2411 | `CompilationFailed` | Warning | Build-Fehler |
| 2412 | `TestExecutionFailed` | Warning | Testfehler |
| 2413 | `CodingRunExhausted` | Error | Modellversuchslimit erschöpft |
| 2414 | `CodingRunFailed` | Error | Unvorhergesehene Ausnahme; nur Typ |
| 2420 | `AttemptSucceeded` | Information | `logSuccessfulSteps=true` |
| 2421 | `RunSucceeded` | Information | `logSuccessfulSteps=true` |

`DecisionRecorded` besitzt bewusst nur explizite, geprüfte strukturierte
Eigenschaften: `DecisionCode`, `Assumption`, `Action`, `Rationale` und
die per `BeginScope` vergebene `RunId`. Beispiele: die Beschränkung auf die
feste Clamp-Aufgabe und die bewusste Entscheidung zum Korrekturversuch nach
Compiler-/Testfehlern bei verfügbarem Aufrufbudget. **Das sind kontrollierte
Begründungen, kein vollständiges Protokoll interner Modellüberlegungen.**

Spätere Modellentscheidungen können nach Schema validierte Decision-DTOs
erzeugen; ihnen dürfen nicht automatisch neue Berechtigungen oder
höhere Autorität zugewiesen werden.

## Offlineprüfung

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --self-test-logging --repo .
```

Dieser Test benötigt weder Modell noch API-Schlüssel. Er prüft, dass die
Default-Konfiguration **Fehler, Decision-Summaries und positive Schritte** speichert. Danach werden beide Schalter unabhängig
variiert. Außerdem werden korrumpierte Konfigurationen (unbekannte Keys,
ungültige Levels, Path Traversal, absolute Pfade und überzogene
Dateigrößen-/Retention-Limits) abgewiesen.

Die Logdateien sind ein **technisches Auditmedium**, kein Ersatz für den
stabilen, lokalen fachlichen `report.json`-Bericht.


## Development-Agent (Issue #38)

`development --agent-run` nutzt dieselbe `ILogger`-/Serilog-Session, nun mit
**Console-Provider, lesbarer rotierender .log-Datei und Compact-JSON-Datei**.
EventIds 3600–3610 beschreiben Startparameter, Preflight, Generierung,
MicroVM-Phasen, Retry-Entscheidungen, klassifizierte Compilerfehler und
abschliessenden Bericht. Die eindeutige `JobId` korreliert Actor,
`agent-job-<JobId>`-Verzeichnis und JSON-Report.

`Information`: lebenszyklusrelevante Erfolge und Entscheidungen;
`Warning`: korrekturfahige Fehler; `Error`: terminale Fehler mit
Phase, Ursache und Handlungsempfehlung; `Debug`: verworfene/stale Nachrichten.
Weder Rohausgaben des Modells noch Sandboxtests, Secrets oder
unbereinigte Exceptiontexte landen in den Betriebslogs.

Der CLI-Aufruf muss einen benannten Unterbefehl verwenden, z. B.
`replay --check .` statt veraltetes `--check .`.
