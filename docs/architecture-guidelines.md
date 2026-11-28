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

- `logSuccessfulSteps=true`: Positive Versuche/Abschlüsse als nachvollziehbare Ereignisse mitprotokollieren; per Konfiguration abschaltbar.
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

**Aktueller Stand (Issue #19):** Die Coding-Aufträge führen echte C#-Builds und feste Tests für zwei eng begrenzte Ausdrücke aus. Der optionale `IChatClient`-Transport ist auf HaMi11 erprobt. Der PR-Workflow prüft über denselben HaMi-Wrapper die Formatierung und aktivierten Analyzer der **geänderten C#-Dateien in den beiden freigegebenen Produktions- und Testprojekten** auf Linux und Windows nicht-mutierend; bei leerem Scope läuft ein fester Smoke-Test. Die `.editorconfig` aktiviert zunächst nur CA2200 und CA2016 und erzwingt bewusst **keine** Umwandlung von LF/CRLF. Sample-Projekte, globale Bestandsbereinigung und allgemeine Codegenerierung sind von diesem ersten Gate nicht abgedeckt. Die Ausführung beliebigen generierten Codes benötigt weiterhin eine OS-Sandbox und zusätzliche Prüfungen.


## Issue-getriebene Codeerzeugung (Pilot #40)

Ein begrenzter GitHub-Issues-Adapter auf der Host-Seite erweitert den bestehenden Development-Vertical-Slice. Ein geprüftes Issue liefert fachliche Ziele und Akzeptanzkriterien, aber niemals Toolrechte, erlaubte Dateien oder Sandbox-/Retry-Grenzen. Die versionierte Contract-Definition bleibt verbindlich.

Der Agent führt genau den bisherigen Akka-/Sandbox-Job aus und dokumentiert dessen Ergebnis mit korrelierter Job-ID als einen aktualisierbaren Issue-Kommentar. Detaillierte Logs und Rohdiagnosen bleiben lokal. Nur ein ausdrücklich freigegebener Publish-Modus darf Schreibzugriffe ausführen; gegebenenfalls notwendige Folgeaufträge beginnen als neue offene Issues ohne autonome Ausführung.

GitHub Projects #2 wird weiter durch vorhandene Label-/PR-Actions gepflegt, nicht von einem zweiten Statussystem im Agenten.

## Erhaltung fehlgeschlagener Regressionstests

Ein fehlgeschlagener Test ist ein Befund, kein Hindernis, das durch Änderung seiner Erwartung beseitigt werden darf. Erhalte den konkreten fehlerhaften Kandidaten und reproduziere die Abweichung. Zusätzliche vereinfachte Tests sind zulässig, ersetzen den ursprünglichen Fehlerfall jedoch nicht. Unterscheide Implementierungsfehler, falschen Testaufbau und falsch formulierte Anforderung anhand überprüfbarer Evidenz. Änderungen an bestehenden Erwartungen, Toleranzen oder Akzeptanzkriterien erfordern einen dokumentierten Nachweis der fehlerhaften ursprünglichen Anforderung und menschliche Review-Freigabe; kein stilles Abschwächen, Überspringen oder Entfernen zur Erzielung grüner CI. Diese Regel gilt auch für autonome Korrekturschleifen.

## Automatische C#-Formatierung (Issue #19)

Microsoft `dotnet format whitespace` ist das verbindliche Korrekturwerkzeug für den vertrauenswürdigen Implementierungscode des Agenten. Es nutzt die .editorconfig und das SDK aus global.json; kein eigener Formatter, keine automatischen Analyzer-Codefixes und keine Bestandsbereinigung. Der bereits vorhandene nicht-mutierende CI-Whitespace-/Analyzer-Gate bleibt verbindlich.

Im separaten, geprüften Git-Worktree nach Restore und vor dem Commit (PowerShell 7.2+):

```powershell
$base = (git rev-parse origin/main).Trim()
dotnet restore src/HaMi.Agentic.Cli/HaMi.Agentic.Cli.csproj
./scripts/quality/Invoke-HaMiFormat.ps1 -Base $base -Mode Apply
```

Bei Änderungen im unterstützten JobState-Probe-Projekt zusätzlich dessen Restore ausführen. Apply selektiert neue/geänderte .cs-Dateien relativ zur expliziten Basis (auch untracked), begrenzt auf die beiden fest benannten Implementierungs-/Testprojekte, formatiert mit dem Herstellerwerkzeug und prüft unmittelbar danach erneut. Kein Apply auf main/master oder im primären Checkout. Hashvergleich von getrackten/nicht ignorierten Dateien und Git-Index/HEAD/Branch erkennt unerwartete Seiteneffekte; bei Fehler Worktree erhalten, nicht committen. Check-Modus ist nicht mutierend; bei leerem Scope kein Formatlauf. Ignorierte Buildartefakte sind kein Teil dieses Quellcode-Integritätsnachweises. Das Skript ist kein Sicherheits-Sandbox-Ersatz und setzt einen vertrauenswürdigen, exklusiv benutzten Implementierungsworktree voraus.

Linux- und Windows-CI führen genau einen Integrationstest für unsere HaMi-Verantwortung aus: Der ursprüngliche RetryBudget-Fehlerkandidat (CI 45460196255) wird geprüft, im erwarteten Fehlerfall ohne Mutation abgelehnt und über den tatsächlichen Korrekturpatch-Pfad formatiert. Der Patch muss anwendbar sein, ausschließlich den autorisierten Kandidaten ändern und die anschließende Microsoft-Prüfung bestehen. Der aufrufende Checkout und Kontrollquelle bleiben unverändert. Ein lokaler detached Test-Commit dient ausschließlich diesem Nachweis. Bewusst erwartete Herstellerdiagnosen werden im Negativtest abgefangen, nicht als CI-Fehlerannotation ausgegeben. Die Ursachenanalyse zur Hersteller-Einrückung (CI 45463667966) und alle drei erfolgreich ausgeführten früheren Tests (CI 45464425523, Git-Stand 61ed2d11) bleiben als historische Evidenz erhalten. Die [freigegebene Reduktion](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/19#issuecomment-7141481996) auf Integrationsverantwortung erfolgte nach vollständig grüner CI; keine Fehlerunterdrückung oder Abschwächung bestehender Qualitätsgates. HaMi Developer implementiert keinerlei eigene Formatter-Funktionalität und testet keine Microsoft-Einrückungsregeln oder Hersteller-Idempotenz. Der bisherige Linux-Format-/Analyzer-Gate wird ohne Abschwächung auf denselben Check-Mechanismus wie die lokale Entwicklung umgestellt und um das bestehende Testprojekt sowie Windows ergänzt. Bei dessen Fehlschlag erzeugt ein zusätzlicher read-only CI-Schritt einen tatsächlichen Microsoft-Korrekturpatch im temporären Worktree und protokolliert ihn zur Übernahme auf dem exakten PR-Stand; kein CI-Commit/Push und kein Überschreiben des fehlgeschlagenen Qualitätsgates. Die GitHub-API-basierte Remote-Implementierung kann dotnet nicht vor ihrem API-Commit ausführen; in diesem Pfad dient der CI-Patch als deterministische Korrekturquelle, nicht als Freigabe. Kein verpflichtender lokaler Git-Hook wird installiert.

**Abgrenzung zum generierten Agentenkandidaten:** dotnet format lädt MSBuild-Projekte. Unvertrauenswürdige generierte Projekte dürfen deshalb nicht auf dem Host formatiert werden. Spätere automatische Kandidatenformatierung innerhalb der frischen Offline-MicroVM, nur im autorisierten Scope, vor finalem unabhängigem Build/Test und Hashbildung, gesondert implementieren und abnehmen. Bestehender RetryBudget-Expression-Scope und seine Trivia-/Hash-Gates bleiben unverändert. Keine Ausweitung der Schreibrechte.

### Einheitlicher Format-/Analyzer-Gate (Issue #19, Folge aus M5b)

CI 45607384863: Der bisherige Gate erkannte fünf Whitespace-Fehler; der Patch-Helper scheiterte separat an der Git-Metadatenprüfung. Ein lokales Git-Minimalrepro zeigt: shallow HEAD plus separat geholter Base-Commit genügt nicht für `merge-base --is-ancestor` (Exit 1), vollständige Historie ergibt Exit 0. Die genaue fehlgeschlagene Abfrage des historischen CI-Laufs wurde damals nicht protokolliert. Beide CI-Checkouts erhalten vollständige Historie (`fetch-depth: 0`); der Wrapper lehnt shallow Repositories ausdrücklich ab, prüft die Base-Abstammung weiterhin und benennt fehlgeschlagene Git-Unterbefehle/Exitcodes ohne ungefilterte stderr-Ausgabe. Keine Umgehung der Scope- oder Checkout-Prüfung.

`Invoke-HaMiFormat.ps1` ist die einzige Quelle der Projektauswahl für Check, Apply und Patch-Helper. Check führt Microsoft Whitespace und Analyzer (`--severity warn`) aus; Apply korrigiert nur Whitespace und verifiziert ihn erneut, keine automatischen Analyzer-Codefixes. `-Restore` stellt die ausgewählten Projekte bereit; `-SmokeIfEmpty` erhält den CI-Smoke-Test ohne Ausweitung des Apply-Scope. Beide OS nutzen dieselben Check-/Patch-Aufrufe. Unterstützt werden CLI und JobState.Probe einschließlich dortiger Tests; historische Sandbox-/Sample-Fixtures bleiben aufgrund ihrer separaten Akzeptanz- und Hashverträge bewusst außerhalb dieses Format-Gates. Neue reguläre Testprojekte müssen explizit in der gemeinsamen Projekt-Allowlist ergänzt werden. Keine eigene Formatter-Implementierung. Der Integrationstest prüft nun auch einen Testprojekt-only Negativfall, den gemeinsamen Patch beider Projekte und Cleanup, nicht Microsoft-spezifische Einrückungsregeln. CI/Review vor Merge erforderlich.


### Ressourcenschonende CI-Ausführung (Issue #19)

Überholte PR-Ausführungen desselben .NET-CI-Workflows werden durch GitHub Actions Concurrency abgebrochen; getrennte PRs und manuell gestartete Prüfungen bleiben unabhängig. Nur der aktuelle PR-Stand kann die vollständige Abnahme erhalten. Keine Änderung an Check-Namen, Event-Triggern, Runnern, Sicherheitsgrenzen, Branch-Rulesets, Tests oder Analyzer-/Formatter-Regeln. Der PR-Status-Signal-Workflow bleibt separat und unverändert.

Der JobState-Probe referenziert das CLI-Projekt. Pro Linux-/Windows-Job wird deshalb der gemeinsame Projektgraph einmal explizit restored. CLI-Release-Build, Format-/Analyzer-Check und späterer Probe-Run nutzen die vorbereiteten Assets ohne weiteren Restore des jeweiligen Hauptgraphen. Der Probe-Run baut weiterhin den Testcode; isolierte Testworktrees, Patch-Helper und unabhängige Sample-/Gastprojekte behalten ihre eigenen Restores und Verifikationen. Keine Wiederverwendung untrusted Buildausgaben über Jobs oder Runs.

Arbeitsregel: zusammengehörige Implementierung, Tests und Dokumentation in einem konsistenten Commit bündeln; unveränderliche Inhalte pro SHA innerhalb eines Arbeitsauftrags wiederverwenden; CI-Status bei Bedarf abrufen statt engmaschig zu pollen. Alle verbindlichen Gates bleiben auf dem aktuellen PR-Stand vor der menschlichen Mergefreigabe erforderlich. Messbasis: CI 45608368246 (vor Optimierung); danach Runner-Zeit, Anzahl abgebrochener/fehlgeschlagener Läufe und Restore-Aufrufe anhand vergleichbarer Workflows beobachten. NuGet-Caching, selektive Testausführung und weitergehende API-Optimierung erst bei gemessenem Bedarf und separatem Nachweis; keine neuen Caches oder zusätzlichen CI-Workflows auf Vorrat. Keine Abschwächung der Vollabnahme.

GitHub-Referenz: https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency .

### Kontrollierter Windows-Build-Server-Shutdown (Issue #19, Folge aus M5c S01)

CI 45613428906, Windows-Job 139202580830: letzter Regressionstest beendet 12:28:15.789 UTC, Post-Cleanup ab 12:28:16.011, Orphan-Cleanup 12:28:18.673, letzter protokollierter Prozessabbruch 12:28:18.736. Vier verbleibende Prozesse (MSBuild, VBCSCompiler, zwei conhost) wurden vom Runner bereinigt. Der protokollierte Zeitraum belegt keinen minutenlangen Cleanup; die verzögerte Übersichtsanzeige bei bereits erfolgreichem API-Status ist ein separates, nicht abschließend diagnostiziertes Problem. Keine Zusage einer schnelleren UI-Aktualisierung.

Der Windows-Job führt nach allen Tests `dotnet build-server shutdown` als letzten regulären Schritt aus (`if: always()`, maximal eine Minute). Build-Server während der Prüfungen weiterverwenden; kein globales Abschalten, kein pauschales Beenden fremder Prozesse und kein Abschalten der GitHub-eigenen Orphan-Bereinigung. Kein continue-on-error; bestehende Prüfschritte bleiben unverändert. Bei fehlgeschlagenem Shutdown tatsächlichen Exitcode prüfen. Herstellerreferenz: https://learn.microsoft.com/dotnet/core/tools/dotnet-build-server . Projektinhaber hat die Ergänzung am 25.11.2026 freigegeben; im bestehenden M5c-Draft #71 zusammen mit dem ohnehin erforderlichen nächsten Review-Stand prüfen. Vorherige erfolgreiche Bridge-CI bleibt als Evidenz erhalten.

## Zentrale .NET-Build- und Paketkonfiguration

Projektinhaber hat den Zwischenschritt zwischen M5c S03 und S04 am 25.11.2026 bestätigt (Issue #70). Repositoryweite `Directory.Build.props` enthält die bereits identischen vier Build-Eigenschaften (net11.0, ImplicitUsings, Nullable, TreatWarningsAsErrors). `Directory.Packages.props` aktiviert NuGet Central Package Management und enthält genau die bisher 18 direkt referenzierten Paketversionen; Transitive Pinning bleibt deaktiviert. Die exakte native SQLite-Referenz [3.54.1] bleibt unverändert und nur vom JobState.Probe angefordert. Keine Paketupdates, zusätzlichen References, Targets, SDK-Wechsel oder Änderung am Anwendungs-/Testcode. `OutputType`, Ressourcen, InternalsVisibleTo und ProjectReference bleiben projektbezogen. `global.json` und .editorconfig unverändert.

Die vier bestehenden Sample-Projektdateien bleiben bewusst byteidentisch: Sie deklarieren ihre Build-Eigenschaften selbst und besitzen keine PackageReferences. MSBuild importiert gemeinsame Props beim Build im Repository; die expliziten, identischen Eigenschaften der Samples bleiben wirksam. Kopierte bzw. historische Sample-/Sandbox-Verträge dürfen dagegen nicht vom Vorhandensein der Repository-Props abhängen. Bei künftigen Samples mit Paketbedarf muss der isolierte Build-Kontext explizit geprüft werden, bevor Versionsangaben entfernt oder zentrale Importe übernommen werden. CPM wirkt nicht als Paketinstallation; kein globaler PackageReference. Vorherige S03-Referenz: CI 45623052441 auf 525c5b7489dc0a4c269214cf13d720036ef7eb48, SQLite 3.54.1. Abnahme: statisch gleiche direkte IDs und Versionen, Linux/Windows bestehende CI einschließlich Restore/Graph, Samples, Offline-Sandbox-/Recovery-Regression; aufgelösten Graph mit dem Referenzlauf vergleichen. Keine zusätzliche Matrix; kein Merge ohne Freigabe.

Herstellerreferenzen: https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory ; https://learn.microsoft.com/nuget/consume-packages/Central-Package-Management .

### Eigenständige temporäre Guest-Projekte

CI 45623052441 (#309) meldete CS8632 zweimal für dieselbe Nullable-Annotation in HostIsolationProbe.cs.txt (Program.cs Zeile 77, GuestProbe.csproj unter RUNNER_TEMP). Die temporären Projekte außerhalb des Repositorys importieren dessen Directory.Build.props nicht. Beide Erzeugungspfade (Windows-Offline-Compilerprüfung in dotnet.yml und Invoke-HaMiSandboxAcceptance.ps1) deklarieren deshalb Nullable=enable und TreatWarningsAsErrors=true explizit, analog zu den bestehenden vier gemeinsamen Build-Eigenschaften. Keine NoWarn-/Suppressionsregel, keine Änderung am Probe-Code, SDK oder Paketgraph. Der beabsichtigte CS0103-Negativfall bleibt ein Compilerfehler. Abnahme: bestehender Windows-Compile-Step ohne CS8632, übrige CI unverändert grün; reale Sandbox-Abnahme erst im genehmigten Kontext. Issue #70 / Qualitätsgates #19.

## Entwicklungs-CI und verbindliche Freigabe-CI (Issue #19)

Projektinhaber bestätigt am 25.11.2026 selektive Entwicklungsprüfungen und vollständige Abnahme vor Integration. Die vorhandenen zwei Job-Suites bleiben inhaltlich unverändert. Draft ohne besondere Kennzeichnung: Linux. Draft mit Label `ci:windows`: Windows statt Linux. Draft mit `ci:full`: beide (Vorrang vor ci:windows). Plattformübergreifende Paket-/SDK-/Sicherheitsänderungen gezielt auf beiden prüfen. Kennzeichnung gilt beim nächsten PR-Commit/Reopen; keine CI auf allgemeinen Label-Ereignissen, damit Board-Automation keine zusätzlichen Runs auslöst. Labels bei Bedarf im Repository anlegen; Workflow selbst hat keine Label-/Schreibrechte. Manueller workflow_dispatch bietet linux/windows/both (Standard both), dient nur Diagnose. Auf einem reviewbereiten PR laufen unabhängig von Labels immer beide Suites. ready_for_review und converted_to_draft explizit getriggert; neuester PR-Event gewinnt, manuelle Läufe bleiben unabhängig.

**Freigabegrenze:** GitHub behandelt übersprungene Jobs als erfolgreiche Pflichtchecks. Daher verwenden Draft-/manuelle Jobs eigene Namen (Draft Linux build, Draft Windows regression / Manual Linux build, Manual Windows regression), und die internen Job-IDs sind ebenfalls nicht identisch mit den Pflichtcheck-Namen. Ausschließlich der Nicht-Draft-PR-Ereignispfad publiziert `build` und `Windows file-handle logging regression`, beide unconditionally für diesen Pfad. Das aktive Ruleset protect-main 27473232 (GitHub Actions integration 15368, keine Bypasses) bleibt unverändert. Kein zusätzlicher Gate-Runner, kein Skip eines Review-Pflichtjobs, keine neue Berechtigung oder pull_request_target. Eine bereits vollständig geprüfte identische Revision kann vorhandene gültige Pflichtchecks behalten; vor Merge aktuellen Head, Base und tatsächliche erfolgreiche vollständige PR-Jobs prüfen. Kein Anspruch auf Schutz gegen eigenmächtige Änderungen am Workflow durch bereits schreibberechtigte Akteure; bestehende menschliche Workflow-Review bleibt verbindlich.

Abnahme: Draft-Run muss tatsächlichen Linux-Erfolg, Windows skipped ohne Pflichtcheck-Namen und fehlende Pflichtchecks auf neuer Revision zeigen. Windows-only/full draft und manuelle Auswahl bei konkretem Bedarf prüfen; die reguläre spätere Ready-for-review-Vollabnahme muss beide alten Pflichtcheck-Namen mit tatsächlichen erfolgreichen Jobs zeigen, nicht skipped. Keine zusätzlichen Test-PRs oder Live-Ausführungen nur für diesen Zwischenschritt. Statische Fallmatrix (Draft default / Windows / full / beide Labels, Review mit beliebigen Labels, manual linux/windows/both) und unveränderte Steps geprüft; tatsächliche GitHub-Namens-/Skip-Semantik und Merge-Gate bleiben bis Live-Nachweis offen. CI #309 Messbasis 2m36s Linux, 3m14s Windows, 3m18s Gesamt. Rechnerische Runner-Ersparnis bei Linux-only 3m14s, nicht garantierte Wandzeitersparnis; Queue/Runner-Konkurrenz separat beobachten. Quellen: https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-jobs-with-conditions ; https://docs.github.com/en/actions/reference/workflows-and-actions/contexts ; https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows .


### Inkrementelle Testauswahl innerhalb der Draft-Linux-Suite (Issue #19)

Projektinhaber bestätigt 25.11.2026: gezielte Unit-/Feature-/Integrationsprüfungen während der Entwicklung, vollständige Regression und reale Meilensteinabnahme an den bisherigen Gates. Keine neue Testbibliothek und keine komplexe automatische Abhängigkeitsanalyse. Erster konservativer Pilot: `scripts/quality/select-draft-tests.py` klassifiziert ausschließlich den tatsächlichen synchronisierten Push-Bereich eines Draft-PRs. Prüft before/after/base SHA, after = PR head, exakte Checkout-Merge-Eltern, Fast-forward und unveränderte Merge-Basis; NUL-separierter Diff ohne Rename-Erkennung. Nur eine explizite Liste eng begrenzter Order-Ledger-/Kandidaten-/Budget-/Probe-/Dokumentationspfade erlaubt `order-recovery`; jede andere Datei (insbesondere gemeinsame Store-/Scope-/Sicherheitskomponenten, Projekt-/SDK-/Paket-/Workflow-Dateien), leere/unklare Änderung, fehlende Git-Historie oder ungültige Event-Daten ergibt `full`. Kein Heuristik-Skip aufgrund eines Commit-Titels. Kein Anspruch auf vollständige Test Impact Analysis, keine Aussage über seit der letzten vollständigen Abnahme kumulierte Änderungen. Die vollständige Review-CI bleibt zwingend.

Order-recovery führt weiterhin Restore/NuGet-Audit/Graph, Release-Build, Format/Analyzer, Formatter-Regression, Vertrags-/Worktree-Selftest, CLI/Akka/Order/Budget/Security-Selftest, echte Prozess-Recovery-Probe, production-verify Contract-Gate und CLI-Help aus. Nur die 18 eigenständigen Linux-Schritte für Sandbox, Replay, Workflow, Config und Coding werden zurückgestellt. Alle bisherigen Testbefehle bleiben byteidentisch, Windows-Suite komplett unverändert. Full bei neuem/reopened/converted/ready PR, manueller Diagnose, ci:full oder nicht erkanntem Profil; Nicht-Draft und ci:full explizit in den Step-Conditions abgesichert. Kein neuer Job/Permission/Required-Check, keine Änderung Ruleset. Selbsttest des Selektors läuft bei jedem Linux-Lauf; erste Änderung des Workflows selbst muss full auswählen. Live-Nachweis beim nächsten passenden fachlichen Push (kein künstlicher Commit nur für CI); dann tatsächliche ausgeführte/skipped Steps und Runner-Zeit mit CI #319 vergleichen. Vollständige Linux/Windows-Abnahme bei Ready-for-review bleibt offen. Referenz: https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows .
