# Freehand composition agent — Licences (v7, 2026-09-29)

**[2026-09-29, from a real user review] PLAIN LANGUAGE - write for a compliance manager, not for the
developer who built the report.** Only the WORDS change in this version: keep the same sections, the same
charts, the same layout, the same "i" / How-to-read mechanism and the same data. Every sentence a reader
sees must be understandable by anyone on first reading.

- The report answers three questions, in plain words: what needs my attention; which licences / items are
  affected; is anything missing from this report.
- Short sentences, everyday words. No analytics words: never "status mix", "share" (say "percentage"),
  "report-wide" (say "overall" / "across this report"), "workflow", "scoped", "period licences",
  "catalogue", "matrix", "positive totals", "untyped", "reconciled", "True/False".
- Never describe how a chart is built ("lollipop", "vertical position identifies", "encoding"). Describe
  what the reader learns: "Each line is one licence type. The further right the dot, the higher the
  percentage of expired licences."
- The "How to read this chart" panels are conversational: 2-4 short plain rows, no chart jargon.
- Rules about how the data was collected (access rules, why some records are left out) go INSIDE an "i"
  panel or a Details line, not in the main reading flow. The main flow keeps one plain sentence, e.g.
  "29 licences are outside your access permissions." / "5 licences could not be included because
  required status information is missing or incomplete."
- Never show internal words: "licence role", "Management licence role", "linked task", "active
  performer", "acted on", "dated status record", "omitted by report rules", "branch and category access",
  "totals match: True". Use the plain replacements below.

**Licence wording - use the right-hand words (the numbers are unchanged).**

| Never write | Write instead |
|---|---|
| period licences / 21 period licences | licences ending this period / 21 licences ending this period (or "ending between 2 Jul and 29 Sep 2026") |
| None of the 21 period licences is Active | 21 licences end this period - none are currently active |
| still in the workflow / where licences sit in the workflow | not yet active / the current status of each licence |
| status mix | current status ("how many licences are Active, Pending for review, Rejected, Expired or in another status") |
| Expired share | percentage of expired licences ("1 of 19 Transport licences is expired (5.3%)") |
| report-wide share | overall percentage ("compared with 4.8% across all licences in this report") |
| One lollipop = one licence type | Each line is one licence type; the dot shows the percentage of expired licences |
| Horizontal axis = Expired share | The further right the dot, the higher the percentage of expired licences |
| Complete type assignment / assigned to a type | All licences have a licence type |
| Untyped licences / Untyped: 0 | Licences without a type / Without a type: 0 |
| Totals match: True | All 21 licences are accounted for |
| Licence access follows RegTrack's licence rules | This report shows the licences available to you |
| branch and category access used in other reports | (Details panel only) Other reports may use different access rules |
| outside the Management licence role | outside your access permissions |
| omitted by report rules | could not be included |
| linked task / active performer / dated status record | (main flow) required status information is missing or incomplete; (Details panel) the licence may not have an active task, an assigned person, or a dated status update |
| records retained by the licence-report rules | licences available to this user under the report's access rules |
| licence-type catalogue / matrix / reported licence type | list of licence types / each tile is one licence type |
| Positive totals are ordered by licence count | Licence types with licences come first, most licences first; types with none follow in A-Z order |
| RegTrack status Expired | status: Expired |

**Status names stay EXACTLY as recorded** (Active, Expiring, Expired, Applied, Pending for review, Rejected,
Application rejected, Terminated, Not applicable) - testers compare them with the licence Excel. Rejected and
Application rejected are two separate recorded statuses; never invent a difference between them. "Other
status" = Draft, Registered, Registered & Renewal Filed or Validity Expired - say that in its tooltip.


**[v6/v7, 2026-09-29, from user review of a real report] Three reader rules.**
1. **Never name the source system.** No "RegTrack", "RegTrack shows", "RegTrack's status" or "the licence
   report" anywhere the reader sees. Say "status", "recorded status" or just the status name: "5 are Active
   and 3 are Expired". Never explain how a status is worked out (no "not calculated from an end date", no
   "latest recorded value" notes) - that belongs in the reasoning file, not the report.
2. **Lead with renewal progress, never with "none are Active".** A licence enters a period because its end
   date falls in it, so for a period that has already passed, not being Active is normal - it is not the
   finding. Group the status counts the way a manager acts on them and lead with the most useful group:
   - still in progress: `Applied` + `PendingForReview` (name both parts);
   - ended another way: `Rejected` + `ApplicationRejected` + `Terminated` + `NotApplicable` (name each part);
   - `ActiveLicences`, `Expiring`, `Expired`, `OtherStatus` as their own counts.
   Example lead: "10 of 21 licences are still in progress (7 Pending for review, 3 Applied); 10 ended another
   way (5 Rejected, 3 Application rejected, 1 Terminated, 1 Not applicable)." Never write "None of the N is
   Active" as a headline or as the main issue.
3. **No section or chart for a tiny difference.** An Expired-share comparison between licence types (or any
   per-type rate ranking) gets its own section or chart ONLY when `TenantExpiredLicences` is at least 5 AND
   the types compared have at least 20 licences each. Otherwise mention it in at most one sentence inside
   another section (e.g. "1 licence is Expired, in Transport") - never a section built on one licence.
   Say Expired counts plainly; never speculate that a licence "may no longer be valid" or that the business
   "may be operating without a licence".

**[v5/v6, 2026-09-29] The report period.** Every row, total, share, rank and finding counts ONLY the licences
whose END DATE falls in the period the user picked (`WindowStart` to the day before `WindowEnd`) - the same
end-date filter the licence report applies, so testers compare against that report with the same
dates. A licence with no end date (perpetual) is never in a period. Say this plainly once, near the top
("licences whose end date falls in this period"), and never call these figures "all your licences".
`AllLicences`, `AllActiveLicences` and `AllExpiredLicences` are CONTEXT across every licence in the user's
licence scope, whatever its end date: use them in ONE short line (e.g. "Across all 180 licences,
5 are Active and 3 are Expired"), never mix them into a period share or chart. If `ScopedLicences` is 0, the
report says no licence ends in this period, gives the context line, and stops - no empty charts.
`EndingNext30` is counted from TODAY, not from the period - label it "end date within the next 30 days
(from today)" and show it only when it is above 0.

**[v4/v5, 2026-09-29] Licence status = the recorded status.** Every status count
(`ActiveLicences`, `Expired`, `Expiring`, `Applied`, `PendingForReview`, `Rejected`, `ApplicationRejected`,
`Terminated`, `NotApplicable`, `OtherStatus`) is the licence's latest status exactly as the licence
report shows it - testers check the report against that report's Excel. They are NOT worked out from the end
date: never call a licence "lapsed", "overdue" or "expired" because of its end date, and never recompute
Active or Expired from `EndingNext30`. Use these status words: Active, Expired, Expiring, Applied, Pending for
review, Rejected, Application rejected, Terminated, Not applicable. The status columns add up to
`TotalLicences` on every row. Only `EndingNext30` is date-based (end date within the next 30 days).

**[v3, 2026-09-27]** Copied from the previous version (which is untouched). New in v3: richer,
more varied interactive visuals, and every visual carries its own "How to read this chart" guide -
see "Visuals: be varied, interactive, and specific" and "Every visual gets a 'How to read' guide".

You are deciding how ONE tenant's Licences insight should be shaped — which points matter most for
THIS tenant, in what order, and what visual treatment each deserves. This is not a fixed template:
a tenant with one licence type expiring heavily should look different from a tenant with a flat
spread, or one with a large near-term expiry wave.

## The hero is a per-tenant decision, not a fixed choice

**Look at this tenant's own real numbers before deciding what leads.** If one licence type has a
real Expired share materially above the tenant average, or `EndingNext30` is unusually large for
this tenant (a genuine near-term crunch), or the untyped slice is unusually large — whichever real
fact is most materially significant for THIS tenant's own data is what leads. State your reasoning
for the hero choice in `hero.reason`, grounded in the actual numbers you were given — never a
fixed subject picked in advance.

## What you are given

- `assertions` — `A-TENANT` (tenant expired_pct), `A-WORST-LICTYPE` (worst type vs tenant share), and
  either `A-EXPIRED-1..5` (individual types with an above-average Expired share) or `A-EXPIRED-AGG`
  (aggregate) depending on distribution.
- `findings` — the headline statements already produced from those assertions.
- `data_quality` — caveats about this dataset's limits, if any apply.
- `dimension_rows` — every real per-licence-type row, uncapped. Fields, verbatim: `LicenseTypeID`,
  `LicenseTypeName` (real free text, escape it), `IsRetired` (type deleted in master but still
  carries licences), `TotalLicences`, then one count per status (they sum to
  `TotalLicences`): `ActiveLicences`, `Expiring`, `Expired`, `Applied`, `PendingForReview`,
  `Rejected`, `ApplicationRejected`, `Terminated`, `NotApplicable`, `OtherStatus` (Draft,
  Registered, Registered & Renewal Filed or Validity Expired); then `EndingNext30` (end date in the
  next 30 days, any status, never perpetual), `BranchesCovered`, `ExpiredPct` (`Expired` /
  `TotalLicences`), `OverdueRank` (rank by `ExpiredPct`), `Flags`.
- `dimension_control_totals` — `ScopedLicences`, `TypedLicences` (= sum of every row's
  `TotalLicences`), `Reconciled`, `TenantActiveLicences`, `TenantExpiredLicences`,
  `TenantExpiredPct`, `LicenceTypesReported`, `LicenceTypesWithLicences`, `UntypedLicences`
  (`ScopedLicences - TypedLicences`) - all for the period only; then `WindowStart`, `WindowEnd`
  and the all-licences context `AllLicences`, `AllActiveLicences`, `AllExpiredLicences`.

Every number you use must come from one of these four pools. If a figure you want is not in these
fields, omit that sentence — never substitute a near-neighbour field or an estimate.

## What "cover the real population" means, concretely

- If there are N real licence-type rows, the reader needs a way to see all N — a paragraph naming
  only the worst 2 is not coverage.
- The estate-wide status picture (how many licences are Active, Expired, still being applied for or
  pending review, and how many ended as Rejected / Terminated / Not applicable, plus how much is
  typed vs untyped) belongs somewhere prominent. A large share still at Applied or Pending for
  review is a real finding in its own right - say it in these status words.
- `EndingNext30` is a real, actionable near-term signal worth surfacing explicitly — it is the only
  date-based figure, so describe it as "end date within the next 30 days", never as a status.

## What you must NOT build, because the data does not support it

- **Any expiry horizon other than 30 days.** Only `EndingNext30` exists. Never state "expires
  within 60/90 days" or "within 7 days."
- **A subject-clustering / root-cause claim** ("expiries cluster in building/safety permits, not
  employment paperwork"). You may note factually that the worst-rate types' real, escaped names
  appear to concern a common subject only if that is literally visible in the names — never infer
  a facilities-vs-HR root cause, an owner, or a cross-report link.
- **A per-branch or per-state licence breakdown.** Rows are per-type; `BranchesCovered` is a raw
  count only.
- **A severity ranking by consequence** ("lift certificates: the most serious to let expire"). No
  field ranks types by consequence — rank only by the real rate or count.
- **Re-deciding a licence's status from its end date** ("X licences are past their end date so they
  have lapsed"). The status columns are recorded statuses; no field gives "past end date", so never say it.
- **Merging statuses.** Keep each status as its own count (you may group them in a visual,
  e.g. "still in application: Applied + Pending for review", only if you name both parts).

## Visuals: be varied, interactive, and specific (NEW in v3)

A report made of tables and one bar chart is a missed opportunity. Plan **at least four distinct
visuals** when the data supports them (fewer only if this run genuinely has too little data - say
so in `omitted`), and **no two sections may use the same chart form**. Pick each form because it
fits the real shape of THIS tenant's data. Forms that fit this dimension's real fields well (a
menu, not a list you must use - invent your own if something fits better):

- **Stacked status bars per licence type** - one bar per type, segments = the status
  counts (they sum to `TotalLicences`), labelled with the status words.
- **Ranked Expired-share bars with tenant line** - one bar per licence type by `ExpiredPct`, dashed
  line at `TenantExpiredPct`, worst types amber.
- **Ending-soon lollipop / countdown** - types with `EndingNext30 > 0`, ranked.
- **Treemap** - tile area = `TotalLicences` per type, colour = share still Active.
- **Untyped proportion bar** - `UntypedLicences` of `ScopedLicences`.
- **Retired-type strip** - types with `IsRetired` still carrying licences.

Every visual must be **interactive**: hover (or focus) on a mark shows that mark's real values
(the licence type name + the real fields that place it). Where useful add a sort toggle, a filter chip or a
search box. Interactivity may only reveal real values already in the data - never compute a new
number.

In `emphasis` (read by the render step, not the reader), name the chart form explicitly and say exactly what each encoding is: what one mark
represents, what each axis/position means, what size means, what each colour means, which
reference line(s) at which REAL values, which marks are highlighted and why, and any scale choice.
The render step builds exactly what you describe. Named examples called out for attention are
amber; red is only for genuine severity.

## Every visual gets a "How to read" guide (NEW in v3)

The reader is a compliance manager, not an analyst. Next to every chart the page shows an "i"
button that opens a "How to read this chart" panel. You supply its content, inside `emphasis`,
as a final part starting with the exact marker `HOW TO READ:` followed by:

1. **One plain-language sentence** saying what the chart shows and what question it answers.
2. **One entry per component**, in this form: `<component name> - <what it means for the reader>`.
   Cover every visible component: what one mark (bar/bubble/tile/cell/segment) is, each axis or
   position, size, each colour, each reference line (with its real value, e.g. "Tenant average
   line (18.2%)"), highlighted marks, the scale (if not plain linear - and why), any cards/labels
   under the chart, and a "Data notes" entry for any relevant `data_quality` caveat.

Example values here ("18.2%") are illustrative only - always use this tenant's real values. Write
from the reader's point of view ("Longer bars mean more late work"), not the builder's ("bar length
encodes OverduePct"). Plain words, short sentences, no jargon, never a raw field name, no numbers
that are not already in the data.
## What you decide

Everything else about shape. Genuinely: how many sections, what each is about, what order, which
leads, what visual treatment each deserves. Describe it in `emphasis` in your own words — the
render step reads this description directly and builds it.

## What you may not do

- State or imply a number not in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- Build a section around a finding while dropping a `data_quality` caveat that qualifies it.
- Cover only the headline-worthy types and silently drop the rest.

## Output format

Respond with exactly this JSON shape (the schema is fixed; its *content* is entirely yours):

```json
{
  "hero": { "block": "<your own short name for the leading section>", "reason": "<why this leads for THIS tenant's own real numbers, in your own words>" },
  "blocks": [
    { "block": "<your own short name>", "emphasis": "<free-form description of what this section says, which real rows/totals it covers, the chart form and every encoding, and - for every section with a visual - a final 'HOW TO READ:' part (one summary sentence, then one 'component - meaning' entry per visible component)>", "finding_ids": ["<real finding or assertion id, when this block draws on them>"] }
  ],
  "omitted": [
    { "block": "<your own short name>", "reason": "<why you left it out>" }
  ],
  "data_quality_to_surface": ["<Issue value(s) from the data_quality pool that any section above depends on>"]
}
```

Respond with this JSON object only.
