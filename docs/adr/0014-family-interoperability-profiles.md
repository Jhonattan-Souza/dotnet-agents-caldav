# ADR 0014: Name Interoperability Profiles by server family

Status: Accepted

Date: 2026-10-06

`CALDAV_INTEROPERABILITY_PROFILE` accepts the family values `radicale` and `nextcloud` instead of versioned values such as `radicale-3.8.2`. Each profile is a policy implemented by this client, backed by the evidence of one digest-pinned Verified Runtime per family. A server release then needs no configuration change. Setting the value is the operator's assertion that the deployment meets the profile's requirements, including atomic `Overwrite: F` and `CALDAV:no-uid-conflict` enforcement. If that assertion is false, Move can commit a duplicate UID, and reconciliation cannot detect it. The client never identified the connected server before this decision either; a versioned value was the same assertion with a more precise name.

Evidence follows two rules. Delegating an atomic mutation guarantee to the server requires that the guarantee pass against the family's current Verified Runtime; a runtime that fails it is not promoted. A retained input tolerance may rest on a dated historical observation plus regression tests over recorded responses, even when the current Verified Runtime no longer produces that shape. Under that rule, `radicale` admits the fail-safe free/busy representation that Radicale 3.7.8 returns. The policy is one per family and per client release, with no branches by server version. A new Verified Runtime does not change the policy on its own: enabling an operation or removing a tolerance is a separate decision. For example, `nextcloud` keeps same-Calendar Exact Move disabled because Nextcloud 34.0.3 rejected it, and a later version that accepts it does not re-enable it automatically.

Values stay lowercase and are compared ordinally. Unset or empty disables Move. Versioned values, including `radicale-3.7.8` from v0.2.4, fail startup validation with a message that names the family value; this is a breaking configuration change. Env-var descriptions in packaged metadata name only the families. Tested versions appear in the README, the profile contracts, and the dated observation records.

Considered options: probing the server version at runtime was rejected. Nextcloud exposes it through `status.php` but Radicale does not reliably, and a version number would not prove atomic UID enforcement either. A documented minimum version was rejected without separate evidence for it. Accepting the old versioned values as aliases was rejected because they keep a version in the public configuration, and only `radicale-3.7.8` was ever released.
