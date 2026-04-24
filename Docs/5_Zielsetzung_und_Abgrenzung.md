# Zielsetzung und Abgrenzung

## Gesamtziel

Ziel des Projekts war die Entwicklung einer Middleware, die Microsoft Dynamics NAV 2016 mit dem neuen Kassensystem Actindo verbindet. NAV sollte dabei weiterhin das führende System für Artikel, Varianten, Preise, Lagerbestände, Kundendaten und buchungsrelevante Informationen bleiben. Die Middleware sollte diese Daten entgegennehmen, für Actindo aufbereiten und an die passenden Actindo-Schnittstellen weiterleiten.

Im Gegensatz zur bisherigen direkten Anbindung sollte die neue Lösung nicht nur eine reine Weiterleitung von Daten darstellen. Sie sollte als eigenständige Zwischenschicht dienen, in der Synchronisationsvorgänge kontrolliert, protokolliert und nachvollziehbar verarbeitet werden können. Dadurch sollte die Kopplung zwischen NAV und dem Kassensystem reduziert werden. Änderungen an der Actindo-Anbindung sollten künftig möglichst in der Middleware umgesetzt werden können, ohne die fachliche Logik in NAV unnötig zu erweitern.

Ein weiteres Ziel war die Umstellung von einer überwiegend synchronen Verarbeitung auf einen kontrollierteren, asynchronen Ablauf. Anfragen aus NAV sollten über einen Synchronisationspuffer gesammelt und anschließend über Jobs verarbeitet werden. Die Middleware sollte dabei mehrere Vorgänge parallel bearbeiten können, während weitere Anfragen in einer Warteschlange verbleiben. Nach Abschluss der Verarbeitung sollten Rückmeldungen, zum Beispiel erzeugte Actindo-IDs oder Synchronisationsergebnisse, über den bestehenden generischen Webservice wieder an NAV zurückgegeben werden.

Neben der technischen Anbindung sollte auch eine Weboberfläche entstehen. Diese sollte der Softwareabteilung und den beteiligten Bereichen einen besseren Überblick über Produkte, Kunden, Synchronisationsjobs, Fehler und Einstellungen geben. Dadurch sollte die neue Schnittstelle nicht nur im Hintergrund arbeiten, sondern im Fehlerfall auch besser kontrollierbar und administrierbar sein.

## Fachliche Ziele

Ein wesentliches fachliches Ziel war die Synchronisation von Produktdaten aus NAV nach Actindo. Dazu gehören Produkte, Varianten und die dazugehörigen Actindo-IDs. Bei neu angelegten Produkten sollte Actindo eine eigene ID erzeugen, die anschließend wieder nach NAV zurückgeschrieben wird. Dadurch kann NAV bei späteren Änderungen erkennen, ob ein Produkt in Actindo neu angelegt oder nur aktualisiert werden muss.

Die Preisverarbeitung sollte gegenüber der bisherigen Lösung feiner aufgeteilt werden. Wenn sich nur ein Preis ändert, sollte nicht erneut die gesamte Produktstruktur übertragen werden müssen. Stattdessen sollte ein gezielter Preis-Sync möglich sein. Dazu zählen neben normalen Verkaufspreisen auch Mitarbeiterpreise, Mitgliedspreise und Staffelpreise. Damit sollten die in Actindo verfügbaren Preisfunktionen aus den NAV-Daten heraus genutzt werden können.

Auch Lagerbestände sollten getrennt von vollständigen Produktdaten verarbeitet werden können. Bei einer reinen Bestandsänderung sollte nur der neue Bestand übertragen werden. Dadurch wird die Datenmenge reduziert und die Verarbeitung beschleunigt. Gleichzeitig wird fachlich klarer erkennbar, ob ein Vorgang ein Produkt, einen Preis oder einen Bestand betrifft.

Ein weiteres Ziel war die Synchronisation von Kundendaten. Kundendaten aus NAV sollten an Actindo übertragen werden können, damit Verkaufs- und Buchungsprozesse auf einer einheitlichen Datenbasis arbeiten. Auch hier war wichtig, die unterschiedlichen IDs zwischen NAV und Actindo sauber zu verwalten.

Zusätzlich sollten Transaktionen aus Actindo verarbeitet werden. Verkäufe aus dem neuen Kassensystem sollten so aufbereitet werden, dass daraus in NAV passende Belege entstehen und die bereits vorhandene Buchungslogik genutzt werden kann. Die eigentliche Buchungsfunktionalität war nicht neu zu entwickeln, die vorgelagerte Belegerstellung und Übergabe der Actindo-Transaktionen war jedoch Teil des Projekts.

Ergänzend dazu sollte eine Gutscheinschnittstelle auf NAV-Seite umgesetzt werden. Actindo sollte dadurch Gutscheine an der Kasse prüfen, einlösen und bei Bedarf stornieren bzw. rückabwickeln können. Diese Funktion war notwendig, damit auch Gutscheinprozesse in die neue Kassenlandschaft integriert werden können.

## Technische Ziele

Technisch sollte die Middleware mehrere REST-Endpunkte bereitstellen, die von NAV angesprochen werden können. Dazu zählen Endpunkte für das Erstellen und Aktualisieren von Produkten und Kunden sowie getrennte Endpunkte für Preis- und Bestandsänderungen. Zusätzlich sollten Endpunkte für Transaktionen und Produktbilder vorgesehen werden. Die Schnittstellen sollten JSON-Daten entgegennehmen, validieren, verarbeiten und anschließend an die jeweils passenden Actindo-Endpunkte weitergeben.

Die Authentifizierung gegenüber Actindo sollte zentral in der Middleware umgesetzt werden. Während die alte Shopware-Anbindung mit einem API-Key als Bearer-Token arbeitete, nutzt Actindo einen OAuth2-Ablauf. Die Middleware sollte daher Zugriffstoken verwalten und bei Bedarf erneuern können. Diese Logik sollte bewusst nicht verteilt in NAV umgesetzt werden, damit sie besser wartbar und kontrollierbar bleibt.

Ein weiteres technisches Ziel war die Speicherung von Konfigurationen, Jobs, Protokollen und relevanten Synchronisationsdaten in einer lokalen Datenbank. Dafür wurde SQLite vorgesehen. Die Datenbank sollte unter anderem Einstellungen, Produktinformationen, Benutzer, Jobdaten und Protokolle aufnehmen. Dadurch kann die Weboberfläche den aktuellen Zustand anzeigen und vergangene Synchronisationsvorgänge nachvollziehbar machen.

Auf NAV-Seite sollten die notwendigen Tabellen und Codeunits erstellt werden. Dazu zählt insbesondere der Synchronisationspuffer, in dem anstehende Vorgänge gesammelt werden. Eine neue Collector-Logik sollte erkennen, welche Daten geändert wurden, und daraus passende Synchronisationsaufträge erzeugen. Die Verarbeitung sollte anschließend über Job-Queue-Mechanismen erfolgen, damit die Daten nicht unmittelbar blockierend verarbeitet werden müssen.

Für den Betrieb sollte die Anwendung containerisiert bereitgestellt werden. Ziel war eine Docker-basierte Bereitstellung mit getrennten Instanzen für Test und Live. Dadurch können Änderungen zunächst in einer Testumgebung geprüft werden, bevor sie im produktiven Umfeld eingesetzt werden. Diese Trennung sollte sowohl zur Stabilität als auch zur besseren Wartbarkeit der Lösung beitragen.

## Qualitätsziele

Ein zentrales Qualitätsziel war die Nachvollziehbarkeit der Synchronisationsvorgänge. Jeder relevante Vorgang sollte protokolliert werden, damit im Fehlerfall erkennbar ist, welche Daten gesendet wurden, welcher Endpunkt verwendet wurde und welche Antwort vom Zielsystem kam. Dadurch sollte die Fehlersuche gegenüber der bisherigen direkten Anbindung deutlich vereinfacht werden.

Die Lösung sollte außerdem so gestaltet werden, dass fachlich unterschiedliche Vorgänge getrennt betrachtet werden können. Ein Preisupdate, eine Bestandsänderung, ein Produkt-Sync oder eine Kundensynchronisation sollten nicht als ein großer, schwer durchschaubarer Gesamtprozess erscheinen, sondern als jeweils eigener Vorgang nachvollziehbar sein. Das verbessert sowohl die technische Wartung als auch die fachliche Kontrolle.

Ein weiteres Qualitätsziel war die Reduzierung unnötiger Datenübertragungen. Wenn sich nur ein einzelner Teil eines Produkts ändert, sollte auch nur dieser Teil übertragen werden. Dadurch werden Schnittstellenaufrufe schlanker, die Verarbeitung wird schneller und Fehler lassen sich gezielter eingrenzen.

Die Middleware sollte zudem erweiterbar bleiben. Da bei Schnittstellenprojekten erfahrungsgemäß während der Einführung neue Anforderungen oder Anpassungen entstehen, sollte die Lösung so aufgebaut werden, dass weitere Endpunkte, zusätzliche Einstellungen oder neue Verarbeitungsschritte ergänzt werden können, ohne die gesamte Architektur neu aufbauen zu müssen.

Auch die Trennung von Test- und Livebetrieb war ein wichtiges Qualitätsziel. Neue Anpassungen sollten zunächst in einer Testumgebung geprüft werden können. Dadurch wird das Risiko reduziert, dass fehlerhafte Änderungen direkte Auswirkungen auf den produktiven Kassenbetrieb haben.

Schließlich sollte die Lösung für die beteiligten Bereiche bedienbar und kontrollierbar sein. Die Softwareabteilung sollte Einstellungen und Jobs einsehen können, der IT Service sollte bei Problemen eine erste Orientierung erhalten und die Buchhaltung sollte von einer zuverlässigeren und stärker automatisierten Belegverarbeitung profitieren.
