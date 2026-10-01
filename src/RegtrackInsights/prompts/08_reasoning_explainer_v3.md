# Reasoning file for testers (v3, 2026-09-29)

**[v3]** Replaces `08_reasoning_explainer_v2.md` (kept untouched). Real user feedback: the file must be a
PLAIN TEXT file (it is now served as .txt, not Markdown) and written in very easy English that anyone can
read - short sentences, everyday words. Same job as v2: explain every number on the report.

## Who reads this

A tester has the finished report open next to this file. English may not be their first language. They want
to check every number on the report. For every number they need:
1. what it means - one short, simple sentence;
2. how we got it - the sum in words, then with the real numbers;
3. where to find it - the section title copied from the report, and the data field name.

They also want to know why the report looks the way it does (why this section comes first, why this chart).

## What you are given (one JSON object)

- `report_text` - the visible text of the FINISHED report. This is your checklist.
- `numbers_on_report` - every number in `report_text`, found by code. EVERY one must be explained in part 3
  (a year or a date inside a sentence needs no entry).
- `dimension_rows` - every real row of source data, with its real field names.
- `dimension_control_totals` - the real overall totals.
- **OR**, for the combined Entity report only, `dimension_data` instead of the two fields above - one
  object per contributing area (Location, Users, Licence, Risk, and others), each with its OWN rows and
  control totals, in the very same shape `dimension_rows`/`dimension_control_totals` would have had for
  just that one area. Treat each key the same way you would treat `dimension_rows`/
  `dimension_control_totals` on their own - just remember which area a number came from, and say so in
  "Where to find it" (e.g. "Data field: TenantExpiredPct (from the Licence data)").
- `assertions`, `findings` - computed comparisons (rank, gap to average). Use them for sums and values, but
  NEVER show their ids (`A-...`, `F-...`).
- `composition_plan` - why the first section leads (`hero.reason`), what each section shows (`emphasis`),
  what was left out (`omitted`).
- `reasoning_log` - the models' short notes on their thinking (may be empty).
- `tool_invocations` - any extra data look-up the model made (usually none).
- `data_quality` - known limits of this data.

Never write a number, a name or a reason that is not in this JSON. If you cannot trace a number, say so in
part 5 - never guess. No SQL, no queries, no code.

## Plain text only - this is NOT Markdown

- No `#` headings, no `**bold**`, no tables, no `|` pipes, no backticks, no code blocks, no bullets made of
  `*`. Use CAPITAL LETTERS for part titles, numbers (1. 2. 3.) for lists and "- " for small lists.
- Keep lines short. Leave one empty line between entries.
- Field names are written plainly after "Data field:", e.g. "Data field: ScopedLicences".

## Very easy English

- One idea per sentence. Most sentences under 15 words.
- Everyday words: "licences ending in this period" (not "period licences" or "scoped"), "percentage" (not
  "share" or "rate"), "overall" (not "report-wide"), "left out" (not "omitted"), "matches" (not
  "reconciles"). No words like workflow, catalogue, matrix, assertion, encoding, robust, holistic, leverage.
- Explain a sum like a teacher: "We divide 1 by 19 and multiply by 100. The answer is 5.3%."
- Never praise the report or the models. No closing summary.

## Layout (keep it short - about 2 pages)

~~~
HOW THIS REPORT WAS MADE - {dimension}

Period: {from the report text or the period data-quality note, or "no period" if there is none}.
Data used: {number of rows and what one row is, e.g. "108 licence types"} and {the main total, e.g.
"21 licences ending in this period"}. When you were given `dimension_data` instead (the combined Entity
report), list every area it drew from instead of one row count, e.g. "Location (19 branches), Users (305
people), Licence (108 licence types), Risk, and 4 more areas."

1. IN SHORT
2 or 3 simple sentences: what the report is about, its main message, the one thing to check first.

2. WHY THE REPORT LOOKS LIKE THIS
1. What data was used.
2. Why the first section comes first (from hero.reason, in easy words).
3. One line for each section: what it shows and why that chart was used.
4. What was left out and why (or "Nothing was left out.").
5. Any extra data look-up (or "No extra look-ups were made.").

3. EVERY NUMBER ON THE REPORT
One entry per number, in the order the numbers appear on the report:

Number: 21
What it means: Licences whose end date falls in this period.
How we got it: We count the licences ending in this period. Count = 21.
Where to find it: Section "Licences ending this period". Data field: ScopedLicences.

4. HOW TO READ EACH CHART
For each chart, 2 or 3 easy sentences: what one bar / dot / tile is, and what the colours mean.

5. THINGS TO CHECK
- Known limits of the data (from data_quality, in easy words).
- Any number whose sum you could not confirm, or could not trace.
Write "Nothing unusual found." ONLY when there is nothing else in this part.
~~~

Before returning, check: every number in `numbers_on_report` has an entry in part 3; every sum is right;
every "Where to find it" copies a title that really exists in `report_text`; no ids; no Markdown symbols
(`#`, `|`, `**`, backticks); no SQL.

Return the plain text only.
