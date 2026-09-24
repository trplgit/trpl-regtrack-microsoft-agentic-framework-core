# Report Generation — Users, freehand

**[REPLACED 2026-09-23]** This file previously reproduced Sambram's fixed single-section
"dimension view" template verbatim (see git history for that version). Product direction now, same
as every other freehand dimension: composition (the plan you are given, already approved) decided
real structure/hero/emphasis for THIS tenant's own data - your job is to actually build what it
describes. Real markup, real CSS, real layout, matching what `composition_plan.blocks[].emphasis`
asks for. You have genuine freedom over illustration choice, layout, and visual treatment - this is
deliberately not a fixed document shape.

## The only things that are NOT yours to change — the shared theme

Everything else about presentation is open. These keep the report recognizably the same product
across every tenant and every dimension:

**1. Font.** `font-family:'Poppins',sans-serif` on `body`. Do not declare `@font-face` yourself —
real vendored Poppins bytes are injected automatically after you return. Just write the family
name.

**2. Colour palette.** Declare these exact tokens in `:root` and build every surface from them —
this is the real, already-shipping palette other dimension views use:
```css
:root{
  --c-bg:#f9fafb;--c-surface:#ffffff;--c-text:#3d3d3d;--c-text-2:#585858;--c-text-3:#666666;--c-grey:#999999;
  --c-border:#dbdbdb;--c-hairline:#ececec;--c-brand:#125aab;--c-light-blue:#e8f2fd;--c-mist:#f7f8fc;
  --ok:#1e8a4a;--ok-bg:#e7f5ec;--ok-stroke:#a8d3b8;--ok-fill:#2e9e5b;
  --warn:#b45708;--warn-bg:#fcf0de;--warn-stroke:#e8c79c;--warn-fill:#e0a106;
  --bad:#b3261e;--bad-bg:#fceae8;--bad-stroke:#dfa39d;--bad-fill:#d24a3a;--neu-fill:#8a8f99;
}
```
Page background is `--c-bg`, cards/panels are `--c-surface`, borders are `--c-border`, your accent
is `--c-brand`; `--ok`/`--warn`/`--bad` (with their `-bg`/`-stroke`/`-fill` pairs) are the real
severity semantics — good/caution/critical, never re-purposed for anything else. Add as many new
tokens as you like for anything these don't cover; you may not redefine these.

**3. Button/pill/chip sizing.** Any interactive or badge-like control (a tab, a tonetag, a chip,
a toggle) uses this real spacing/radius scale, not ad-hoc values:
```css
:root{ --r-sm:3.5px;--r-md:5.5px;--r-lg:9px;--r-xl:11px; --gap-sm:8px;--gap-md:12px;--gap-lg:16px; }
```

**4. Outer layout.** The page is a single centered column: `max-width:1100px;margin:...auto;padding:0 18px 26px`
(narrow it responsibly below `52rem`). Inside that column, arrange sections however the composition
plan calls for — grid, stacked cards, single-column narrative, whatever fits the real data best.

**5. Tabs, if you use them.** A single long scroll, an accordion, or a tab strip are all fine. If
you do use tabs, they must look like this real, already-shipping mechanism:
```css
.di-tabnav{display:inline-flex;align-items:center;gap:4px;padding:4px 6px;border-radius:9px;background:var(--c-mist)}
.di-tab{cursor:pointer;user-select:none;display:inline-flex;align-items:center;gap:8px;padding:6px 12px;border-radius:7px;color:var(--c-grey);font-weight:500;border:1.25px solid transparent}
.di-tab:hover{color:var(--c-brand)}
/* active tab: color:var(--c-brand); border-color:var(--c-brand); font-weight:600 */
```
Use a CSS-only radio-driven tab mechanism if you build tabs (input elements nested INSIDE the
element your `:has()` selectors target — not as preceding siblings, which silently breaks
`:has()`).

**6. Long tables — contained, never page-growing.** A "complete register"/"all users" table (not a
capped top-5/top-10 list) goes inside a fixed-height container with its OWN internal scroll — this
dimension can easily have hundreds of real users, so this matters here more than most:
```css
.table-scroll{max-height:420px;overflow-y:auto;overflow-x:auto;border:1px solid var(--c-border);border-radius:var(--r-lg)}
.table-scroll table{width:100%;border-collapse:separate;border-spacing:0}
.table-scroll thead th{position:sticky;top:0;background:var(--c-mist);z-index:1}
```
`max-height` can be any value that keeps the container to roughly one screenful (350-500px is a
reasonable range) - the TABLE scrolls, the PAGE around it does not grow to fit every row. Sticky
header keeps column labels visible while scrolling. A live search input above the table and
clickable per-column sort are a real, approved enhancement for a large register - worth doing when
the composition plan's emphasis calls for genuinely exploring hundreds of real users rather than
just listing them.

## Technical constraints (unrelated to visual freedom — security/platform requirements)

1. Exactly one HTML document, `<meta charset="utf-8">` first inside `<head>`.
2. All CSS inline. Inline `<script>` is allowed. Zero external references — no external
   stylesheets, scripts, images, fonts; no runtime network calls, no fetch/XHR/WebSocket.
3. Never build a `<script>` whose text content contains an HTML-tag-shaped substring
   (`<div`, `<p`, etc. inside a JS string) — the sanitizer that runs after you return deletes the
   whole script if it finds one. Build elements via `createElement`/`className`/`textContent`/
   `appendChild` instead of `innerHTML` with a literal tag.
4. Escape all tenant-entered text (user names included — real people's names are tenant data).
   Never place it in a `<script>`, `onclick=`, `style=`, or `href`/`src`. Semantic HTML, real
   `<table>`/`<th>`/`<caption>`, ordered headings, colour paired with a text label (never colour
   alone).
5. Never the literal word "Tenant" or a numeric tenant id anywhere in the document.

## Real vs. NOT AVAILABLE — Users dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a user row** (`dimension_rows`): `UserID`, `UserName`, `IsActive`,
`Instances`, `PerformerInstances`, `ReviewerInstances`, `OtherRoleInstances`, `Overdue`,
`OverduePct`, `ImprisonmentInstances`, `BranchesCovered`, `Logins12m`, `EngagementBand`,
`CompletedEvents`, `OnTimeEvents`, `OnTimePct`, `MedianDaysEarlyLate`, `TimingSampleSize`, `Flags`.

**Every real tenant-level total** (`dimension_control_totals`): `ScopedInstances`,
`AssignedInstancesDistinct`, `Reconciled`, `UnassignedInstances`, `OverdueInstances`,
`TenantOverduePct`, `UsersReported`, `SumOfPerUserInstances`, `TenantMedianOnTimePct`,
`TenantMedianPerformerLoad`, `InstancesWithSoleReviewer`, `PerformerUserCount`, `ReviewerUserCount`,
`TenantMedianDaysEarlyLate`, `TimingOutliersExcluded`.

| Fact | Status |
|---|---|
| "N performers, M reviewers" or a reviewer:performer ratio | **REAL, but ONLY from `PerformerUserCount`/`ReviewerUserCount` cited verbatim** — never counted from `dimension_rows` yourself (see the composition prompt's own trap note; this is a real, previously-shipped bug class). |
| "The estate has X live obligations" | **`ScopedInstances`, not `SumOfPerUserInstances`.** The latter is a sum of per-user role-holdings and is deliberately larger (an instance can have both a performer and a reviewer) — never present it as the obligation count. |
| A per-user risk mix (Critical/High/Medium/Low %) | **NOT AVAILABLE.** Use the real on-time/overdue split (`OnTimePct`/`OverduePct`) if you want a per-user composition visual — same visual slot, real data. |
| "Heads N departments" | **NOT AVAILABLE as departments.** `BranchesCovered` is a real raw branch count only — relabel accordingly, never call it departments. |
| A named "Approver" or other specific sub-role | **NOT AVAILABLE.** `OtherRoleInstances` lumps every role outside Performer/Reviewer, undifferentiated — label it generically ("other roles"). |
| A joint "imprisonment AND overdue" figure | **NOT AVAILABLE as a joint %.** `ImprisonmentInstances` and `Overdue` are separate real marginals on each row; never multiply them together and present the product as real. |
| A two-account pairing/overlap claim ("99% overlap between these two") | **NOT AVAILABLE.** No real field measures instance-set overlap between specific accounts — never state or imply one. |
| A real early/late timing pattern | **REAL when `MedianDaysEarlyLate`/`TimingSampleSize` exist for enough real users** — negative is typically early, positive typically late; never treat a `null` reading as 0. |

## Self-check before returning

- Any complete-register/all-users table sits inside `.table-scroll` (fixed max-height, its own
  internal scroll, sticky header) - the page itself never grows to fit every row.
- Every real user with meaningful load is represented somewhere - none silently dropped from a
  coverage view (a capped top-N highlight list is fine; a coverage/register section is not).
- `PerformerUserCount`/`ReviewerUserCount` are cited verbatim from `dimension_control_totals`,
  never re-counted from `dimension_rows`.
- `SumOfPerUserInstances` is never presented as the obligation/estate count.
- No department headcount claim, no named sub-role beyond Performer/Reviewer/"other roles", no
  joint imprisonment-overdue %, no cross-account pairing/overlap claim.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated, not buried or dropped.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles,
  any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak, every user name properly HTML-escaped.

Return the HTML document only.
