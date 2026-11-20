# HaMi AI Engineering Agent

**Agentic software engineering with .NET 11, driven by documented development workflows.**

Die Methodik und ihre Erkenntnisse werden im eigenständigen [HaMi AI Engineering](https://github.com/Snahdul/HaMi-AI-Engineering) dokumentiert. Dieses Repository entwickelt eine ausführbare agentische Umsetzung dieser Arbeitsweise.

**Projektsteuerung:** [HaMi AI Engineering Agent – GitHub Project #2](https://github.com/users/Snahdul/projects/2/views/1) · [Statusautomatisierung und Einrichtung](docs/project-board.md).

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

# Den API-Schlüssel nicht im Chat, Git, Skript oder Issue hinterlegen.
$secret = Read-Host "OpenAI API-Schlüssel" -AsSecureString
$env:OPENAI_API_KEY = [System.Net.NetworkCredential]::new("", $secret).Password
dotnet run --project src/HaMi.Agentic.Cli -- --repo . --model gpt-5.4-mini

# Alternative für einen lokal unbeaufsichtigten Replay (keine GitHub-Schreibrechte):
# dotnet run --project src/HaMi.Agentic.Cli -- --repo . --model gpt-5.4-mini --unattended
Remove-Item Env:OPENAI_API_KEY
```

Die CLI prüft den Repository-Ursprung und den historischen Commit, erstellt einen separaten lokalen **detached Worktree** im temporären Verzeichnis und sendet ausschließlich den kuratierten Auftrag und die drei ursprünglichen öffentlichen Quellvorlagen an das Modell. **Standardmäßig** fordert sie vor dem Schreiben weiterhin ausdrücklich `JA`. **Optional** überspringt `--unattended` nur diesen lokalen Freigabepunkt, wenn `--repo` explizit gesetzt wurde, die fachliche Validierung erfolgreich ist und der abgetrennte historische Worktree sauber auf dem erwarteten Commit steht. Es werden ausschließlich die drei allowlisteten Dateien dort geschrieben; kein Push, Merge, Deployment und keine Ausführung von Modell-Shellbefehlen.

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

Echten, kostenpflichtigen Ablauf nur auf HaMi11 und mit temporär gesetztem `OPENAI_API_KEY` starten:

```powershell
dotnet run --project .\src\HaMi.Agentic.Cli -c Release -- workflow --repo . --manifest workflows/mvp02.json --unattended --model gpt-5.4-mini
```

Berichte liegen ausschließlich lokal unter `%TEMP%\hami-agent-workflows\<lauf>\report.json`; es werden keine Modellinhalte, API-Schlüssel oder lokalen Artefakte nach GitHub übertragen. Der unabhängige Workflow-Durchstich ist zunächst **nur für Markdown-Artefakte** vorgesehen. Er ist weder ein generischer Softwareentwicklungsagent noch eine Lizenz zur automatischen Bearbeitung produktiver Quelltexte.

## Sicherheit

Öffentliche Informationen und erlaubte Werkzeuge sind durch [SECURITY.md](SECURITY.md) begrenzt. Der Mensch bleibt für die Freigabe und die Integration verantwortlich.
