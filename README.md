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

- `logSuccessfulSteps: true`: Auch die wesentlichen Erfolge und Statusschritte protokollieren.
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

### Abnahme VOR dem Merge auf HaMi11 (separater PR-Worktree)

Der PoC muss bereits mit dem tatsächlich zu prüfenden PR-Code ausgeführt
werden. Er akzeptiert dafür einen **sauberen**, detached Review-Worktree,
dessen HEAD auf dem **aktuellen** lokalen `main` basiert. Der Snapshot
besteht weiterhin ausschließlich aus den fest vorgegebenen Dateien
von `refs/heads/main`. Das produktive Arbeitsverzeichnis bleibt unverändert.

```powershell
$repo = 'I:\git\HaMi\HaMi-AI-Engineering-Agent'
$review = 'G:\HaMi\Sandbox\PoC\Runner-PR34'

git -C $repo fetch origin
if ((git -C $repo rev-parse refs/heads/main) -ne
    (git -C $repo rev-parse refs/remotes/origin/main)) {
    throw 'Lokales main zuerst per git pull --ff-only aktualisieren.'
}

git -C $repo worktree add --detach $review origin/feature/issue33-sandbox-result-runner-20261120
Set-Location $review
git status --short
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- development --repo . --sandbox-poc-self-test

dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- development `
  --repo . --sandbox-poc --results-dir 'G:\HaMi\Sandbox\PoC\Runs'
```

Den Bericht unter `G:\HaMi\Sandbox\PoC\Runs\reports` und die
beiden Logs prüfen; **erst danach** PR #34 zur Mergefreigabe vorlegen.
Die Sandbox wird nicht automatisch gelöscht.

**Kein freier Build-Agent:** Der PR-Worktree enthält lediglich den zu
prüfenden Runner; die kompilierten RetryBudget-Dateien werden aus dem
geschützten Git-`main` entnommen, nicht aus PR-Dateien oder lokalen
Änderungen.

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

## HaMi Developer: beliebiger generierter C#-Code in frischer MicroVM (Issue #33)

Der neue Modus `development --sandbox-run` akzeptiert ein vorbereitetes C#-Projekt,
nicht nur den RetryBudget-PoC. Der beliebige Projektcode wird **nur innerhalb**
einer frischen, lokalen Docker-MicroVM gebaut und ausgeführt; keine Host-Shell,
keine Modell-Keys, kein Repo-Mount und kein GitHub-Schreibzugriff im Gast.

Voraussetzung: lokale Vorlage `hami-dotnet11:0.1` mit .NET 11.0.100 und ICU,
`sbx` installiert und globale Netzwerkpolicy default deny.

```powershell
Clear-Host
$repo = 'I:\git\HaMi\HaMi-AI-Engineering-Agent'
$snapshot = 'G:\HaMi\Sandbox\Candidates\demo-console'
dotnet run --project "$repo\src\HaMi.Agentic.Cli" -c Release -- `
  development --repo $repo --sandbox-run `
  --source $snapshot --project Demo.csproj --mode run `
  --results-dir 'G:\HaMi\Sandbox\PoC\Runs'
```

Modi: `build` (nur Restore/Build), `run` (zusätzlich `dotnet run`),
`test` (zusätzlich `dotnet test`). Unterprojekte als relativen
Projektpfad angeben, z. B. `src/App/App.csproj`.

Der Runner überträgt einen Snapshot mit höchstens **512 Dateien / 16 MiB**.
Metadaten-/Credential-Dateien, Symlinks und Junctions sind gesperrt.
Restore erfolgt **offline** aus einem leeren Gast-Paketverzeichnis.
Externe NuGet-Pakete benötigen später einen separat bereitgestellten
geprüften Offline-Cache. Es gibt keine vom Modell gewählten Shell-Kommandos.

Pro Auftrag wird eine neue mountlose MicroVM mit 4 vCPU / 4 GiB RAM
erstellt. Workspace, Host-Mounts, Secrets, MCP-Server, Ports und drei
Netzwerkziele werden vor der Codeausführung kontrolliert. Zeitlimits
gelten pro Phase (Restore 75 s, Build 120 s, Ausführung 60 s).
Im `finally` wird die **vom Runner selbst erzeugte** Sandbox mit
`sbx rm --force` entfernt. Der bisherige PoC bleibt unverändert.

Auf dem Host entstehen unter `reports/hami-code-<id>/` die Dateien
`report.json`, `restore.log`, `build.log` und bei Ausführung
`execution.log`. Der JSON-Bericht enthält getrennte Exitcodes,
Timeout-/Cleanup-Status und Phasenzeiten. Logs sind unvertrauenswürdige
Diagnosen und werden niemals auf dem Host ausgeführt.

Während der Ausführung meldet der Runner seinen Fortschritt **direkt
und fortlaufend im Terminal**: Auftrags-ID, VM-Name, Template, Projekt,
Status der Sicherheits-/SDK-Prüfung, Kopieren, Restore, Build,
Ausführung und Entfernung der MicroVM. Jede Meldung enthält die seit
Auftragsbeginn vergangene Zeit; abgeschlossene Phasen zeigen ihre
Dauer und den Exitcode. **Nach** Abschluss des Auftrags wird
automatisch eine **begrenzte, entschärfte Vorschau** des relevanten Logs
im Terminal angezeigt: bei Restore-Fehler `restore.log`, bei
Compilerfehler `build.log`, ansonsten `execution.log` bzw. im
Modus `build` `build.log`. Die Vorschau ist auf 4096 Zeichen /
80 Zeilen begrenzt; ANSI-Steuerzeichen und Unicode-Formatkontrollen
werden neutralisiert. Die vollständigen, unveränderten Diagnosen
liegen weiterhin im Ergebnisverzeichnis. Gast-Ausgaben werden
**nicht live gestreamt**. Bei Timeout ohne abgeschlossenes `sbx exec`
existiert unter Umständen keine `execution.log`; dies wird angezeigt.
`Clear-Host` gehört ausschließlich vor den interaktiven
PowerShell-Aufruf, nicht in den Runner selbst.

Erster echter HaMi11-Realtest (Demo.csproj, Modus run): Restore 0,
Build 0, Ausführung 0, Timeout false, VM wurde entfernt,
`totalMilliseconds=11527`. Davon VM-Erstellung 6998 ms,
Dateiübertragung 321 ms, Restore 910 ms, Build 1173 ms,
Ausführung 528 ms und Bereinigung 556 ms. Das ist **ein**
Messwert und noch kein Performance-Benchmark.

Auf HaMi11 wurden Timeout und anschließende MicroVM-Entfernung,
Netzwerk-Default-Deny für einen Internet-HTTPS- und einen Management-LAN-HTTP-
Kontakt sowie die fehlenden typischen Host-Mountpfade praktisch geprüft.
Die Einzelbelege stehen in Issue #33 / PR #35. Weitere Ressourcenmessungen,
Secret-/Host-Canary-Prüfung und menschliche Mergefreigabe bleiben erforderlich.

### Credential-Vorprüfung für die Sandbox (Issue #33)

Der erste echte Host-/Secret-Test fand im Gast den Namen `GH_TOKEN`.
Damit war **noch kein Tokenwert** nachgewiesen: Docker kann einen
deklarierten, aber leeren Variablennamen bereitstellen. Bei der
Abnahme wird daher **leer** von **nicht leer** unterschieden; Werte
werden niemals protokolliert.

Der allgemeine Runner entfernt die sechs überwachten Credential-
Variablen (`OPENAI_API_KEY`, `GITHUB_TOKEN`, `GH_TOKEN`,
`AZURE_CLIENT_SECRET`, `AWS_SECRET_ACCESS_KEY`, `SSH_AUTH_SOCK`)
aus der Prozessumgebung jeder `sbx`-Invocation, setzt sie beim
Erstellen der MicroVM explizit leer und führt **vor** SDK-Check und
Snapshot-Kopie eine feste Gast-Prüfung durch. Ein nichtleerer Wert
führt zum Abbruch und zur anschließenden VM-Bereinigung. Gastcode
wird in diesem Fall nicht übertragen oder ausgeführt.

Die Gast-Diagnose nennt ausschließlich den Variablennamen, niemals
einen geheimen Wert. Dies schützt die ausdrücklich überwachten
Variablen, ist aber kein formaler Beweis für die Abwesenheit jeder
denkbaren anderen Secret-Quelle.

Der gezielte erneute Realtest erfolgt ohne Wiederholung der übrigen
fünf bereits bestandenen Checks:

```powershell
Clear-Host
$review = 'G:\HaMi\Sandbox\PoC\Runner-PR35'
git -C $review fetch origin
if ($LASTEXITCODE -ne 0) { throw 'Git fetch fehlgeschlagen.' }
git -C $review switch --detach origin/feature/issue33-general-csharp-sandbox-runner-20261120
if ($LASTEXITCODE -ne 0) { throw 'Git switch fehlgeschlagen.' }
& "$review\scripts\acceptance\Invoke-HaMiSandboxAcceptance.ps1" -SecurityOnly
```

### HaMi11-Sammelabnahme – ein Durchlauf statt vieler Einzelbefehle

Das versionierte Skript `scripts/acceptance/Invoke-HaMiSandboxAcceptance.ps1`
erzeugt **eigene** kurzlebige C#-Snapshots und eine ausschließlich auf dem
Host gespeicherte Dummy-Canary-Datei. Es prüft Hostpfade, leeren
Secret-Mount, ausgewählte sicherheitsrelevante Umgebungsvariablen,
Gast-CPU/-RAM, erwarteten Compilerfehler samt Log, Ablehnung einer
`.env`-Datei **vor** Erzeugung einer MicroVM und drei vollständige
Restore-/Build-/Run-Lebenszyklen. Der Runner bereinigt weiterhin nur
seine selbst erzeugten Sandboxes. Die globalen Docker-Regeln werden
nicht verändert, produktive Daten werden nicht gelesen.

Aus dem separaten **Review-Worktree** auf dem aktuellen PR-Branch
`feature/issue33-general-csharp-sandbox-runner-20261120`:

```powershell
Clear-Host
git fetch origin
git switch --detach origin/feature/issue33-general-csharp-sandbox-runner-20261120
& .\scripts\acceptance\Invoke-HaMiSandboxAcceptance.ps1
```

Falls **nur** ein Sicherheitsbefund erneut geprüft werden muss, ist
`-SecurityOnly` verfügbar: Dieser Modus wiederholt lediglich den
Host-/Secret-/Gastressourcentest und erstellt einen gesonderten
Teilprüfbericht. Vorhandene Compiler-/Snapshot-/Performancetests werden
**nicht** wiederholt. Für die Diagnose werden bei gesetzten
sicherheitsrelevanten Umgebungsvariablen ausschließlich **vordefinierte
Variablennamen** protokolliert, niemals deren Werte. Solange eine
unerklärte Variable im Gast vorhanden ist, schlägt die Teilprüfung
vorsätzlich fehl; es gibt keine automatische Freigabe.

Das Skript zeigt jeden Test live und schreibt
`G:\HaMi\Sandbox\PoC\Runs\acceptance-<ID>\acceptance-summary.json`
inklusive PASS/FAIL, Phasen-/Lebenszyklusmessungen und gesammelter
Host-RAM-Stichproben. Letztere zeigen **systemweit verfügbaren RAM**,
nicht den isolierten RAM-Verbrauch einer einzelnen MicroVM.
Test-Snapshots bleiben für die Nachvollziehbarkeit unter
`G:\HaMi\Sandbox\Candidates\acceptance-<ID>\` liegen.
Ergebnisse der zuvor bestandenen Internet-/LAN-/Timeout-Negativtests
werden referenziert, **nicht erneut ausgeführt**.

Das Batch-Ergebnis liefert einen reproduzierbaren Sicherheits- und
Leistungsbefund. Ein `PASS` bedeutet **keine Freigabe aller denkbaren
Zugriffswege** und führt weder zu produktivem KI-Code-Lauf noch zum
automatischen PR-Merge. Der Benutzer entscheidet über den Go/No-Go-
Einsatz des begrenzten Offline-Runners nach gemeinsamer Auswertung.


## HaMi Developer: CommandLine + Akka-gesteuerter Coding-Retry (Issue #36)

Der erste agentische Vertical Slice verbindet den bereits abgenommenen lokalen
Docker-Sandbox-Runner mit dem durch Akka.Hosting gesteuerten Coding-Job.
Die CLI verwendet System.CommandLine 3.0.0 für benannte Befehle; die
Replay wird nur noch als ausdruecklicher Unterbefehl `replay` aufgerufen.

**Offline-Selbsttest ohne Docker und ohne Modellaufruf:**

~~~powershell
Clear-Host

dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- `
    development --repo . --agent-self-test
~~~

Der Selbsttest prüft die CLI-Verarbeitung, den versionierten Auftrag,
einen Compilerfehler mit erfolgreichem Retry, ein ausgeschöpftes
Modellbudget und einen Timeout ohne erneuten Sandbox-Start.

**Realer End-to-End-Durchstich auf HaMi11:**

~~~powershell
Clear-Host

$repo = 'G:\HaMi\Sandbox\PoC\Runner-PR35'
$results = 'G:\HaMi\Sandbox\PoC\Runs'

dotnet run --project "$repo\src\HaMi.Agentic.Cli" -c Release -- `
    development --repo $repo `
    --agent-run `
    --results-dir $results
~~~

Zuvor den bestehenden Review-Worktree Runner-PR35 kontrolliert auf
Branch feature/issue36-commandline-akka-sandbox-flow-20261123
aktualisieren. Ein zusätzlicher Worktree ist nicht erforderlich.

Der deterministische Generator erzeugt zunächst absichtlich den
Compilerfehler CS0103, wertet dessen Sandbox-Diagnose aus und erzeugt
einen korrigierten Program.cs-Kandidaten. **Jeder Versuch läuft in einer
eigenen MicroVM.** Ein JSON-Jobbericht und getrennte MicroVM-Berichte
dokumentieren die Ergebnisse. Kein generierter Code wird auf HaMi11
gebaut oder gestartet.

**Modellaufruf (nur explizit, kann Gebühren verursachen):** Dasselbe
Kommando um --live --model gpt-5.4-mini ergänzen. Ohne --live erfolgt
kein Modellaufruf. Die Ausgabe darf nur Program.cs in einem
temporären Snapshot ersetzen; die Default-deny-Netzwerkpolicy und
Credential-Isolation aus PR #35 bleiben unverändert.

Der Auftrag ist bewusst auf den versionierten Vertrag
contracts/development/agent-console-retry.json und ein .NET-11-
Konsolenprogramm begrenzt. Andere Projektdateien, Online-NuGet,
Windows-/WPF-Builds und GitHub-Schreibzugriffe bleiben unzulässig.
Architektur: docs/decisions/0001-commandline-akka-isolated-coding.md.


**Empfohlene gebündelte HaMi11-Abnahme für PR #37:**

~~~powershell
Clear-Host

$review = 'G:\HaMi\Sandbox\PoC\Runner-PR35'
git -C $review fetch origin
if ($LASTEXITCODE -ne 0) { throw 'Fetch fehlgeschlagen.' }
git -C $review switch --detach origin/feature/issue36-commandline-akka-sandbox-flow-20261123
if ($LASTEXITCODE -ne 0) { throw 'Worktree-Aktualisierung fehlgeschlagen.' }

& "$review\scripts\acceptance\Invoke-HaMiDeveloperAcceptance.ps1"
~~~

Das Skript führt ohne Modellkosten nacheinander CLI-/Akka-Selbsttests
und zwei echte frische MicroVM-Versuche (Compilerfehler, Korrektur)
durch. Es prüft beide Cleanup-Nachweise, das neue Agent-JSON und dass
Git-Status und HEAD des Review-Checkouts unverändert geblieben sind.
Bestehende untracked Dateien werden weder gelöscht noch zurückgesetzt.
Kein Git-Merge erfolgt automatisch.


**Abschlussprüfung eines bereits ausgeführten Jobs ohne erneute MicroVMs**

Beim HaMi11-Reallauf vom 23.11.2026 sind beide Gäste laut Runner
erfolgreich entfernt worden. Das ursprüngliche Abnahmeskript
scheiterte lediglich daran, dass die Antwort von `sbx inspect`
die Wörter `sandbox` und `not found` auf zwei
Zeilen verteilte. Die korrigierte Auswertung akzeptiert eine solche
mehrzeilige Meldung, verlangt weiterhin Nichtnull-Exitcode und den
konkreten Sandboxnamen.

Dafür gibt es `-VerifyOnly -ReportPath`. Es werden
**keine** Agent-, Modell-, Build-, Restore- oder MicroVM-Aufträge
gestartet; nur die bereits erzeugten Berichte und die Abwesenheit der
beiden Sandboxnamen werden geprüft.

~~~powershell
Clear-Host

$review = 'G:\HaMi\Sandbox\PoC\Runner-PR35'
git -C $review fetch origin
if ($LASTEXITCODE -ne 0) { throw 'Fetch fehlgeschlagen.' }
git -C $review switch --detach origin/feature/issue36-commandline-akka-sandbox-flow-20261123
if ($LASTEXITCODE -ne 0) { throw 'Worktree-Aktualisierung fehlgeschlagen.' }

$existingReport = 'G:\HaMi\Sandbox\PoC\Runs\agent-job-1763b445d24a427f8ee487149fd5eb4a\agent-report.json'

& "$review\scripts\acceptance\Invoke-HaMiDeveloperAcceptance.ps1" `
    -VerifyOnly -ReportPath $existingReport
~~~

Der Verify-Only-Modus prüft nur den **aktuellen Zustand der verfolgten
Git-Dateien**; der frühere Status bereits vorhandener untracked Dateien
kann nachträglich nicht unabhängig bewiesen werden.


### Agent-Logging und CLI-Bereinigung (Issue #38)

Der Development-Agent schreibt korrelierte `ILogger`-Events gleichzeitig als
lesbare Konsolenausgabe und in die bestehende rotierende Serilog-
`.clef`-Datei unter `HAMI_AGENT_WORKTREE_ROOT/hami-agent-logs`.
`JobId`, Versuchsnummer, Preflight-/Sandbox-Phase, Restore-/Build-/Run-
Exitcodes, Cleanup-Status und Retry-Entscheidung sind darin nachvollziehbar.
Terminale Fehler nennen einen geprueften Grundcode und eine Massnahme.
Ein Report wird nur dann angegeben, wenn er wirklich geschrieben wurde.
Die historischen impliziten Option-first-Aufrufe sind entfernt:
`replay --check .` verwenden, nicht `--check .`.
