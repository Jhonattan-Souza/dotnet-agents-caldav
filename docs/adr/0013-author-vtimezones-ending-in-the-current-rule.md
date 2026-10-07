# ADR 0013: Author VTIMEZONEs that end in the zone's current rule

Status: Accepted

Date: 2026-10-06

Every VTIMEZONE this server writes covers the zone's exact onsets from its earliest value to the start of the rule tzdb follows today. It then ends in that rule: a yearly STANDARD and DAYLIGHT pair with RRULEs and no UNTIL, or one fixed observance for a zone without daylight saving time. The rule must match every tzdb onset up to 2200, and where no yearly rule fits, as in Africa/Cairo, the exact onsets continue up to that horizon. Each definition was previously bounded by the values of its resource, as explicit DTSTART and RDATE onsets.

A bounded definition is valid RFC 5545, but other readers treat it as the whole zone. Radicale 3.7.8 keeps the last VTIMEZONE it parsed for a TZID in a process-wide registry, and serves a definition derived from it to every later resource that names the TZID without one. One Event with a Europe/Paris override on 2026-03-08 got a single +0100 observance. After that write, Radicale stored Paris without daylight saving time for other resources and other users, an hour wrong every summer. A definition that ends in the current rule stays correct after the values that produced it, whoever reuses it.

Considered options: keeping bounded definitions and validating every IANA-named VTIMEZONE against tzdb on read was rejected for now. It would only expose the damage as unresolved values instead of preventing it, and it reverses the authority a resource-local VTIMEZONE has. Copying tzdb's full history into every resource was rejected for its size. The written bytes change, and resources written before this decision keep their bounded definitions.
