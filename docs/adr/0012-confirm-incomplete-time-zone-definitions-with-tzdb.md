# ADR 0012: Confirm Incomplete Time Zone Definitions with tzdb

Status: Accepted

Date: 2026-10-06

A resource-local VTIMEZONE remains the authority for its Temporal Values. When its observances do not chain, because one onset's TZOFFSETFROM differs from the previous TZOFFSETTO, the definition is incomplete. Its RFC 5545 reading is then used only where the IANA zone of the same TZID, directly or through the Windows mapping, gives the same instant. Elsewhere the value stays unresolved.

Two real clients write the same incomplete shape with opposite consequences. DAVx5 with ical4j 4.3.0 keeps only America/Sao_Paulo's last STANDARD rule, which is correct after daylight saving time was abolished in 2019. Radicale has emitted an America/New_York definition with a yearly DAYLIGHT rule and no return to standard time, which is an hour wrong every winter. No structural rule tells them apart, and before this decision the chain check rejected both.

Considered options: reading every incomplete definition literally by the RFC was rejected because it turns the Radicale case into silent one-hour errors. A rule that accepts only a single expired observance was rejected because it encodes one client's minification and still misplaces values the definition no longer describes. Replacing the VTIMEZONE with tzdb whenever the TZID is an IANA name was rejected earlier because it overrides definitions their authors meant. Here tzdb only decides whether a self-contradictory definition can be trusted at a given instant; a definition that does not name an IANA zone stays unresolved, as before.
