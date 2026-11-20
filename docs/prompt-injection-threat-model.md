# Prompt-Injection-Threat-Model

Stand: 20.11.2026 · [Issue #21](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/21)

## Abgrenzung

Prompt Injection ist ein **Vertrauensgrenzenproblem**: Ein Modell kann Anweisungen in Daten
(z. B. Kompilierfehlern, Testlogs, README-Texten oder GitHub-Issues) mit tatsächlich
autorisierten Instruktionen verwechseln. Ein Verbot im Prompt, JSON-Escaping oder
ein Klassifikator allein verhindern dieses Verhalten **nicht zuverlässig**.

Der bisherige Coding-Slice ist eng beschränkt. Für die allgemeine zukünftige
Codegenerierung entstehen zusätzliche Angriffsflächen – insbesondere wenn fremder
Repository-Text und Toolausgaben eine agentische Aktion beeinflussen können.

## Vertrauensstufen

| Quelle | Einstufung | Erlaubte Verwendung |
| --- | --- | --- |
| Menschlich freigegebener Task / Ausführungszweck | Trusted | Fachliche Zielsetzung und Akzeptanzkriterien |
| Versioniertes Prompty-Asset | Trusted *nach* Review | Rollen und Modellanweisungen, keine eigenständige Toolfreigabe |
| Versionierte JSON-Execution-Policy | Trusted *nach* Review | Erlaubte Fähigkeiten, Pfade, Budgets, Timeouts |
| Host-Code und feste Testharness | Trusted, sofern geprüfter Baseline-Commit | Erzwingung und unabhängige Validierung |
| Compiler-, Test- und Analyzer-Ausgaben | **Untrusted** | Diagnostische Daten, keine Instruktionen |
| Modellantworten | **Untrusted** | Strukturierte Vorschläge, nicht unmittelbar ausführbar |
| GitHub-Issues/PRs/Kommentare, Repo-Dateien, Webseiten, MCP-Ergebnisse | **Untrusted** | Späterer Kontext, erst nach Provenienz-/Scopeprüfung |

**Wichtig:** Lokal veränderte, kompromittierte oder fremde Versionen einer
Prompty-/Policy-Datei sind nicht automatisch Trusted. Das Lesen aus einem
Git-Checkout genügt nicht als Berechtigungsnachweis. Bei allgemeineren Aufträgen
müssen Auftrags-/Policy-Provenienz und der geprüfte Baseline-Commit zusätzlich
verifiziert werden.

## Aktuelle deterministische Schutzmaßnahmen

1. **Least Privilege**: Das Modell bekommt keine Shell-/GitHub-Tools. Beide
   API-Transporte behalten Tool-Listen leer. `allowShellCommands`,
   `allowGitHubWrites` und `allowedModelTools` aus der Policy werden
   im engen Slice nur in der sicheren Konfiguration akzeptiert.
2. **Getrennte Datenkanäle**: `prompts/coding/ClampExpression.prompty`
   enthält autorisierte Anweisungen; Feedback aus Compiler/Test/Validation
   wird vor dem Rendern **als JSON-Datenobjekt** mit `source`,
   `truncated` und `text` serialisiert. Fake-`system:`-/`user:`-Marker,
   JSON-Objekte und Steuerzeichen bleiben innerhalb des kodierten Datenfelds.
3. **Begrenzung**: `maxFeedbackCharacters` liegt in der versionierten,
   validierten `policies/coding/clamp.policy.json`; der Output-Reader und
   der Prompt-Renderer erzwingen den Budgetwert. Der Host lehnt zu große
   oder ungültige Policy-Limits vor Worktree-/Modellaufruf ab.
4. **Output-Validierung**: Strukturiertes `plan`/`expression`-JSON,
   Regex-/Identifier-Allowlist, nur die feste `Clamp.cs`-Datei, echte
   Compiler- und 142 Testfälle, begrenzte Wiederholung, Rollback bei Fehler.
   Keine von Modellinhalten abgeleiteten Commands oder weiteren Pfade.
5. **Ausführungsisolation**: Git-Worktree trennt Änderungen vom produktiven
   Checkout, **ist aber keine OS-Sandbox**. Unbeschränkter Modellcode darf
   dort weiterhin nicht ausgeführt werden.
6. **Auditierbarkeit**: Lokales `report.json` enthält Modelltransport,
   Ergebnis, Prompt-/Policy-SHA-256 und Aufrufzähler, aber keine Secrets.
   Der Feedbacktext wird nicht unnötig in dieses Report-DTO kopiert.

Das JSON-Datenfeld erschwert *Rollen-/Delimiter-Spoofing*, **garantiert
aber nicht**, dass ein Sprachmodell inhaltlich bösartigen Instruktionen
nicht folgt. Deshalb müssen die deterministischen Grenzen unabhängig
vom Modell wirksam bleiben.

## Adversariale Offline-Regression

Der Prompt-Asset-Selbsttest verwendet unter anderem unvertrautes Feedback
mit eingebettetem `system:`, `{"role":"system",...}`, XML-ähnlichen
Rollen-Tags und Terminal-Steuersequenzen. Er prüft insbesondere:

- Das Original bleibt im **JSON-`text`-Feld**, ohne eine neue
  Nachrichtenrolle zu erzeugen.
- Die autorisierte Systeminstruktion ist unabhängig vom Feedback unverändert.
- Überlange Diagnosedaten werden mit `truncated=true` und begrenztem
  `text` kodiert.
- Bereits existierende Build-/Test-, Policy-, Prompty- und
  Fake-`IChatClient`-Regressionen bleiben grün.

Dies sind **hostseitige Strukturtests**, kein Nachweis einer universellen
Immunität gegen Prompt Injection.

## Nächste Sicherheitsstufe für allgemeine Vertical Slices

Bevor wir beliebige Quelltexte, GitHub-/MCP-Ausgaben oder Webseiten einlesen,
brauchen wir überprüfbare Daten-/Berechtigungsgrenzen: autorisierte Tasks,
Quelle/Provenienz, begrenztes Context-Window, nur die minimalen Fähigkeiten
des jeweiligen Slices, isolierte Ausführung, Diff-Prüfung, kontrollierte
Weitergabe von Ergebnissen und eine adversariale Testsuite. Keine automatische
Privilegienerhöhung durch neue Prompt- oder Policy-Einträge.

Quellen:
- [OWASP: LLM Prompt Injection Prevention](https://cheatsheetseries.owasp.org/cheatsheets/LLM_Prompt_Injection_Prevention_Cheat_Sheet.html)
- [OWASP: Secure Coding with AI](https://cheatsheetseries.owasp.org/cheatsheets/Secure_Coding_with_AI_Cheat_Sheet.html)
- [Microsoft: Defend against indirect prompt injection](https://learn.microsoft.com/en-us/security/zero-trust/sfi/defend-indirect-prompt-injection)
- [Microsoft: Input, Context, and Retrieval Hygiene](https://learn.microsoft.com/en-us/security/zero-trust/catalog-ai-defense-capabilities/input-context-retrieval-hygiene)
