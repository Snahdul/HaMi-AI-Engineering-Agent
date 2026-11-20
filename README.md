# HaMi AI Engineering Agent

**Agentic software engineering with .NET 11, driven by documented development workflows.**

Die Methodik und ihre Erkenntnisse werden im eigenständigen [HaMi AI Engineering](https://github.com/Snahdul/HaMi-AI-Engineering) dokumentiert. Dieses Repository entwickelt eine ausführbare agentische Umsetzung dieser Arbeitsweise.

**Projektsteuerung:** [HaMi AI Engineering Agent – GitHub Project #2](https://github.com/users/Snahdul/projects/2/views/1) · [Statusautomatisierung und Einrichtung](docs/project-board.md).


**Architekturleitlinien:** [Modularität, Vertical Slices und Prompt Assets](docs/architecture-guidelines.md). Neue Modellprompts werden als eigenständige `.prompty`-Dateien mit YAML-Metadaten und lesbarem Prompttext versioniert.

## Dauerhafte Laufzeitkonfiguration auf HaMi11 (Windows)

[Issue #17](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/17) vereinheitlicht den Worktree-Pfad und die OpenAI-Key-Auflösung für die Replay-, Workflow- und Coding-Modi.

Einmalig **als angemeldeter Windows-Benutzer** in PowerShell einstellen:

```powershell
# Der Root-Ordner liegt auf I: (ReFS), als Geschwister des produktiven Checkouts.
[Environment]::SetEnvironmentVariable(
    'HAMI_AGENT_WORKTREE_ROOT',
    'I:\git\HaMi\_agent-worktrees',
    'User'
)

# Nur falls der API-Schlüssel noch NICHT unter diesem Namen gespeichert ist:
# $secret = Read-Host 'OpenAI API-Schlüssel' -AsSecureString
# [Environment]::SetEnvironmentVariable(
#     'HaMi-AI-Engineering-Agent-Key',
#     [System.Net.NetworkCredential]::new('', $secret).Password,
#     'User'
# )
```

Der Schlüsselwert darf weder in PowerShell-Quelltexten, Git, Issues, CI noch in Logs stehen. Eine Windows-Benutzerumgebungsvariable ist **nicht verschlüsselt** und ersetzt keinen Credential-Manager.

**Konfiguration prüfen – offline und ohne Schlüsselanzeige:**

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- config --check --repo .
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- config --self-test
```

Die CLI liest den API-Schlüssel in dieser Priorität: bereits vorhandenes prozesslokales `OPENAI_API_KEY`, anschließend `HaMi-AI-Engineering-Agent-Key` im Prozess bzw. unter Windows im Benutzer- oder Maschinenkontext. Es wird **kein Schlüsselwert** angezeigt. Der benutzerdefinierte Worktree-Root kommt aus `HAMI_AGENT_WORKTREE_ROOT` (Prozess → Windows User → Machine), sonst aus dem bisherigen systemseitigen Temp-Verzeichnis. Ein relativer Pfad und ein Root **innerhalb des produktiven Repository-Checkouts** werden abgelehnt.

Damit ist keine globale Änderung von `TEMP`/`TMP` mehr erforderlich. Für HaMi11 muss `I:` beim Start vorhanden und schreibbar sein. Das Setzen der Benutzerumgebungsvariable allein verschiebt **keine** früheren Worktrees.

## MVP 0.1 – erster historischer Replay-Durchstich

- [Issue #5: Referenzfall](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/5) – ein bereits abgeschlossenes öffentliches Issue, mit getrenntem Ausgangs- und Ergebnisstand
- [Issue #6: Implementierung](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/6) – begrenzte .NET-11-CLI mit einem KI-Modell
- Ein einziger Replay-Fall mit genau drei erlaubten Ausgabedateien, kein generischer Tool- oder Workflow-Agent
- Der historische Lösungs-PR wird erst nach einem autonomen Versuch für den Ergebnisvergleich herangezogen
- **Keine** automatischen GitHub-Schreibvorgänge, Merges oder Deployments

### Voraussetzungen auf HaMi11 (Windows 11)

- Git sowie das .NET 11 SDK (siehe `global.json`)
- Eine eigene OpenAI-API-Berechtigung mit Guthaben (separat von ChatGPT Plus)
- Der aktuelle Repository-Checkout auf `main` (enthält die freigegebene MVP-Implementierung)

### Erster lokaler Lauf (PowerShell)

```powershell
git clone https://github.com/Snahdul/HaMi-AI-Engineering-Agent.git
cd HaMi-AI-Engineering-Agent
git switch main

dotnet --version
dotnet build src/HaMi.Agentic.Cli/HaMi.Agentic.Cli.csproj --configuration Release
dotnet run --project src/HaMi.Agentic.Cli -- --help

# Nach einmaliger Windows-Konfiguration kein erneutes Einlesen des Schlüssels:
dotnet run --project src/HaMi.Agentic.Cli -- config --check --repo .
dotnet run --project src/HaMi.Agentic.Cli -- --repo . --model gpt-5.4-mini

# Alternative für einen lokal unbeaufsichtigten Replay (keine GitHub-Schreibrechte):
# dotnet run --project src/HaMi.Agentic.Cli -- --repo . --model gpt-5.4-mini --unattended
```

Die CLI prüft den Repository-Ursprung und den historischen Commit, erstellt einen separaten lokalen **detached Worktree** unter dem konfigurierten Root (sonst im temporären Verzeichnis) und sendet ausschließlich den kuratierten Auftrag und die drei ursprünglichen öffentlichen Quellvorlagen an das Modell. **Standardmäßig** fordert sie vor dem Schreiben weiterhin ausdrücklich `JA`. **Optional** überspringt `--unattended` nur diesen lokalen Freigabepunkt, wenn `--repo` explizit gesetzt wurde, die fachliche Validierung erfolgreich ist und der abgetrennte historische Worktree sauber auf dem erwarteten Commit steht. Es werden ausschließlich die drei allowlisteten Dateien dort geschrieben; kein Push, Merge, Deployment und keine Ausführung von Modell-Shellbefehlen.

Sie validiert die drei erzeugten Dateien auf YAML-Struktur und fachliche Pflichtabschnitte. Bei Fehlern bleibt der historische Worktree unverändert. Ergebnis und Prüfstatus werden ausschließlich lokal in `report.json` festgehalten, einschließlich `approvalMode` (`interactive` oder `unattended-isolated`) und `modelCalls`. **Der historische MVP-0.1-Replay wurde bereits in [Issue #5](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/5) technisch und fachlich geprüft und abgeschlossen.** Diese Bewertung gilt für den engen Referenzfall und ersetzt keinen Test allgemeiner autonomer Programmieraufgaben.

### Fachliche Validierung ohne zusätzlichen Modellaufruf

Das Ergebnis eines vorherigen Replays kann direkt im **isolierten historischen Worktree** geprüft werden:

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- --check "C:\<lokaler-Pfad-zum-Replay>\worktree"
```

Dieser Modus liest ausschließlich die drei erwarteten Dateien und benötigt weder `OPENAI_API_KEY` noch einen neuen Modellaufruf. Er kontrolliert nun zusätzlich:
- Verlaufs- und Ergebnisfelder dürfen bei Issue-Erstellung **nicht** verpflichtend sein.
- Das PR-Template muss Abhängigkeiten/ADRs und den historischen Replay-Ergebnisvergleich explizit dokumentierbar machen.

Die Prüfung folgt fachlichen Regeln aus dem historischen Referenzfall, verlangt aber keinen identischen Wortlaut. Sie ist keine vollständige semantische Qualitätsbewertung und ersetzt keinen GitHub-UI-Test.

Bei einem **neuen** Replay ruft die CLI bei Validierungsfehlern das Modell **höchstens einmal zusätzlich** mit konkreten Prüfbefunden auf (maximal zwei kostenpflichtige Modellaufrufe). Erst eine valide Antwort wird zur lokalen Schreibfreigabe angeboten. Die historische Lösung wird dabei nicht als Modellkontext verwendet.

### Bewusste Grenzen von MVP 0.1

- Der Modellkontext ist auf vorab festgelegte Dateien beschränkt; das Modell besitzt keine selbstständigen GitHub- oder Shell-Tools.
- Der Vergleich mit dem historischen PR ist für die manuelle Evaluation vorgesehen, aber noch **nicht automatisiert**.
- Ein Build/Smoke-Test im GitHub-Workflow benötigt **keinen** API-Schlüssel und führt **keinen** echten Agenten-Replay aus.
- Erst nach erfolgreichem Durchstich werden zusätzliche Fähigkeiten anhand der beobachteten Probleme implementiert.

## MVP 0.2 – zwei aufeinanderfolgende lokale Arbeitsschritte

[Issue #13](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/13) erweitert die CLI um den eigenständigen Befehl `workflow`. Das bisherige historische `ReplayCommand` bleibt unverändert.

Ein versioniertes JSON-Manifest unter [`workflows/mvp02.json`](workflows/mvp02.json) beschreibt zwei geordnete Markdown-Aufgaben und ihre festen Ausgabepfade. Schritt 2 erhält den **validierten Inhalt** des ersten Schritts. Pro Schritt erfolgt bei Validierungsfehlern höchstens **ein** zusätzlicher Modellaufruf (insgesamt maximal vier). Erst wenn beide Schritte erfolgreich sind, werden genau zwei Dateien in einem neuen temporären detached Worktree unter `docs/agent-generated/` geschrieben. **Kein** Push, Merge, Deployment oder Ausführen von Modellcode.

Ohne API-Schlüssel lokal prüfen:

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- workflow --repo . --manifest workflows/mvp02.json --validate
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- workflow --self-test
```

Echten, kostenpflichtigen Ablauf nur auf HaMi11 mit konfiguriertem API-Schlüssel starten:

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- workflow --repo . --manifest workflows/mvp02.json --unattended --model gpt-5.4-mini
```

Berichte liegen ausschließlich lokal unter dem konfigurierten Worktree-Root in `hami-agent-workflows\<lauf>\report.json` (ohne Konfiguration unter `%TEMP%`); es werden keine Modellinhalte, API-Schlüssel oder lokalen Artefakte nach GitHub übertragen. Der unabhängige Workflow-Durchstich ist zunächst **nur für Markdown-Artefakte** vorgesehen. Er ist weder ein generischer Softwareentwicklungsagent noch eine Lizenz zur automatischen Bearbeitung produktiver Quelltexte.

## MVP 0.3 – echter, begrenzter C#-Build/Test/Korrektur-Zyklus

[Issue #15](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/15) führt einen eigenständigen `coding`-Befehl ein. Als konkretes erstes Beispiel dient die feste [.NET-11-`ClampKata`](samples/ClampKata/): eine Methode `Clamp(int value, int min, int max)` mit 14 Grenzfällen und 128 deterministischen Testwerten.

Der Agent erhält **ausschließlich** den Auftrag, den C#-Ausdruck hinter `=>` zu erzeugen. Vollständige Modell-Dateien, andere Bezeichner, API-/Methodenaufrufe, Statements, Imports und Shell-Befehle sind gesperrt. Der restliche Quellcode und das Testprogramm sind vertrauenswürdige, versionierte Repository-Dateien. Der Agent darf nur `samples/ClampKata/Clamp.cs` im **neu angelegten detached Worktree** verändern.

Für jeden der höchstens drei Modellversuche folgen Ausdrucksprüfung, echtes `dotnet build` und gegebenenfalls ein echter `dotnet run --no-build` mit dem festen Testprogramm. Testfehler werden in den nächsten Modellaufruf zurückgespielt; bei vollständigem Scheitern wird die ursprüngliche Quelltextdatei im Worktree wiederhergestellt. Kein Modellaufruf darf selbst ein Programm oder einen Befehl auswählen.

**Kostenloser Offline-Selbsttest** (benötigt .NET 11, aber keinen API-Schlüssel):

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --self-test --repo .
```

Er prüft mit dem echten .NET-Compiler und 142 Tests zuerst eine kompilierbare, aber **falsche** Lösung und danach die korrekte Korrektur. Zusätzlich testet er verbotene Syntax und Wiederherstellung nach ausgeschöpften Versuchen.

**Echter Coding-Durchlauf auf HaMi11** (mit vorhandenem API-Schlüssel, maximal drei kostenpflichtige Modellaufrufe):

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --repo . --unattended --model gpt-5.4-mini
```

Der Bericht liegt ausschließlich lokal unter dem konfigurierten Worktree-Root in `hami-agent-coding\<lauf>\report.json` (ohne Konfiguration unter `%TEMP%`); der erfolgreiche Worktree enthält eine geänderte `samples/ClampKata/Clamp.cs`, während das produktive `main` unverändert bleibt.

**Sicherheitsgrenze:** Das ist **kein frei programmierender Agent**. `dotnet build` und `dotnet run` sind keine OS-Sandbox für beliebigen KI-Code. Die Ausführung ist hier nur vertretbar, weil die Modellantwort auf einen kurzen C#-Ausdruck ohne Methodenaufrufe und mit einer festen, bekannten Testanwendung begrenzt wird. Für allgemeine Codegenerierung ist eine gesonderte Prozess-/Betriebssystem-Isolation erforderlich.

## MVP 0.6 – zweiter begrenzter Coding-Auftrag am Agenten selbst

[Issue #28](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/28)
erweitert die fest kodierte Task-Allowlist um `retry-budget`. Anders als
die ursprüngliche `ClampKata` bearbeitet dieser Auftrag **wirklichen
Produktionscode des Coding-Agenten**:
[`RetryBudget.HasAnotherAttempt`](src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs).
Die Regel wird für die protokollierte Entscheidung verwendet, ob ein
weiterer Korrekturversuch möglich ist. Der Host erzwingt die maximale
Modellaufrufzahl unabhängig davon weiterhin mit seiner eigenen Schleife.

Der Generator darf **ausschließlich einen kurzen C#-Ausdruck** in dieser
vorab definierten Methode im isolierten Git-Worktree ändern. Die feste
[RetryBudget-Testsuite](samples/RetryBudget/) bindet das echte
Produktions-Quelltextfile über `Compile Include` ein und prüft die
negativen und positiven Grenzen. Ein gesondertes [Prompty-Asset](prompts/coding/RetryBudget.prompty)
und eine [Execution Policy](policies/coding/retry-budget.policy.json)
definieren die auf diesen Task begrenzte Eingabe und Berechtigungen.

**Ohne API-Kosten** – beide Task-Verträge, echte Compiler-/Testkorrekturen
und Rollback prüfen:

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --self-test --repo .
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --policy-check --task retry-budget --repo .
```

**Optionaler Live-Durchstich auf HaMi11 nach Merge und Freigabe** (maximal
drei kostenpflichtige Modellaufrufe):

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --task retry-budget --repo . --unattended --ai-extensions --model gpt-5.4-mini
```

Ohne `--task` bleibt `clamp-kata` der Default. Fremde Task-IDs, fremde
Dateipfade, unbekannte Identifier, Modelltools und freie Shell-/GitHub-
Schreibvorgänge werden weiterhin abgelehnt. Der lokale `report.json`
dokumentiert Task-ID, Prompt-/Policy-Fingerabdruck, Ergebnis und
den exakt bearbeiteten Pfad. Das produktive `main` wird **nicht**
automatisch verändert.

**Abgrenzung:** Zwei ausdrücklich zugelassene Expressions sind noch
kein allgemeiner C#-Entwicklungsagent. Beliebiger KI-generierter
Programmcode darf auf HaMi11 nicht ohne zusätzliche OS-Sandbox
ausgeführt werden.

## MVP 0.5 – Microsoft.Extensions.AI als optionaler Modelltransport

[Issue #19](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/19) führt `Microsoft.Extensions.AI` und `Microsoft.Extensions.AI.OpenAI` **11.0.0** ein. Der Coding-Modus kann optional über `--ai-extensions` mit einem injizierten `IChatClient` arbeiten; der bisherige direkte Responses-Transport bleibt der Standard.

**Prompt-Verwaltung:** Beide Coding-Transporte laden den versionierten Prompt des ausgewählten Tasks (Standard: [`ClampExpression.prompty`](prompts/coding/ClampExpression.prompty), zweiter Task: [`RetryBudget.prompty`](prompts/coding/RetryBudget.prompty)). Der Datei-Loader verarbeitet bewusst nur die benötigte Prompty-Teilmenge (YAML-Metadaten, `system:`/`user:`, `{{feedback}}`), lehnt nicht unterstützte Ausdrücke ab und wird im Offline-Selbsttest geprüft. Modellberechtigungen und Ausführungslimits werden über die vertrauenswürdige, versionierte [Execution Policy](policies/coding/clamp.policy.json) konfiguriert, vor dem Lauf strikt validiert und im C#-Host durchgesetzt. Historische Replay-/Workflow-Prompts werden separat migriert.

Der optionale Adapter überträgt dieselben Begrenzungen wie bisher: striktes JSON-Schema für `plan` und `expression`, feste Token-Grenze, **keine Tools**, maximal drei Modellversuche und beim OpenAI Responses SDK ausdrücklich `StoredOutputEnabled = false` (entspricht `store=false`). Bei jeder Modellantwort bleiben die bestehenden C#-Ausdrucksregeln, der echte Compiler/Test-Harness und die Abbruch-/Rücknahmelogik unverändert. Der verwendete Transport wird im lokalen `report.json` ausgewiesen.

**Deklarative Ausführungsrichtlinie:** Der Coding-Slice liest [`policies/coding/clamp.policy.json`](policies/coding/clamp.policy.json) mit `Microsoft.Extensions.Configuration.Json` und strengem typisierten Binder (`ErrorOnUnknownConfiguration`). Dort stehen erlaubte Schreibpfade und Modell-Tools, GitHub-/Shell-Berechtigungen, API-Speicherung und die Limits für Aufrufe, Token und Prozess-/API-Dauer. Ein ungültiger, fehlender oder über die geprüften Fähigkeiten hinausgehender Eintrag stoppt **vor** API- oder Worktree-Zugriff. Sicherheitswerte lassen sich nicht per Prompt oder Umgebungsvariable überschreiben. Laufberichte enthalten künftig den Policy-Pfad und SHA-256.

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --policy-check --repo .
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --self-test --repo .
```

Diese Befehle prüfen die Policy ohne API-Kosten. Die technischen Schranken der zwei explizit erlaubten Coding-Tasks bleiben bestehen; eine Konfigurationsänderung kann nicht die Ausführung beliebigen Codes oder Shell-/GitHub-Tools freischalten. Siehe [Architekturleitlinien](docs/architecture-guidelines.md).

**Kostenloser Offline-Test (Fake-IChatClient, kein Modellaufruf):**

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --self-test-ai --repo .
```

**Optionaler Live-Kompatibilitätstest** (erst nach CI/Review/Merge; bis zu drei kostenpflichtige Modellaufrufe):

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --repo . --unattended --ai-extensions --model gpt-5.4-mini
```

**Migrationsgrenze:** Microsoft markiert den `ResponsesClient.AsIChatClient()`-Adapter noch als experimentell (`OPENAI001`). Deshalb bleibt der bisherige produktiv getestete Transport bis zu einem erfolgreichen echten Kompatibilitätstest unangetastet; das Experimental-Warning wird nur direkt an der betreffenden Factory lokal unterdrückt. Das ist **keine** Freigabe für beliebigen generierten C#-Code oder erweiterte Tool-Ausführung.

### Indirekte Prompt Injection bei Diagnosedaten

[Issue #21](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/21)
führt die erste praktische Vertrauensgrenze ein: Compiler-, Test- und
Validierungsfeedback bleibt **untrusted data**, auch wenn es gefälschte
`system:`-Rollen oder Handlungsanweisungen enthält. Der Coding-Slice
übergibt solche Inhalte nicht mehr als freien Prompttext, sondern
als kodiertes JSON-Datenfeld (`source`, `truncated`, `text`).
Das Größenbudget `maxFeedbackCharacters` ist in
[`policies/coding/clamp.policy.json`](policies/coding/clamp.policy.json)
konfigurierbar. Überlange Diagnosen werden abgeschnitten.

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --self-test --repo .
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --self-test-ai --repo .
```

Die kostenlosen Offline-Tests enthalten adversariale Diagnoseinhalte und
prüfen, dass diese als JSON-Daten erhalten bleiben, ohne neue System-/
User-Rollen zu erzeugen. **JSON-Kodierung ist kein vollständiger Schutz**
gegen inhaltliche Prompt Injection. Entscheidend bleiben die vom Modell
unabhängigen Grenzen: keine Modell-Tools, nur erlaubte Zieldateien, feste
Compiler-/Testbefehle und deterministische Validierung. Siehe
[Prompt-Injection-Bedrohungsmodell](docs/prompt-injection-threat-model.md).

## Fachliche C#-15-Union-Ergebnisse im Coding-Slice

[Issue #19](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/19)
und [PR #23](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/pull/23)
verbessern die Lesbarkeit der internen Coding-Ergebnisse:

- `SourceValidationResult`, `CompilationResult`, `TestExecutionResult`:
  jeweils genau einer der typisierten fachlichen Ausgänge.
- `CodingAttemptResult`: genau einer von **Ausdruck abgelehnt,
  Build fehlgeschlagen, Tests fehlgeschlagen, erfolgreich**.
- `CodingRunResult`: erfolgreich oder Versuchslimit erschöpft.
- `CodingRunReport`: ausdrücklich separates Berichtsmodell. Die
  `report.json`-Felder inklusive bisheriger Status-Strings und boolescher
  Versuchsfelder bleiben **unverändert** für vorhandene Auswertungen.

Ein gemeinsamer `Result<T>`-Typ ist **nicht** vorgeschrieben. Unions
bleiben zunächst im Coding-Vertical-Slice; weder `IChatClient` noch die
anderen Features müssen dafür ihre Schnittstellen ändern.

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --self-test --repo .
```

Der Offline-Test deckt alle Union-Fälle, Report-Kompatibilität, reale
Compiler-/Testkorrekturen und Rollback ab. Details in den
[Architekturleitlinien](docs/architecture-guidelines.md).

## Strukturiertes Logging und Entscheidungsbegründungen

[Issue #24](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/24)
führt für den Coding-Slice ein source-generiertes
`Microsoft.Extensions.Logging`-Logging mit Serilog-Provider und
kompakten rotierenden JSON-Dateien ein.

In der versionierten [`config/logging.json`](config/logging.json) gelten
folgende **unabhängigen Defaults**:

- `logSuccessfulSteps: false`: Nur Fehler/Warnungen, keine normalen Erfolgseinträge.
- `logDecisionSummaries: true`: Kurze, ausdrücklich verfasste
  Annahmen, Entscheidungen und Begründungen zusätzlich protokollieren.
- `minimumLevel: Information`: Entscheidungslogs werden nicht weggefiltert.

Logdateien entstehen außerhalb des produktiven Checkouts im
`HAMI_AGENT_WORKTREE_ROOT/hami-agent-logs`-Ordner.
`RunId` korreliert Ereignisse; keine Rohprompts, Schlüssel,
Modellantworten, Quellcodes oder vollständigen Fehlerdiagnosen im Log.
Die fachlichen `report.json`-Dateien behalten ihr bisheriges Format.

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- coding --self-test-logging --repo .
```

Der Offline-Test prüft beide Schalter getrennt, die JSON-Struktur,
die Korrelation und ungültige Konfiguration. Weitere Informationen:
[Logging- und Entscheidungsnachweise](docs/logging.md).

## Sicherheit

Öffentliche Informationen und erlaubte Werkzeuge sind durch [SECURITY.md](SECURITY.md) begrenzt. Der Mensch bleibt für die Freigabe und die Integration verantwortlich.

## Development Preflight – versionierter Auftrag und kontrollierter Multi-Datei-Diff

Der separate CLI-Befehl `development` ist der **erste sichere Vorbereitungsdurchstich**
für Entwicklungsaufträge mit mehreren C#-Dateien. Er ruft **kein Modell**
auf, baut oder startet **keinen** vorgeschlagenen Code und integriert nichts.

Die verbindliche, versionierte Auftragsdatei
[`contracts/development/retry-budget-refinement.json`](contracts/development/retry-budget-refinement.json)
enthält eine explizite Task-ID, das Ziel-Framework, Zweck, Akzeptanzkriterien
und eine exakt definierte Allowlist für zwei produktive C#-Dateien.
Unbekannte JSON-Felder, fehlende Pflichtangaben, doppelte Schlüssel,
relative Pfadausbrüche oder unzulässige Dateitypen werden abgewiesen.

**Kostenlose lokale Prüfung** nach `git pull --ff-only` auf `main`:

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- development --repo . --contract-check --contract contracts/development/retry-budget-refinement.json
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- development --repo . --self-test
```

Ein später in einem isolierten, detached Worktree entstandener Änderungssatz kann
**rein lesend** gegen den auf `main` eingecheckten Vertrag geprüft werden:

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- development --repo . --worktree-check --contract contracts/development/retry-budget-refinement.json --worktree "I:\git\HaMi\_agent-worktrees\<candidate>"
```

Die Prüfung verlangt dieselbe Git-Repository-Identität, exakt den aktuellen
`main`-Commit als Baseline und einen detached Worktree außerhalb des
Hauptcheckouts. Sie akzeptiert nur explizit erlaubte Dateipfade und
zulässige Git-Änderungsarten; Löschungen, Umbenennungen, Symlink-/
Reparse-Änderungen und fremde Dateien führen zu einem Fehler. Die
CI-Selbsttests erzeugen dafür ein temporäres Git-Repository, prüfen
Positiv- und Negativfälle und verändern **nicht** den produktiven Checkout.

**Wichtige Grenze:** Dieser Scope-Check beurteilt nicht die Fachlichkeit des
Quellcodes, überprüft keine Anwendungs-API und ist keine OS-Sandbox.
Die Ausführung allgemeiner KI-generierter Programme bleibt verboten,
bis eine separate, konkret geprüfte Isolations- und Build/Test-Stufe
implementiert ist. Die bestehenden `coding`-Aufträge bleiben unverändert.

## Docker Sandboxes: lokaler RetryBudget-Smoke-Test (Issue #33)

Der neue, bewusst eingeschränkte Development-Modus `--sandbox-poc` prüft
**nur** die vier festen, vertrauenswürdigen RetryBudget-Dateien aus
dem Gitobjekt des lokalen main-Branches. Es wird kein KI-generierter
Code, kein unbekanntes Projekt und keine vom Modell bestimmte Shell
ausgeführt. Im Gast erfolgt ein Offline-Restore, Release-Build und
die bestehende Testsuite mit 268 Prüfungen.

Voraussetzungen auf HaMi11: lokale Sandbox hami-dotnet11-check,
aus Template hami-dotnet11:0.1 mit .NET SDK 11.0.100 und ICU
erzeugt; Workspace, Host-Mounts, Secrets und MCP-Server dürfen
nicht eingebunden sein. Die globale Netzwerkpolicy muss Zugriffe
auf api.github.com:443 verweigern. Der Runner prüft die vorliegenden
Einstellungen und bricht bei Abweichungen ab.

### Aufruf nach Review/Merge auf HaMi11 (main)

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- development --repo . --sandbox-poc-self-test

dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- development `
  --repo . --sandbox-poc --results-dir 'G:\HaMi\Sandbox\PoC\Runs'
```

Erzeugt pro Lauf einen temporären Host-Snapshot (vier Dateien,
aus main) und kopiert ihn ohne Mount in den Gast. Windows-Eigentümer
werden ausschließlich für diesen neuen Gastsnapshot auf agent
gesetzt. Es werden nur report.json, build.log und test.log
aus dem Gast nach results-dir/reports/hami-retry-poc-<id> abgeholt.

Der JSON-Bericht enthält getrennte Build-/Test-Exitcodes und
Laufzeit im Gast; Host-Diagnosen werden nicht ausgeführt.
Der Runner startet keine neue Sandbox und löscht die vorhandene
Test-Sandbox nicht; Kaltstart-/Lebenszyklusmessung und die
Freigabe für beliebigen generierten Code stehen noch aus.
Die CI testet den festen Guard und das Report-Schema ohne Docker.

