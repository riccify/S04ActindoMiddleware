# Dynamics NAV - Actindo Integration

## Uebersicht

Middleware zwischen Dynamics NAV und Actindo (Kassensystem). NAV sendet Produkte, Kunden, Preise und Bestaende an die Middleware, die diese dann an Actindo weiterleitet. Das Frontend gibt Ueberblick ob beide Systeme synchron sind.

## Architektur

```
NAV  -->  Synch Buffer  -->  Job Queue  -->  Middleware  -->  Actindo
                                                  |
                                            Frontend (Status)
```

## Codeunits

### S04 Actindo Core (50090)
- Basis-Kommunikation mit der Middleware
- HTTP-Requests via RestSharp
- Bearer Token Authentifizierung
- TLS 1.2 erzwungen
- Liest Webservice-Setup aus Record 60073

### S04 Actindo Service (80092)
- Hauptlogik fuer Synchronisation
- Verarbeitet Synch Buffer Eintraege
- Endpoints: CREATE_PRODUCT, SAVE_PRODUCT, CREATE_CUSTOMER, SAVE_CUSTOMER, SAVE_INVENTORY, SAVE_PRICE, GET_TRANSACTIONS
- Erstellt JSON-Payloads fuer Produkte/Kunden
- Try-Funktionen fuer Fehlerbehandlung

### S04 Actindo Middleware (81122)
- ID-Synchronisation zwischen NAV und Actindo
- SetProductIDs: Setzt Actindo POS ID auf Item Record
- SetCustomerID: Setzt Actindo Customer ID
- GetCustomerIDs: Liefert alle Kunden mit Actindo ID
- ClearProductIDs: Entfernt alle Actindo IDs (Admin-Funktion)

### S04 Actindo Jobs (66088)
- Job Queue Handler
- Parameter: PROCESSSYNCHBUFFER, GETTRANSACTIONS, PROCESSTRANSACTIONS, COLLECTDATA
- Ruft entsprechende Funktionen in S04 Actindo Service auf

### S04 Voucher Services (60077)
- Gutschein-Verwaltung fuer Actindo POS
- ChargeGiftCard: Gutscheinkarte aktivieren oder Restgutschein anlegen
- CheckGiftCard: Gutschein-Status und Balance pruefen
- PayUsingGiftCard: Mit Gutschein bezahlen
- ReverseGiftCardTransaction: Transaktion stornieren
- Status-Codes: ACTIVE, INACTIVE, BLOCKED, EXPIRED
- Error-Codes: 1=Invalid Card, 2=Invalid Amount, 3=Not enough Balance, 4=Not charged, 5=Not redeemed

### S04 Actindo Image Handler (81000)
- Produktbilder an Actindo senden
- Base64-Encoding der Bilder
- MIME-Type Erkennung
- Batch-Verarbeitung aller Produkte mit Actindo ID

### S04 Sync Collect (81123)
- Sammelt relevante Eintraege fuer Synch Buffer
- CollectRelevantEntriesForSyncBuffer

## Tabellen

### S04 Actindo Synch. Buffer (80091)
Queue-Tabelle fuer alle Synchronisations-Requests

| Feld | Typ | Beschreibung |
|------|-----|--------------|
| No. | BigInteger | Lfd. Nummer (AutoIncrement) |
| Request Type | Text50 | Anfragentyp |
| Status | Option | Open, In Progress, Processed, Error |
| Request Timestamp | DateTime | Zeitpunkt der Anfrage |
| Processing Timestamp | DateTime | Zeitpunkt der Verarbeitung |
| Processing Duration | Duration | Dauer der Verarbeitung |
| Request Document | BLOB | JSON Request |
| Response Document | BLOB | JSON Response |
| Error Message | Text250 | Fehlermeldung |
| Reference No. | Code50 | Item-Nr oder Customer-Nr |
| Direction | Option | Inbound, Outbound |
| Endpoint | Text30 | z.B. CREATE_PRODUCT |

## Wichtige Records

| Record | Beschreibung |
|--------|--------------|
| Item (27) | Artikel mit Feld "Actindo POS ID" |
| Item Variant (5401) | Varianten mit "Actindo POS ID" |
| Customer (18) | Debitoren |
| S04 Customer Extended (60018) | Erweiterung mit "Actindo Customer ID" |
| Webservice Setup (60073) | Basis-URL und Bearer Token |
| Webservice Endpoints (80090) | Endpoint-URLs |
| Actindo Setup (80092) | Synch-Einstellungen (aktiv/inaktiv) |
| SP - Voucher (5091710) | Gutscheine |
| SP - Voucher Entry (5091712) | Gutschein-Posten |

## Sync-Logik

1. Produkt/Kunde wird geaendert
2. TransferToSynchBuffer() prueft ob Actindo ID existiert
   - Ja: SAVE_PRODUCT/SAVE_CUSTOMER (Update)
   - Nein: CREATE_PRODUCT/CREATE_CUSTOMER (Neu)
3. JSON-Payload wird erstellt und in Buffer geschrieben
4. Job Queue ruft ProcessSynchBufferEntries() auf
5. Buffer-Eintraege werden nacheinander abgearbeitet
6. Response wird gespeichert, Status auf Processed oder Error

## Middleware Endpoints (von NAV aufgerufen)

| Endpoint | Methode | Beschreibung |
|----------|---------|--------------|
| /api/actindo/products/create | POST | Neues Produkt anlegen |
| /api/actindo/products/save | POST | Produkt aktualisieren |
| /api/actindo/customers/create | POST | Neuer Kunde |
| /api/actindo/customers/save | POST | Kunde aktualisieren |
| /api/actindo/products/inventory | POST | Bestand setzen |
| /api/actindo/products/price | POST | Preis setzen |

## NAV API Endpoints (von Middleware aufgerufen)

Ueber navapi mit Bearer Token:
```
URL: https://notify.schalke04.de/nav/test/navapi
Method: POST
```

| requestType | Beschreibung |
|-------------|--------------|
| actindo.product.id.get | Alle Produkte mit Actindo ID holen |
| actindo.product.id.set | Actindo IDs in NAV setzen |
| actindo.customer.id.get | Alle Kunden mit Actindo ID |
| actindo.customer.id.set | Actindo ID fuer Kunde setzen |

## Voucher API

Die Voucher API wird direkt von Actindo aufgerufen (nicht ueber Middleware):

| Funktion | Beschreibung |
|----------|--------------|
| ChargeGiftCard | Aktiviert Gutscheinkarte oder legt Restgutschein an |
| CheckGiftCard | Prueft Status und Guthaben |
| PayUsingGiftCard | Einloesung/Bezahlung |
| ReverseGiftCardTransaction | Storno |

## Entwickler-Referenzen

- Developer: s04-jra, s04-frank, s04-mbe, s04-jhc, s04-jsc, s04-pgu, s04-fdu
- Reference-IDs: DEV-68, DEV-69, DEV-70, DEV-76
- Version List: S04.00 - S04.05, ACTINDO
