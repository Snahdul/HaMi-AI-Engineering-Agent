# Architekturleitlinien: Module und Vertical Slices

Stand: 20.11.2026 · [Issue #19](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/19)

## Zielbild

HaMi AI Engineering entwickelt **fachlich geschlossene Vertical Slices** mit klaren Modulgrenzen. Wir organisieren Code primär nach **Anwendungsfall bzw. Feature**, nicht nach globalen technischen Schichten (`Controllers/`, `Services/`, `Repositories/` für das gesamte System).

Ein Vertical Slice führt einen konkreten Auftrag **End-to-End** aus: Eingabe → Anwendungslogik → externe Schnittstellen → Validierung → Ergebnis. Er erhält eigene Akzeptanzkriterien und Tests. Die Architektur muss autonome, begrenzte Ausführung ermöglichen, ohne technische oder fachliche Entscheidungen unkontrolliert an das Modell zu delegieren.

**Modularität** und **Vertical Slices** ergänzen sich:

- Ein **Modul** kapselt zusammengehörige Fähigkeiten, seine Verträge und Abhängigkeiten.
- Ein **Slice** realisiert innerhalb eines Moduls einen konkreten Use Case durch alle benötigten Schichten.
- Ein eigener Prozess, eine Assembly, ein separates Projekt oder ein Message Bus sind **keine Voraussetzung**. Wir führen diese Grenzen erst ein, wenn eine konkrete Abhängigkeit oder ein eigenständiger Release-/Testbedarf sie rechtfertigt.

## Aktueller Agent: bewusst inkrementelle Struktur

```text
src/HaMi.Agentic.Cli/
  Program.cs                     # CLI-Routing / später Composition Root
  Features/
    Replay/                      # historischer Replay-Use-Case
      ReplayCommand.cs
    Workflow/                    # zweistufiger Dokument-Use-Case
      WorkflowCommand.cs
    Coding/                      # Code generieren → validieren → bauen → testen
      CodingCommand.cs
      CodingAiClient.cs          # IChatClient-Adapter dieses Slices
  Runtime/
    RuntimeSettings.cs           # tatsächliche Querschnittskonfiguration
```

Diese **bestehenden** Feature-Grenzen bleiben erhalten. `CodingAiClient` gehört vorerst in den Coding-Slice; erst bei einem echten zweiten Nutzer wird eine übergreifende KI-Schnittstelle herausgezogen.

Die bestehenden großen Command-Klassen sind **technische Schuld**, keine Zielarchitektur. Wir trennen Verantwortlichkeiten innerhalb des jeweils betroffenen Slices schrittweise, beispielsweise:

```text
Features/Coding/
  CodingCommand.cs               # Parameter, Aufruf, Ergebniscode
  CodingUseCase.cs               # begrenzter Ablauf und Wiederholungsbudget
  CodingTask.cs                  # fachlicher Auftrag + Akzeptanz
  CodingAiClient.cs              # Modelladapter (IChatClient)
  ExpressionValidator.cs         # erlaubte Syntax und Zielpfade
  CodingWorkspace.cs             # isolierter Git-Worktree
  CodingVerifier.cs              # feste Build-/Testbefehle
  CodingReport.cs                # lokale Nachweise
```

Diese zweite Struktur ist **geplant**, nicht bereits implementiert. Klassen werden erst ausgelagert, wenn Tests und Verantwortungsschnittstellen sinnvoll sind. Keine pauschale Neuschreibung der funktionierenden MVPs.

## Abhängigkeitsregeln

1. Jeder Slice besitzt seine **fachlichen Regeln, Task-Verträge, Modellprompts, Validatoren und Tests**. Feature A greift nicht auf Implementierungsdetails von Feature B zu.
2. `Runtime/` bzw. spätere gemeinsame Infrastruktur enthält **ausschließlich echte Querschnittsfunktionen**: Konfiguration, Logging, sichere Worktree-Verwaltung oder standardisierte Modellanbindung, sofern von mehreren Slices gebraucht.
3. Externe Dienste stehen hinter **kleinen, testbaren Adaptern**. `Microsoft.Extensions.AI.IChatClient` ist unser bevorzugter Vertrag für Modellzugriffe; Refit ist für geeignete REST-Clients vorgesehen, nicht pauschal für jedes HTTP-API.
4. **Dependency Injection** an der äußeren Kompositionsgrenze; Geschäfts-/Validierungslogik soll ohne Netzwerk, API-Key oder GitHub schrittweise testbar sein.
5. Keine künstlichen `I*Service`-Interfaces für Klassen ohne mehrere Implementierungen, keine generischen Repositories oder Mediator-/CQRS-Pflicht. Abstraktionen entstehen aus einer tatsächlich benötigten Test- oder Austauschgrenze.
6. Änderungen innerhalb eines Slices sollen möglichst **lokal** bleiben. Grenzüberschreitende Änderungen brauchen begründete Architekturentscheidung und Regressionstests.

## Vorgaben für generierten Anwendungscode

Vor dem ersten Modellaufruf erhält der Agent einen **strukturierten Entwicklungsauftrag** mit:

- Feature/Modul und eindeutigem Use Case; fachlicher Ein- und Ausgabe-/Fehlervertrag.
- Ziel-Framework (aktuell .NET 11), betroffene und erlaubte Dateien sowie bestehende Projektkonventionen.
- Zulässigen Abhängigkeiten und einzuhaltenden Architekturgrenzen.
- Akzeptanzkriterien einschließlich Positiv-, Negativ- und Randfalltests.
- Sicherheits- und Ausführungsgrenzen, insbesondere keine freien Modellbefehle oder Änderungen an `main`.

Der generierte Code folgt der **vorhandenen Architektur des Zielprojekts**, nicht blind der des Agenten. Bei neuen Projekten bevorzugen wir featureorientierte Slices: z. B. `Features/Orders/CreateOrder/` mit Request, Handler/Use Case, Validation, Endpunkt und passenden Tests. Gemeinsame Domänentypen werden nur dann zentralisiert, wenn es echte fachliche Invarianten gibt.

Eine Bibliothek wird nur genutzt, wenn sie den Use Case konkret vereinfacht: `CommunityToolkit.Mvvm` für MVVM-UI, Refit für geeignete HTTP-Integrationen, `Microsoft.Extensions.AI` für KI-Anbindung. Keine unnötigen Pakete, Schichten oder Frameworks nur aus Konvention.

## Prompt Assets statt C#-Stringliteralen

Für jeden Slice liegen System- und Aufgabenprompts in **separaten versionierten Dateien**. Neue Prompts verwenden bevorzugt **Prompty** (`.prompty`): YAML-Frontmatter für Metadaten/Eingaben, darunter Markdown-Text mit Rollen (`system:`, `user:`). Die bisherige Semantic-Kernel-YAML-Form bleibt für bestehende SK-Anwendungen gültig.

Aktueller konkreter Coding-Slice:

```text
prompts/
  coding/
    ClampExpression.prompty     # lesbarer System- und User-Prompt
src/HaMi.Agentic.Cli/
  Features/Coding/
    CodingPromptTemplate.cs     # geprüftes Laden/Rendern; keine LLM-Texte
    CodingAiClient.cs           # IChatClient-Transport
    CodingCommand.cs            # begrenzter Build/Test-Korrektur-Ablauf
```

**Grenzen der aktuellen Implementierung:** Der schlanke C#-Loader verarbeitet aus dem Prompty-Format **nur** die verifizierte Teilmenge YAML-Frontmatter plus genau eine `system:`- und eine `user:`-Nachricht sowie `{{feedback}}`. Er ist **kein allgemeiner Prompty-/Jinja2-Interpreter**. Zusätzliche Template-Konstrukte und Modell-/Tool-Konfigurationen werden zurückgewiesen. Sicherheits- und Laufzeitwerte wie erlaubte Pfade, Modell-Tools, Budgets und Timeouts liegen in einer vertrauenswürdigen, versionierten Policy-Datei. Der Host validiert und erzwingt diese Werte; Prompt-Metadaten dürfen sie nicht überschreiben.

**Verbindlich für kommende Slices:**
- Ein Prompt ist ein eigenständiges, überprüfbares **Feature-Asset**. Der Code lädt es über einen kleinen Adapter; keine mehrzeiligen Prompts als `const string` im C#-Quelltext.
- Variable Eingaben erfolgen über benannte Platzhalter mit geprüftem Eingabevertrag. Compiler-/Test-Feedback ist **unvertraute Eingabedaten**, keine neue Systeminstruktion.
- Prompt-Änderungen sind reguläre Code-Änderungen: Git-Diff, Review und Offline-Tests für Parsing, Rollen, erforderliche Variablen und Fehlerfälle.
- Prompt-Metadaten dürfen **keine** zusätzlichen Modell-Tools, Berechtigungen, Schlüssel, Endpunkte oder Dateisystemzugriffe freischalten.
- Die verbliebenen Inline-Prompts der historischen Replay-/Workflow-Slices werden **schrittweise** in deren eigene Feature-Verzeichnisse ausgelagert; keine unkontrollierte Änderung funktionierender Modellabläufe.

**Formatquellen:** [Prompty-Spezifikation](https://prompty.ai/specification/file-format/) · [Semantic Kernel YAML Prompt Schema](https://learn.microsoft.com/en-us/semantic-kernel/concepts/prompts/yaml-schema).

## Deklarative Execution Policies (Policy-as-Configuration)

Für die Laufzeitgrenzen eines Slices verwenden wir eine **versionierte JSON-Policy** mit typisiertem Options-Modell und strikter Startvalidierung. Die Konfigurationsabstraktionen `Microsoft.Extensions.Configuration` und der Binder bilden den etablierten .NET-Mechanismus; in einer späteren DI/Host-Architektur wird die Policy über `IOptions<T>` und `IValidateOptions<T>` bzw. `ValidateOnStart` bereitgestellt.

```text
prompts/coding/ClampExpression.prompty        # fachliche Modellanweisungen
policies/coding/clamp.policy.json              # Rechte, Ressourcenbudgets, erlaubte Pfade
src/HaMi.Agentic.Cli/Features/Coding/
  CodingExecutionPolicy.cs                    # typisierter Loader/Validator/Policy-Snapshot
  CodingCommand.cs                            # Erzwingung bei Writes, Modell-/Prozessaufrufen
  CodingAiClient.cs                           # Provideradapter liest dieselben Limits
```

Die Policy legt fest: `allowedWritePaths`, `allowedModelTools`, `allowShellCommands`, `allowGitHubWrites`, `storeModelResponses`, `maxModelCalls`, `maxOutputTokens`, `processTimeoutSeconds`, `apiTimeoutSeconds`. **Diese Werte nicht mehr im Code suchen oder in Prompts verstecken.** Die Policy ist ein eigenständig reviewbares Artefakt. Der Laufbericht dokumentiert Pfad und SHA-256-Fingerabdruck zur Nachvollziehbarkeit.

**Zwei unterschiedliche Grenzen:**

1. **Konfigurierbare Berechtigung:** Der vertrauenswürdige Projektverantwortliche legt die Richtlinien in der versionierten Datei fest. Der ausführende Agent und das Modell erhalten keine Schreibrechte auf diese Policy. Keine Umgebungsvariablen-/Prompt-Overrides für Berechtigungen.
2. **Technisch implementierte Fähigkeit:** Die CLI kann derzeit ausschließlich die feste `ClampKata`-Datei beschreiben und feste `dotnet`-Befehle ausführen. Die Policy darf diese Fähigkeiten **einschränken**, nicht durch Eintrag von `shell`, `github` oder einem beliebigen Pfad neue Fähigkeiten herbeizaubern. Ein weitergehender Rechteumfang benötigt Implementierung, abgesicherte Ausführungsumgebung, Tests und Freigabe.

**Fail-closed:** Fehlende Policy, unbekannte Eigenschaften, unbekannte Version, unerlaubte Berechtigungen und zu große Budgets führen **vor** Worktree-/API-Zugriff zum Abbruch. Laufzeitwerte werden als geprüfter Snapshot eingelesen, nicht während des Ausführungszyklus dynamisch nachgeladen. Der API-Schlüssel bleibt außerhalb dieser Datei im Windows-Kontext.

Später kann ein `appsettings.json` die allgemeinen *nicht sicherheitsrelevanten* Einstellungen (z. B. Ausgabe-/Log-Stufe) enthalten. Die Sicherheits-Policy bleibt davon getrennt; pauschales `AddEnvironmentVariables()` als höher priorisierte Quelle für Berechtigungen wäre hier unerwünscht.

**Quellen:** [Microsoft Configuration](https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration) · [Options pattern und ValidateOnStart](https://learn.microsoft.com/en-us/dotnet/core/extensions/options) · [ErrorOnUnknownConfiguration](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.configuration.binderoptions.erroronunknownconfiguration).

## Vertrauensgrenzen und Prompt Injection

Alle Inhalte aus Compiler-/Test-/Analyzer-Ausgaben, Modellantworten, GitHub-Issues,
Repository-Dateien, Webseiten und späteren Tool-/MCP-Ergebnissen werden als
**untrusted data** behandelt – auch wenn sie scheinbare `system:`-Rollen,
Anweisungen, JSON oder Markdown enthalten. Sie dürfen keine neue Policy,
Modellrolle oder Laufzeitberechtigung definieren.

Der Coding-Slice kodiert Diagnosefeedback als **begrenztes JSON-Datenfeld**,
statt Rohtext an Promptinstruktionen anzuhängen. Das Budget
`maxFeedbackCharacters` steht in der vertrauenswürdigen JSON-Ausführungspolicy.
Fake-Rollen und überlange Nutzdaten werden adversarial offline getestet.
**Das ist keine absolute Prompt-Injection-Verhinderung**: Sicherheitsentscheidungen
sind weiterhin deterministisch in Host-/Policy-Prüfungen zu erzwingen.

[Bedrohungsmodell und Sicherheitsgrenzen](prompt-injection-threat-model.md).

## Fachlich benannte Ergebnisse mit C# 15 Unions

Erwartete fachliche Ausgänge eines **einzelnen Vertical Slices** werden mit
nominalen C#-15-Union-Typen modelliert, nicht pauschal mit einem globalen
`Result<T>` für sämtliche APIs. Die Namen sind anwendungsfallbezogen:
`SourceValidationResult`, `CompilationResult`, `TestExecutionResult`,
`CodingAttemptResult` und `CodingRunResult`. Jeder Union-Case bekommt
einen eindeutigen fachlichen Namen und erforderliche Diagnoseinformationen.

Die Coding-Implementierung liegt in
[`Features/Coding/CodingResults.cs`](../src/HaMi.Agentic.Cli/Features/Coding/CodingResults.cs).
Der tatsächlich benutzte Ablauf differenziert Quellcode-Ablehnung, Compilerfehler,
fehlgeschlagene Tests und erfolgreiche Versuche. `CodingRunSucceeded` und
`CodingRunExhausted` sind die einzigen regulären Gesamtausgänge. Die
Ausführung kann bei unerwarteten Infrastruktur-/Programmfehlern weiterhin
mit einer Exception abbrechen; diese Fehler werden nicht als fiktive
fachliche Ergebnisse kaschiert.

**Result vs. Report:** `CodingRunResult` beschreibt den Zustand des
Auftrags. `CodingRunReport` ist ein gesondertes, stabiles
Kompatibilitäts-/Auditmodell. Die Union-Fälle werden explizit auf die
historischen `report.json`-Felder projiziert (Status, Versuchsliste und
boolesche Flags), ohne neue widersprüchliche Zustände im fachlichen
Ablauf zu ermöglichen. Die vorhandenen Berichtsfelder bleiben erhalten.

**Regeln:** Compiler-geprüfte `switch`-Abdeckung ohne generischen Catch-all;
immutable Snapshots der Versuche, keine globale Super-Union; keine
Unions in fremde SDK-Schnittstellen pressen. Für sehr heiße
Werttypenpfade müssten mögliche Boxing-Kosten untersucht werden.
Die neue Struktur wird mit Offline-Tests für **alle** Union-Fälle,
die bestehende JSON-Projektion, echte Compiler-/Testkorrekturen und
Rollback abgesichert.

## Strukturiertes High-Performance-Logging

Der Agent trennt **fachliche Ereigniserzeugung** (`ILogger`) und
**persistente Ausgabe** (Serilog). Anwendungscode verwendet ausschließlich
`Microsoft.Extensions.Logging.ILogger` mit source-generierten
`[LoggerMessage]`-Methoden, stabilen EventIds und typisierten Feldern.
`Serilog.Extensions.Logging` bindet den Provider einmalig in
`Runtime/Logging/` an. Keine direkten `Serilog.Log.*`-Aufrufe im Slice.

Die versionierte [Logging-Konfiguration](../config/logging.json) legt
zwei **unabhängige Defaults** fest:

- `logSuccessfulSteps=false`: Positive Versuche/Abschlüsse unterdrücken.
- `logDecisionSummaries=true`: Explizite, kurze Annahmen,
  Entscheidungen und Begründungen protokollieren.

Alle Laufereignisse tragen eine korrelierbare `RunId`; Fehlerereignisse
bestehen aus EventId, Versuch und knappem Fehlercode/Exception-Typ.
Keine Secrets, Rohprompts, vollständigen Diagnosen, Modelltexte oder
Stacktraces in den Logs. Decision-Summaries sind **ausdrücklich
formulierte, überprüfbare Begründungen**, keine verborgenen
Modell-Gedankengänge.

Serilog schreibt kompaktes JSON pro Zeile in den vom Arbeitsrepository
getrennten Worktree-Root. Tages-/Größenrotation und Aufbewahrung werden
durch eine streng validierte, datengetriebene Konfiguration gesteuert.
Die fachliche `report.json`-Schnittstelle bleibt hiervon getrennt.

[Ereigniskatalog und Offline-Regressionsprüfungen](logging.md).

## Zweiter ausführbarer Coding-Task – verifiziertes Produktionsziel

Mit [Issue #28](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/28)
kann `coding --task retry-budget` erstmals einen klar begrenzten
**Produktionsquelltext** im Agenten selbst bearbeiten.
`CodingTaskDefinition` verwaltet die einzigen zwei erlaubten Task-IDs,
jeweils mit festem Zielpfad, zugehörigem Projekt und Allowlist der C#-
Identifier. Ein frei erfundener Task oder eine abweichende Ziel-Datei
wird vom Host **vor** einem Modell- oder Dateischreibaufruf abgelehnt.

```text
src/HaMi.Agentic.Cli/Features/Coding/
  CodingTaskDefinition.cs     # Host-Allowlist: clamp-kata / retry-budget
  RetryBudget.cs              # echter Coding-Produktionscode
samples/RetryBudget/
  RetryBudget.csproj          # vertrauter .NET-11-Testharness, linkt Produktionsdatei
  Program.cs                  # feste Grenzfall- und deterministische Tests
prompts/coding/RetryBudget.prompty
policies/coding/retry-budget.policy.json
```

Der Modelloutput ist **weiterhin ausschließlich ein kurzer C#-Ausdruck**
ohne Methoden-/Dateisystemzugriff, beliebige API-Aufrufe oder dynamische
Shellbefehle. Die `retry-budget`-Policy genehmigt exakt
`src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs`, nicht generell
das `src/`-Verzeichnis. Die Produktion-Methode wird über `Compile Include`
in einem fest vorgegebenen Testprojekt geprüft. Der Host behält seine
von dieser Methode unabhängige Begrenzung auf höchstens drei Modellversuche.

**Wesentliche Grenze:** Ein detached Git-Worktree ist keine OS-Sandbox.
Generierter allgemeiner Code ist weiterhin ausdrücklich verboten,
bis eine getrennte sichere Ausführungsumgebung und stärkere AST-/Diff-
Prüfungen vorhanden sind. `main` wird ausschließlich nach menschlichem
Review/Freigabe geändert.

## Qualitätsgates je Vertical Slice

1. **Vertrag und Architektur**: Scope, erlaubte Pfade, Modulgrenzen, Abhängigkeiten und Definition of Done prüfen.
2. **Deterministische Prüfung**: `dotnet restore/build/test`, Analyzer und Formatierung gemäß Zielprojekt; bei neuen Slice-Implementierungen mindestens ein passender positiver, negativer und Randfalltest.
3. **Review des Diffs**: Nur autorisierte Dateien; keine neuen versteckten Abhängigkeiten, Secrets, globalen Seiteneffekte oder unerwarteten Querverweise.
4. **Begrenzte Korrekturschleife**: Compiler-, Test- und Analyzerfehler als Feedback; keine automatische Lockerung von Akzeptanzkriterien oder Sicherheitsregeln.
5. **Isoliertes Ergebnis**: Worktree auf I: (ReFS), nachvollziehbarer Bericht; Integration nach `main` ausschließlich nach menschlicher Entscheidung.

**Aktueller Stand (Issue #19):** Die Coding-Aufträge führen echte C#-Builds und feste Tests für zwei eng begrenzte Ausdrücke aus. Der optionale `IChatClient`-Transport ist auf HaMi11 erprobt. Der PR-Workflow prüft zusätzlich die Formatierung und aktivierten Analyzer der **geänderten produktiven C#-Dateien** nicht-mutierend (`dotnet format ... --verify-no-changes`); bei reinen Konfigurationsänderungen läuft ein fester Smoke-Test. Die `.editorconfig` aktiviert zunächst nur CA2200 und CA2016 und erzwingt bewusst **keine** Umwandlung von LF/CRLF. Sample-Projekte, globale Bestandsbereinigung und allgemeine Codegenerierung sind von diesem ersten Gate nicht abgedeckt. Die Ausführung beliebigen generierten Codes benötigt weiterhin eine OS-Sandbox und zusätzliche Prüfungen.
