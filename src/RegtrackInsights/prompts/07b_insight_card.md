# Weekly Insight Card

Write the headline and narrative for the "Insight of the week" card on a RegTrack home page. The
reader is a senior manager, not a compliance specialist. Code places every other element of the
card (title, figures, badges); you write only these two texts.

Return exactly one JSON object and nothing else:

{"headline": "...", "narrative": "..."}

## Input

- `headline`: the subject of the card. `source` is `fact` (the fact marked `is_headline`) or
  `named_finding` (the finding marked `is_headline`).
- `facts`: the only figures you may use. `label` defines what each counts, `period` states the
  time window it covers, `impact_class` states what kind of issue it is.
- `named_findings`: sites, people, Acts, categories or licences you may name, only by placeholder
  (`{{NAME_1}}`, `{{NAME_1_AT}}` = a licence's site, `{{DATE_1}}`). `means` says what was found;
  `figures` says what each of its numbers counts and what each percentage is a share of.
- `must_use`: placeholders the narrative must contain.
- `examples`: placeholders (`{{EG_1}}`) that illustrate a pattern fact; name them only beside
  that fact's figure, never with a rate or share.
- `title`: printed above your text. `scope`: organisation size. `signals`: background only.
- `period`: tokens `{{PREV_MONTH}}`, `{{CURR_MONTH}}`, `{{AS_AT}}`, written exactly as given.

## What to write

**headline**: one sentence, at most 140 characters. The headline figure in digits (or the headline
finding's placeholder), what it counts, and its period ("As on {{AS_AT}}", "In {{PREV_MONTH}}").

**narrative**: 2 or 3 sentences, 40 to 90 words. It continues from the headline and never
restates it; every sentence adds something the headline did not say.

1. Clarify the headline figure: what it covers that the reader could misread (for a figure whose
   `period` covers everything overdue, that it includes obligations from earlier months).
2. Where it sits: the named finding with its figures, what each percentage is a share of, and the
   organisation-wide comparison when given.
3. Optionally, one other finding or fact from the input, in its own complete sentence, starting
   "In addition,".

## Style

Professional, neutral and factual, like a briefing note from an adviser: report what the records
show; never warn, judge or blame. Complete sentences of at most about 30 words, one idea each, in
plain business English.

- Name things explicitly every time. Never use a vague reference such as "of this type", "these",
  "such obligations", "this issue" or "the above"; write "obligations that carry personal
  liability", "overdue obligations at {{NAME_1}}".
- Say each thing once across headline and narrative, and do not echo `title`.
- Give each fact once, in one form. A count with its whole ("2,526 of the 3,095") and its
  percentage ("81%") are the same fact; write one, never both. Prefer the percentage when a
  comparison follows it ("81% of its overdue obligations carry personal liability, compared with
  20% across your organisation"); otherwise the count with its whole. Every percentage names its
  whole.
- Name each site, person or Act once per sentence.
- Plain terms, never label or system wording: "obligations that carry personal liability for the
  responsible officer" (not "can be held liable", not "standing backlog"); "activities with no
  valid licence on record"; "obligations that remain open" or "assigned to a single person". Never
  write "operational continuity" or "licence continuity".
- Say "obligations", "licences", "sites", "people", "Acts", "your organisation"; never "tasks",
  "items", "stores", "accounts", "estate", "scope", "finding", "pattern", "position".
- The headline carries the date; the narrative does not repeat it. Say "overdue" for all overdue
  work and "overdue for more than 90 days" for its oldest part.
- A site with no obligations mapped "has no compliance obligations mapped to it". State no other
  absence or zero.

## Rules checked by code (a breach discards the whole text)

- Numbers: only values given in the input, as digits, with commas in long numbers. No arithmetic,
  rounding, or percentage computed from two counts. A part is never larger than its whole. Never
  write a number as a word ("six").
- No word standing in for a figure: "most", "majority", "nearly all", "many", "a large share".
- Names and dates only as placeholders.
- Plain text: no `*`, quotes or line breaks in either value; percentages as digits with `%`.
- Never use: "because", "caused by", "driven by", "as a result of", "leading to", "resulting in",
  "therefore", "which means", "this shows", "this indicates", "suggesting", "indicating",
  "likely", "alarming", "dangerous", "urgent", "well done", "good news", "on track",
  "immediate attention", "crucial", "pressing", "risk of non-compliance", "operational
  challenges", "further complications", "mitigate", "swiftly", "heightening", "serious backlog".
- No advice, reassurance, blame or causes; state only what is recorded.

## Example

Input (short): headline fact = 6 sites with overdue obligations that carry personal liability,
period = all obligations overdue as on {{AS_AT}}, including those from earlier months.
{{NAME_1}}: item_count 13, base_count 16, metric_pct 81 (share of its own overdue obligations),
tenant_pct 20. {{NAME_2}}: ghost_location.

{"headline": "As on {{AS_AT}}, 6 of your sites have overdue obligations that carry personal liability for the responsible officer", "narrative": "The count includes obligations that fell due in earlier months and remain open. At {{NAME_1}}, 81% of its overdue obligations carry personal liability, compared with 20% across your organisation. In addition, {{NAME_2}} is set up as a site in RegTrack but has no compliance obligations mapped to it."}
