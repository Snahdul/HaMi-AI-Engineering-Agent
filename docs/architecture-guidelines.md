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

**Grenzen der aktuellen Implementierung:** Der schlanke C#-Loader verarbeitet aus dem Prompty-Format **nur** die verifizierte Teilmenge YAML-Frontmatter plus genau eine `system:`- und eine `user:`-Nachricht sowie `{{feedback}}`. Er ist **kein allgemeiner Prompty-/Jinja2-Interpreter**. Zusätzliche Template-Konstrukte und Modell-/Tool-Konfigurationen werden zurückgewiesen. Die Sicherheitsregeln (erlaubte Pfade, Tool-Verbot, Schema, Modellbudgets, Worktree-Isolation) sind weiterhin fest im Host kodiert und dürfen nicht durch Prompt-Metadaten überschrieben werden.

**Verbindlich für kommende Slices:**
- Ein Prompt ist ein eigenständiges, überprüfbares **Feature-Asset**. Der Code lädt es über einen kleinen Adapter; keine mehrzeiligen Prompts als `const string` im C#-Quelltext.
- Variable Eingaben erfolgen über benannte Platzhalter mit geprüftem Eingabevertrag. Compiler-/Test-Feedback ist **unvertraute Eingabedaten**, keine neue Systeminstruktion.
- Prompt-Änderungen sind reguläre Code-Änderungen: Git-Diff, Review und Offline-Tests für Parsing, Rollen, erforderliche Variablen und Fehlerfälle.
- Prompt-Metadaten dürfen **keine** zusätzlichen Modell-Tools, Berechtigungen, Schlüssel, Endpunkte oder Dateisystemzugriffe freischalten.
- Die verbliebenen Inline-Prompts der historischen Replay-/Workflow-Slices werden **schrittweise** in deren eigene Feature-Verzeichnisse ausgelagert; keine unkontrollierte Änderung funktionierender Modellabläufe.

**Formatquellen:** [Prompty-Spezifikation](https://prompty.ai/specification/file-format/) · [Semantic Kernel YAML Prompt Schema](https://learn.microsoft.com/en-us/semantic-kernel/concepts/prompts/yaml-schema).

## Qualitätsgates je Vertical Slice

1. **Vertrag und Architektur**: Scope, erlaubte Pfade, Modulgrenzen, Abhängigkeiten und Definition of Done prüfen.
2. **Deterministische Prüfung**: `dotnet restore/build/test`, Analyzer und Formatierung gemäß Zielprojekt; bei neuen Slice-Implementierungen mindestens ein passender positiver, negativer und Randfalltest.
3. **Review des Diffs**: Nur autorisierte Dateien; keine neuen versteckten Abhängigkeiten, Secrets, globalen Seiteneffekte oder unerwarteten Querverweise.
4. **Begrenzte Korrekturschleife**: Compiler-, Test- und Analyzerfehler als Feedback; keine automatische Lockerung von Akzeptanzkriterien oder Sicherheitsregeln.
5. **Isoliertes Ergebnis**: Worktree auf I: (ReFS), nachvollziehbarer Bericht; Integration nach `main` ausschließlich nach menschlicher Entscheidung.

**Aktueller Stand:** MVP 0.3 prüft bereits echte C#-Builds und feste Tests für einen eng begrenzten Ausdruck. Das optionale `IChatClient`-Experiment aus PR #20 ist ein kleiner Schritt zur austauschbaren Modellanbindung. Allgemeine Codegenerierung, Analyzer-/Format-Gates und sichere Ausführung beliebigen generierten Codes sind damit **noch nicht implementiert**.
