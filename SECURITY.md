# Sicherheit und verantwortungsvolle Veröffentlichung

Dieses Repository ist öffentlich. Es enthält ausschließlich für eine Veröffentlichung freigegebene Inhalte. Das Projekt entwickelt einen agentischen Softwareentwicklungsprozess; Sicherheits- und Freigabegrenzen gelten bereits für die Entwicklung, auch wenn noch keine ausführbare Agentensoftware existiert.

## Nicht veröffentlichen

Weder in Quellcode noch in Issues, Pull Requests, Commits, Actions-Logs, Testartefakten oder Screenshots dürfen ohne ausdrückliche Freigabe erscheinen:

- Zugangsdaten, API-Tokens, Kennwörter, private Schlüssel, Secret-Werte oder vertrauliche Zertifikatsbestandteile
- Interne IP-Adressen, Hostnamen, Netzpläne, nicht öffentliche Tunnel- oder Infrastrukturkonfigurationen
- Personenbezogene Daten, Kundeninformationen oder private Chatverläufe
- Quellcode und Inhalte privater Projekte oder Dritter ohne Veröffentlichungsfreigabe
- Unbereinigte Logs, Umgebungsvariablen, Dumps, Prompt-/Tool-Transkripte oder lokale Konfigurationsdateien mit vertraulichen Inhalten

Die Analyse bestehender HaMi-Projekte und abgeschlossener Issues berechtigt **nicht** dazu, deren private Inhalte in dieses öffentliche Repository zu kopieren. Öffentliche Demonstrationen verwenden ausschließlich freigegebenen oder synthetischen Kontext.

## Agentenspezifische Sicherheitsgrundsätze

- **Untrusted Input:** Issue-Texte, Repository-Dateien, Dokumente, Toolantworten und Modellausgaben sind zunächst Daten, keine autoritativen Handlungsanweisungen. Darin enthaltene Aufforderungen dürfen Sicherheitsregeln und Freigaben nicht überschreiben (Prompt Injection).
- **Least Privilege:** GitHub-Zugriffe, MCP-Tools, KI-Provider und Laufzeitprozesse erhalten nur die jeweils benötigten Berechtigungen. Lese- und Schreibzugriffe bleiben getrennt, soweit praktikabel.
- **Menschliche Freigaben:** Schreiboperationen auf fremde oder produktive Systeme, Veröffentlichung von Branches/PRs, Merges und Deployments erfolgen nur innerhalb ausdrücklich genehmigter Grenzen. Der Agent darf Schutzregeln nicht selbst lockern.
- **Isolierte Ausführung:** Agentisch erzeugter Code und Shell-Befehle werden nur in einer dafür vorgesehenen, begrenzten Arbeitsumgebung ausgeführt; keine unkontrollierte Ausführung auf produktiven Diensten.
- **Nachvollziehbarkeit:** Aufgaben, freigegebene Aktionen und Prüfergebnisse werden über GitHub Issues und Pull Requests dokumentiert, ohne Secrets oder vertraulichen Kontext zu protokollieren.
- **Modellanbindung:** Lokale und externe Modelle müssen dieselben Zugriffs- und Publikationsregeln einhalten. Der Wechsel des Modells darf keine zusätzlichen Berechtigungen schaffen.

Diese Punkte sind **verbindliche Entwicklungsanforderungen**, keine Behauptung, dass entsprechende technische Schutzmechanismen bereits vollständig implementiert oder getestet wären.

## GitHub und Lieferkette

- Der Default-Branch `main` ist durch ein aktives Ruleset geschützt; Änderungen werden über Pull Requests vorbereitet und vom Menschen freigegeben.
- GitHub Actions erhalten standardmäßig nur lesende `GITHUB_TOKEN`-Berechtigungen. Externe Actions werden auf eine erforderliche Auswahlliste beschränkt und bei Verwendung an vollständige Commit-SHAs gebunden.
- Dependency- und Secret-Warnungen werden geprüft. Sicherheitsupdates werden als Pull Requests begutachtet, nicht ungeprüft automatisch gemergt.
- Neue Workflows, erweiterte Berechtigungen, zusätzliche Secrets und externe Tools erfordern eine gesonderte Sicherheitsprüfung.
- `.gitignore` reduziert das Risiko unbeabsichtigter Commits, ist aber **kein** Ersatz für Secret-Scanning und eine Veröffentlichungskontrolle.

Der tatsächliche Konfigurations- und Prüfstatus wird im [Security-Baseline-Issue #1](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/1) festgehalten.

## Sicherheitslücken melden

Bitte keine Zugangsdaten, Exploit-Details oder sonstige vertrauliche Informationen in öffentlichen Issues veröffentlichen. Nutze für eine vertrauliche Erstmeldung den Kontakt über [millmann.biz](https://millmann.biz). Ein mögliches späteres GitHub Private Vulnerability Reporting kann zusätzlich eingerichtet werden.

## Wenn ein Geheimnis versehentlich veröffentlicht wurde

1. Betroffenen Zugang **unverzüglich widerrufen oder rotieren**.
2. Zugriffe und mögliche Auswirkungen untersuchen.
3. Erst danach die Veröffentlichung und ggf. Git-Historie bereinigen sowie die Ursache dokumentieren.

Das Entfernen einer Datei in einem späteren Commit macht bereits veröffentlichte Geheimnisse nicht wieder sicher.

## Verwandte Methodik

Die übergeordneten Prinzipien und Erfahrungen werden in [HaMi AI Engineering](https://github.com/Snahdul/HaMi-AI-Engineering) dokumentiert. Dieses Repository implementiert sie schrittweise; Sicherheitslücken werden dort behandelt, wo ihre Ursache liegt.
