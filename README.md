# Actindo Middleware

Eine interne Middleware-Anwendung zur Integration zwischen NAV und der Actindo PIM-Plattform. Das Backend basiert auf **ASP.NET Core 10**, das Frontend auf **SvelteKit**. Alle Konfigurationsdaten, Jobs und Nutzer werden in einer lokalen **SQLite-Datenbank** gespeichert.

---

## Funktionsübersicht

### Produkte
- Produkte und Varianten anlegen (`create`) und aktualisieren (`save`)
- Vollständiger Produkt-Sync inkl. Varianten, Preisen und Beständen in einem Request (`full`)
- Bestandsmengen pro Lagerort aktualisieren (`inventory`)
- Preise aktualisieren (`price`)
- Produktbilder hochladen

### Kunden & Transaktionen
- Kunden anlegen und aktualisieren
- Transaktionen aus Actindo abrufen

### Job Monitor
- Alle gestarteten Jobs (synchron und asynchron) erscheinen auf der **Jobs-Seite** mit Status, SKU, Operation und Laufzeit
- Live-API-Log: Jeden Actindo-API-Call inklusive Request- und Response-Payload einsehbar
- Asynchrone Jobs (mit `await=false`) werden in einer Queue mit max. 5 parallelen Slots verarbeitet und per NAV-Callback bestätigt

### Dashboard
- Übersichtskacheln für Produkte, Kunden, Transaktionen und Medien (Erfolge, Fehler, Ø Laufzeit)
- Actindo-Verbindungsstatus und Token-Gültigkeit live im Header
- Letzte Jobs mit Such- und Fehlerfilter, Replay und Löschen

### Sync-Seite
- Vergleich zwischen NAV, Actindo und Middleware für Produkte und Kunden
- Erkennt fehlende, verwaiste und nicht übereinstimmende Einträge

### Einstellungen (Admin)
- Actindo OAuth2-Zugangsdaten (TokenEndpoint, ClientId, ClientSecret)
- Access- und Refresh-Token (manuell oder automatisch via OAuth-Flow)
- Actindo API-Endpunkte konfigurierbar
- NAV API-URL und -Token
- Lagerort-Mappings (NAV-Lager → Actindo-Warehouse-ID)

### Nutzer & Rollen
- Rollen: `read` · `write` · `admin`
- Registrierung mit Admin-Freigabe
- Login via Session-Cookie; API-Zugriff alternativ per Bearer-Token

---

## Technischer Stack

| Schicht | Technologie |
|---|---|
| Backend | ASP.NET Core 10, C# |
| Frontend | SvelteKit, Tailwind CSS |
| Datenbank | SQLite (`App_Data/dashboard.db`) |
| Auth | Cookie-Session + statischer Bearer-Token |
| Externe API | Actindo REST API (OAuth2) |

---

## API-Routen (Auszug)

### Produkte
```
POST /api/actindo/products/create
POST /api/actindo/products/save
POST /api/actindo/products/full
POST /api/actindo/products/inventory
POST /api/actindo/products/price
POST /api/actindo/products/image
GET  /api/actindo/products/active-jobs
GET  /api/actindo/products/active-jobs/{jobId}/logs
```

### Kunden & Transaktionen
```
POST /api/actindo/customers/create
POST /api/actindo/customers/save
POST /api/actindo/transactions/get
```

### Dashboard & Jobs
```
GET    /api/dashboard/summary
GET    /api/dashboard/jobs
POST   /api/dashboard/jobs/{id}/replay
DELETE /api/dashboard/jobs/{id}
```

### Sync
```
GET /api/sync/products
GET /api/sync/customers
```

### Auth & Nutzer
```
POST /api/auth/login
POST /api/auth/logout
GET  /api/auth/me
POST /api/auth/bootstrap
POST /api/auth/register
GET  /api/users
POST /api/users/{id}/role
GET  /api/registrations
POST /api/registrations/{id}/approve
POST /api/registrations/{id}/reject
```

### Einstellungen
```
GET /api/settings/actindo
PUT /api/settings/actindo
```

---

## Lokale Entwicklung

```bash
# Backend starten
cd backend
dotnet restore
dotnet run
```

```bash
# Frontend starten (separates Terminal)
cd frontend
npm install
npm run dev
```

- Backend läuft auf `http://localhost:5094`
- Im `Development`-Modus wird `App_Data/dashboard.dev.db` verwendet
- OpenAPI/Swagger erreichbar unter `/openapi`
- API-Tests per Bearer-Token:
  ```
  curl -H "Authorization: Bearer <token>" http://localhost:5094/api/dashboard/summary
  ```

Der Standard-Bearer-Token ist in `appsettings.json` unter `StaticBearer:Token` konfigurierbar.

---

## Datenbank

Die SQLite-Datenbank wird beim ersten Start automatisch angelegt. Bei Schema-Änderungen kann die Datei gelöscht werden — sie wird neu erstellt (Datenverlust beachten).

| Tabelle | Inhalt |
|---|---|
| `JobEvents` | Alle Job-Läufe mit Payload, Dauer und Status |
| `JobActindoLogs` | Einzelne API-Calls je Job |
| `Products` | Angelegte Produkte und Varianten |
| `ProductStocks` | Letzter bekannter Bestand je SKU und Lager |
| `Settings` | Actindo-Credentials, Endpoints, Mappings (JSON) |
| `Users` | Nutzerkonten mit Rolle und Passwort-Hash |
| `Registrations` | Offene Registrierungsanfragen |

---

## Erstes Setup

1. Anwendung starten
2. Unter `/register` oder via `POST /api/auth/bootstrap` den ersten Admin-Account anlegen
3. In den **Einstellungen** Actindo OAuth-Zugangsdaten eintragen und Token abrufen
4. Lagerort-Mappings konfigurieren (NAV-Lager-ID → Actindo-Warehouse-ID)
5. NAV API-URL und Token hinterlegen (erforderlich für asynchrone Jobs mit `await=false`)
