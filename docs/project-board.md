# GitHub Project #2 – Statusautomatisierung

Das Agenten-Repository wird über das eigene öffentliche [HaMi AI Engineering Agent Project #2](https://github.com/users/Snahdul/projects/2/views/1) gesteuert. Das eigenständige [Methodik-Project #1](https://github.com/users/Snahdul/projects/1) bleibt davon unberührt.

## Voraussetzung in GitHub Projects

Im Project #2 sind fünf Statusoptionen in `Status` vorhanden:
`Todo`, `In Progress`, `Review`, `Done`, `Cancelled`.

Folgende **eingebauten** Project-Workflows bleiben aktiviert:

- `Auto-add to project`: nur Repository `Snahdul/HaMi-AI-Engineering-Agent`, Filter **`is:issue,pr`**; die tatsächliche Einstellung in der Oberfläche prüfen.
- `Item added to project`: neue Issues und PRs erhalten `Todo`.
- `Item closed`: geschlossene Issues erhalten `Done`.
- `Pull request linked to issue`: das zugehörige Issue wechselt zu `In Progress`.
- `Pull request merged`: gemergte PRs erhalten `Done`.

Diese eingebauten Workflows sind nicht durch die unten stehenden Repository-Actions ersetzt.

## Einmalige Repository-Einstellungen (manuell)

In **Agenten-Repository → Settings → Secrets and variables → Actions**:

1. Repository-Variable `HAMI_PROJECT_NUMBER` auf **`2`** setzen.
2. Repository-Secret `HAMI_PROJECT_TOKEN` bereitstellen: Für dieses **benutzereigene** GitHub Project ist ein Personal Access Token **(classic)** mit dem Scope `project` geeignet. GitHub dokumentiert derzeit eine Einschränkung für Fine-grained PATs bei user-owned Projects. Da der Workflow auf ein öffentliches Repository zugreift, zunächst nur `project` verwenden und den tatsächlichen API-Zugriff testen, statt vorsorglich `repo` zu vergeben. Der Token ist nicht auf Project #2 begrenzbar; ein separater, befristeter Token ermöglicht aber getrennte Rotation und Widerruf.
3. Weder Token-Wert noch sonstige Geheimnisse in Issues, PRs, Workflows oder Logs eintragen. Eine vorhandene Berechtigung für Project #1 bedeutet nicht zwangsläufig Zugriff auf Project #2.
4. GitHub Actions und die erlaubten Workflows prüfen. Die neuen Workflows werden erst nach kontrolliertem Merge nach `main` produktiv.

Die GitHub-Integration in ChatGPT kann Repository-Secrets und -Variablen nicht auslesen oder schreiben. **Die erfolgreiche Konfiguration darf daher nicht allein aus dem Vorhandensein der Workflow-Dateien abgeleitet werden.**

## Statusübergänge

| Ereignis | Project-Status |
| --- | --- |
| Neues Issue/PR aufgenommen | `Todo` (Project-Standardworkflow) |
| Neues/verändertes internes PR ohne Sonderlabel | `Review` |
| Offenes Issue oder internes PR mit `workflow:in-progress` | `In Progress` |
| Offenes Issue oder internes PR mit `workflow:review` | `Review` |
| Issue geschlossen | `Done` (Project-Standardworkflow) |
| PR mit Merge geschlossen | `Done` (Project-Standardworkflow) |
| PR ohne Merge geschlossen | `Cancelled` |
| Interner PR wiedereröffnet | `Review` oder per Label `In Progress` |

Die Secret-freie Action `pr-status-signal.yml` reagiert auf PR-Ereignisse, ohne PR-Code auszuführen. Die Action `project-status.yml` läuft mit Project-Zugriff ausschließlich in der vertrauenswürdigen Default-Branch-Workflow-Ausführung über `workflow_run`, manuelles `workflow_dispatch`, Issue-Labels oder den täglichen Recovery-Lauf. PRs aus fremden Repositories werden nicht synchronisiert. Die Implementierung ist aus dem Methodik-Repository übernommen.

Der tägliche Recovery-Lauf dient dazu, verpasste PR-Ereignisse und Status-Labels nachzuführen; er ersetzt keine Live-Abnahme.

## Abnahme nach Freigabe/Merge

1. `HAMI_PROJECT_NUMBER=2`, `HAMI_PROJECT_TOKEN` und den Auto-add-Filter in der GitHub-Oberfläche verifizieren.
2. Neuen internen Test-PR auf Basis von `main` erstellen: Eintrag im **Project #2** zunächst `Todo`, danach `Review`; GitHub-Action-Protokolle prüfen.
3. Test-PR ohne Merge schließen: `Cancelled`; wiedereröffnen: `Review` bzw. `In Progress`.
4. Eine reguläre PR nach menschlicher Freigabe mergen und `Done` überprüfen.
5. Ein Test-Issue schließen und `Done` prüfen; bei Bedarf die Status-Labels `workflow:in-progress`/`workflow:review` testen.
6. Prüfen, dass **Project #1** unverändert geblieben ist und Logs keinerlei Secrets enthalten.

Ohne diese Live-Nachweise gilt die Automatisierung nur als **vorbereitet**, nicht als abgenommen.

Verfolgung: [Agent-Issue #8](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/8).


## Issue-driven Development: Label-Handover

Ein erfolgreich abgeschlossener Development-Job darf nur nach ausdrücklichem Publish-Opt-in das Label workflow:in-progress entfernen und workflow:review setzen. Die bestehenden GitHub Actions übersetzen dies nach Review in Project #2; der Agent verändert keine ProjectV2-Felder. Ein Folge-Issue ohne Statuslabel wird durch den eingebauten Project-Workflow als Todo aufgenommen. Es gibt keinen automatischen Merge oder Neustart.
