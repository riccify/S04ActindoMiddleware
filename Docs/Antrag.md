# IHK Projektantrag - Fachinformatiker Anwendungsentwicklung

---

## 1. Titel der Projektarbeit

**Entwicklung einer Middleware zur Synchronisation zwischen Microsoft Dynamics NAV und dem neuen Kassensystem Actindo**

---

## 2. Detaillierte Projektbeschreibung

### Ausgangssituation (IST-Zustand)

Der FC Schalke 04 e.V. nutzt Microsoft Dynamics NAV als zentrales ERP-System fuer die Verwaltung von Produkten, Kunden und Lagerbestaenden. Fuer den Verkauf in den Fanshops und am Stadion wird derzeit das Kassensystem Shopware eingesetzt. Fuer die Anbindung zwischen NAV und Shopware existiert eine Middleware-Loesung.

Aufgrund von Lizenzkosten, fehlenden Funktionen und besserer Integration mit der bestehenden Infrastruktur hat sich das Unternehmen entschieden, das Kassensystem von Shopware auf Actindo zu wechseln. Actindo bietet eine moderne REST-API mit OAuth2-Authentifizierung und ist speziell fuer den Einzelhandel optimiert.

Die bestehende Shopware-Middleware kann nicht fuer Actindo wiederverwendet werden, da:

- Actindo eine vollstaendig andere API-Struktur verwendet
- Die Authentifizierung ueber OAuth2 statt API-Keys erfolgt
- Die Datenformate und Feldmappings unterschiedlich sind
- Neue Funktionen wie Gutscheinverwaltung und Transaktionsabruf benoetigt werden

Daher muss eine komplett neue Middleware entwickelt werden, die speziell auf die Actindo-Schnittstelle zugeschnitten ist.

### Zielgruppe und Auftraggeber

Auftraggeber ist die IT-Abteilung des FC Schalke 04 e.V. Die Zielgruppen sind:

- **IT-Administratoren**: Zur Ueberwachung und Konfiguration der Synchronisation
- **Fachabteilung Merchandising**: Zur Produktpflege und Bestandskontrolle
- **Buchhaltung**: Zur Nachverfolgung von Transaktionen

### Projektziele und Nutzen (SOLL-Zustand)

Ziel des Projekts ist die Entwicklung einer Middleware, die als Vermittler zwischen Dynamics NAV und dem neuen Kassensystem Actindo fungiert. Die Middleware soll folgende Funktionen bereitstellen:

1. **Produkt-Synchronisation**: Automatische Uebertragung von Produkten inkl. Varianten, Preisen und Bestaenden von NAV nach Actindo
2. **Kunden-Synchronisation**: Abgleich von Kundendaten zwischen beiden Systemen
3. **ID-Mapping**: Verwaltung der unterschiedlichen Primaerschluessel in NAV und Actindo
4. **Job-Monitoring**: Protokollierung aller Synchronisationsvorgaenge mit Fehlerbehandlung
5. **Web-Frontend**: Benutzerfreundliche Oberflaeche zur Statusueberwachung und Administration

Der wirtschaftliche Nutzen fuer das Unternehmen:

- Reibungsloser Wechsel vom alten auf das neue Kassensystem
- Reduzierung manueller Pflegeaufwaende um ca. 80%
- Vermeidung von Datendiskrepanzen zwischen den Systemen
- Echtzeit-Uebersicht ueber den Synchronisationsstatus
- Schnellere Reaktion auf Fehler durch zentrales Monitoring

### Eigene Leistung (Abgrenzung)

Im Rahmen dieses Projekts werde ich folgende Taetigkeiten eigenstaendig durchfuehren:

- Analyse der bestehenden Systeme und Anforderungserhebung
- Konzeption und Architekturentwurf der Middleware
- Entwicklung des Backends (REST-API, Datenbankanbindung, Authentifizierung)
- Entwicklung des Web-Frontends (Dashboard, Administration)
- Entwicklung der NAV-seitigen Komponenten (Codeunits, Tabellen, Buffer-Logik)
- Implementierung der Schnittstellen zu Actindo und NAV
- Docker-Containerisierung und Deployment-Automatisierung
- Durchfuehrung von Tests und Qualitaetssicherung
- Erstellung der Projektdokumentation

**Nicht Teil meiner Leistung:**

- Die Actindo API wird als bestehende Schnittstelle genutzt
- Die Server-Infrastruktur wird vom Unternehmen bereitgestellt
- Die alte Shopware-Middleware wird nicht von mir betreut

### Schnittstellen

1. **Actindo REST API**
   
   - OAuth2-Authentifizierung
   - Endpoints fuer Produkte, Kunden, Inventar, Preise, Transaktionen
   - JSON-Datenformat

2. **NAV REST API**
   
   - Bearer-Token-Authentifizierung
   - Endpoints fuer ID-Synchronisation (Product-IDs, Customer-IDs)
   - JSON-Datenformat

3. **SQLite-Datenbank**
   
   - Lokale Speicherung von Jobs, Produkten, Einstellungen, Benutzern

### Bereitgestellte Hard- und Software

- Entwicklungsrechner mit Windows 11
- Visual Studio Code als IDE
- Microsoft Dynamics NAV Entwicklungsumgebung
- .NET 10 SDK
- Node.js 20 LTS
- Docker Desktop
- Git fuer Versionskontrolle
- Zugang zum Testserver (Linux VM)
- Testzugang zu Actindo und NAV Testumgebungen

---

## 3. Projektumfeld

Das Projekt wird **betriebsintern** in der IT-Abteilung des FC Schalke 04 e.V. durchgefuehrt.

**Oertlichkeit:** Gelsenkirchen, Deutschland (Buero und Homeoffice)

**Technisches Umfeld:**

- Betriebssystem Server: Linux (Debian-basiert)
- Betriebssystem Entwicklung: Windows 11
- Programmiersprachen: C# (.NET 10), TypeScript, C/AL (NAV), SQL
- Framework Frontend: SvelteKit (Svelte 5)
- Datenbanken: SQLite (Middleware), SQL Server (NAV)
- Containerisierung: Docker
- Versionskontrolle: Git/GitHub

**Bestehende Systeme:**

- Microsoft Dynamics NAV (ERP)
- Shopware (altes Kassensystem, wird abgeloest)
- Actindo POS (neues Kassensystem)
- Internes Netzwerk mit VPN-Zugang

---

## 4. Projektphasen

| Phase                  | Taetigkeit                                       | Stunden |
| ---------------------- | ------------------------------------------------ | ------- |
| **1. Analyse**         |                                                  | **8**   |
|                        | IST-Analyse der bestehenden Systeme              | 3       |
|                        | Anforderungsanalyse mit Fachabteilung            | 3       |
|                        | Analyse der Actindo und NAV APIs                 | 2       |
| **2. Planung**         |                                                  | **10**  |
|                        | Erstellung des Pflichtenhefts                    | 4       |
|                        | Architekturentwurf und Technologieauswahl        | 3       |
|                        | Datenbankdesign                                  | 2       |
|                        | Zeitplanung und Meilensteine                     | 1       |
| **3. Implementierung** |                                                  | **40**  |
|                        | Backend: Grundstruktur und Authentifizierung     | 6       |
|                        | Backend: Actindo-Integration (OAuth, API-Client) | 8       |
|                        | Backend: Produkt- und Kunden-Synchronisation     | 8       |
|                        | Backend: Job-Monitoring und Fehlerbehandlung     | 4       |
|                        | NAV: Codeunits und Buffer-Logik                  | 6       |
|                        | Frontend: Dashboard und Produktuebersicht        | 4       |
|                        | Frontend: Admin-Bereich (Settings, Users)        | 4       |
| **4. Test**            |                                                  | **8**   |
|                        | Komponententests                                 | 3       |
|                        | Integrationstests mit Testsystemen               | 3       |
|                        | Abnahmetests mit Fachabteilung                   | 2       |
| **5. Deployment**      |                                                  | **4**   |
|                        | Docker-Containerisierung                         | 2       |
|                        | Deployment auf Produktivserver                   | 1       |
|                        | Konfiguration und Inbetriebnahme                 | 1       |
| **6. Dokumentation**   |                                                  | **10**  |
|                        | Projektdokumentation                             | 7       |
|                        | Benutzerhandbuch                                 | 2       |
|                        | Technische Dokumentation                         | 1       |
|                        |                                                  |         |
| **Gesamt**             |                                                  | **80**  |

---

## 5. Dokumentation der Projektarbeit

Die Projektdokumentation wird prozessorientiert nach dem Wasserfall-Modell strukturiert und umfasst:

**Hauptdokumentation:**

1. Deckblatt und Inhaltsverzeichnis
2. Einleitung und Projektumfeld
3. Analyse (IST-Zustand, Anforderungen)
4. Planung (Pflichtenheft, Architektur, Datenbankdesign)
5. Implementierung (Backend, Frontend, NAV-Komponenten, Schnittstellen)
6. Test (Testfaelle, Testergebnisse)
7. Deployment und Inbetriebnahme
8. Fazit und Ausblick
9. Glossar und Abkuerzungsverzeichnis

**Anlagen:**

- Pflichtenheft
- ER-Diagramm der Datenbank
- API-Dokumentation
- Klassendiagramme
- Screenshots der Anwendung
- Testprotokolle
- Benutzerhandbuch

---

## 6. Durchfuehrungszeitraum

**Geplanter Durchfuehrungszeitraum:** 03.02.2026 bis 14.02.2026

---

## 7. Projektbetreuer/-in

**Name:** Tim Frank

**E-Mail:** tim.frank@schalke04.de

**Betrieb:** FC Schalke 04 e.V.

**Adresse:**
Ernst-Kuzorra-Weg 1
45891 Gelsenkirchen

---

*Ich versichere, dass der Projektantrag keine schutzwuerdigen Betriebs- oder Kundendaten enthaelt und das Urheberrecht beachtet wird.*
