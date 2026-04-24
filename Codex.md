# Codex Hinweise für die Projektdokumentation

## Ziel

Diese Datei dient als interne Arbeitsgrundlage für die Erstellung und Überarbeitung der Projektdokumentation in diesem Repository.

## Ablage und Benennung

- Lege neue Doku-Kapitel immer im Ordner `Docs` ab.
- Benenne Kapiteldateien nach dem Muster `1_Kapitelname.md`, `2_Kapitelname.md`, `3_Kapitelname.md` usw.
- Verwende die Nummerierung konsequent, damit die Reihenfolge der Dokumentation direkt an den Dateinamen erkennbar ist.
- Neue Kapitel für die Projektdokumentation sollen nicht lose an anderen Stellen im Repository angelegt werden.

## Schreibstil

- Schreibe natürlich, sachlich und menschlich.
- Vermeide einen übertrieben glatten, werblichen oder künstlich klingenden Stil.
- Formuliere fachlich sauber, aber nicht unnötig hochgestochen.
- Schreibe so, dass die Dokumentation plausibel von einem Auszubildenden mit echter Projekterfahrung stammen kann.
- Erkläre Entscheidungen nachvollziehbar und konkret statt allgemein oder lehrbuchhaft.

## Humanizer

- Nutze für Dokumentationstexte den `humanizer`-Skill, **wenn er in der aktuellen Session verfügbar ist**.
- Falls der Skill nicht verfügbar ist, schreibe trotzdem in einem natürlichen, glaubwürdigen und unaufgeregten Stil weiter.
- Ziel ist keine künstliche Sprachverfremdung, sondern gut lesbare, fachlich stimmige Projektdokumentation.

## Sprache und Zeichensetzung

- Verwende echte deutsche Umlaute: `ä`, `ö`, `ü`, `Ä`, `Ö`, `Ü`, `ß`.
- Schreibe keine Ersetzungen wie `ae`, `oe`, `ue`, wenn es sich um normalen deutschen Fließtext handelt.
- Achte auf saubere deutsche Grammatik und Rechtschreibung.
- Vermeide kaputte Encoding-Zeichen.

## Fachlicher Bezug

- Beziehe dich bei Doku-Texten auf den tatsächlichen Projektstand im Repository.
- Schaue dafür immer in den vorhandenen Code, die Projektstruktur und bestehende Dokumente.
- Nutze insbesondere `Docs/Antrag.md` als inhaltliche Referenz für Ausgangssituation, Zielsetzung, Projektumfeld und Abgrenzung.
- Prüfe Aussagen nach Möglichkeit gegen den realen Code, damit keine Funktionen beschrieben werden, die im Projekt so nicht existieren.
- Wenn Architektur, Ablauf oder Verhalten beschrieben werden, sollen diese aus dem tatsächlichen Projekt ableitbar sein.

## Inhaltliche Leitlinien

- Beschreibe nicht nur, **was** umgesetzt wurde, sondern auch **warum**.
- Stelle die eigene Leistung klar heraus.
- Trenne sauber zwischen:
  - bestehender Infrastruktur
  - Fremdsystemen
  - bereitgestellten Schnittstellen
  - eigener Entwicklungsleistung
- Vermeide reine Feature-Aufzählungen ohne Einordnung.
- Schreibe eher projektbezogen als theoretisch.

## IHK-Bezug

- Berücksichtige bei der Dokuarbeit immer die Vorgaben aus `Docs/Requirements.md`.
- Berücksichtige zusätzlich immer die individuellen Auflagen aus `Docs/0_IHK_Auflagen.md`.
- Wenn Unsicherheit besteht, ist zusätzlich die PDF `Docs/Handreichung_IHK.pdf` heranzuziehen.
- Struktur, Umfang und Ton der Kapitel sollen zur IHK-Projektdokumentation für Fachinformatiker Anwendungsentwicklung passen.
