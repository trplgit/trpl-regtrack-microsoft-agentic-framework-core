# Reasoning file for testers (v2, 2026-09-27)

**[v2]** Replaces `08_reasoning_explainer.md` (kept untouched). Real feedback on v1: full of internal
words ("assertion id", "finding id") that a tester cannot use, too long, and it never saw the
finished report, so numbers shown on the page could be left unexplained. v2 is written for the
testing team, in simple Indian English, and covers EVERY number the reader sees.

## Who reads this and what they need

A tester has the finished report open next to this file. Their job: find mistakes in the data, and
catch any number that was made up. For every number on the report they need three things:
1. **what it means**, in one plain line;
2. **how it is calculated**, with the real numbers put into the formula;
3. **where to check it** - a place in the report (which table, which row, which column) and the
   source field name, so they can verify it themselves.

They also want to see **why the report looks the way it does** - the model's chain of thought for
choosing the lead section and each chart.

## What you are given (one JSON object)

- `report_text` - the visible text of the FINISHED report, exactly what a reader sees (headings,
  cards, "What this means" points, chart titles, "How to read" panels). This is your checklist of
  claims.
- `numbers_on_report` - every number that appears in `report_text`, pulled out by code. **Every one
  of these must be explained in section 3** (a year or a date inside a sentence needs no row).
- `dimension_rows` - every real row of source data (one per person / law / location / etc.), with
  its real field names. This is where every per-row number comes from.
- `dimension_control_totals` - the real overall totals for this report.
- `assertions`, `findings` - computed comparisons (rank, gap to average, etc.) the report used.
  Use them to find formulas and values - but NEVER show their ids (`assertion_id`, `finding_id`,
  `A-WORST`, `F-...`) to the reader. A tester cannot do anything with an id.
- `composition_plan` - the model's own decisions: `hero.reason` (why the lead section leads),
  `blocks[].emphasis` (what each section shows, which chart, and why), `omitted` (what was left
  out, and why). This is the chain of thought for structure.
- `reasoning_log` - the models' own short summaries of their thinking, per step (may be empty).
- `tool_invocations` - any live read-only SQL the model ran (usually none).
- `data_quality` - known limits of this data, with real detail text.
- `database_checks` (may be null) - ready, VERIFIED SQL for testers: `setup_sql` rebuilds exactly
  the report's population (already filled with this tenant, user and period), and `checks` has one
  short query per number (`id`, `name`, `tables` in plain words, `query_sql`). A check with
  `per_row: true` contains `{RowKey}` - replace it with that row's own `row_key_field` value from
  `dimension_rows` (e.g. the person's `UserID`). These queries were tested to return exactly the
  report's numbers - copy them VERBATIM, never write or change SQL yourself, never invent a table.

Never write a number, a name or a reason that is not in this JSON. If you cannot trace a number,
say so plainly in section 5 - never guess.

## Formula rules

- Always write the formula in words first, then with the real numbers:
  `Overdue rate = overdue obligations / all obligations x 100 = 28 / 33 x 100 = 84.8%`.
- A rate on one row uses that row's own two fields (e.g. `Overdue` and `Instances`).
- "Points above the average" = this value - the average value (`100.0% - 84.8% = 15.2 points`).
- "X of Y" shares: `X / Y x 100`. Check the arithmetic before writing it; if it does not match
  the number on the report, say so in section 5 - that is exactly what the tester needs to know.
- A plain count (e.g. "5 laws have a possible prison term") says how it is counted:
  "number of laws where `ImprisonmentInstances` is more than 0 = 5".
- A median: "middle value of `X` across all people who have a reading".
- A rank: "position when sorted by `X`, highest first; 1 of 14".
- Show counts as whole numbers and percentages with one decimal, the way the report shows them.

## Where-to-check rules

Point to something the tester can actually open - and ONLY to things that really exist:
**copy the section title, card label or chart title exactly as it appears in `report_text`**.
Never invent a table, card or column name. If the number is shown only inside a sentence, say
which section: `Section "Who carries work with possible prison terms?" -> point 1`.
- a row in a report table or chart that exists: `Chart "Who carries the largest performer
  workloads?" -> bar "Sunil Kamble"`;
- an overall total: `Card "Overdue obligations"` (label copied from the report);
- plus, when `database_checks` has a matching check, its id: `DB check U5` (the query itself goes
  in section 6). Only when there is no matching check, name the source field in backticks instead.

## Structure (keep sections 1-5 short - aim for 2 to 3 pages before the database section)

~~~
# How this report was built - {dimension}

Period: {from the window data-quality note or the report text, or "all time" if there is none}.
Source data: {number of entries in dimension_rows, with what one entry is - e.g. "14 laws",
"305 people"} and {the main total, e.g. "33 obligations"} - never mix the two up.

## 1. In short
3-4 lines: what the report is about, its main message, and the single most important thing to verify.

## 2. How the report was put together (chain of thought)
Numbered steps, one or two lines each:
1. What data was pulled (rows, totals, period).
2. What stood out, and why the lead section leads - from hero.reason, in plain words.
3. One line per section: what it shows, which chart, and why that chart was chosen - from emphasis.
4. What was left out and why - from omitted (or "nothing was left out").
5. Any live SQL the model ran (or "no extra queries were run").
Use reasoning_log where it adds a real reason. Plain words; no ids.

## 3. Every number on the report
One table, grouped by report section, one row per distinct number shown:
| Number on report | What it means | How it is calculated | Where to check |
Every number in numbers_on_report must appear in this table.

## 4. How to read each chart
Per chart: 2-3 lines - what one mark is, what the axes / colours / lines mean.

## 5. Things to double-check
Bullets: known data limits (from data_quality, in plain words), any number whose arithmetic you
could not confirm, any number you could not trace to a row or total, anything that looks odd.
Write "Nothing unusual found." ONLY when this section has no other bullet.

## 6. How to check in the database
(Only when database_checks is not null; otherwise write one line: "No database checks are
available for this report yet.")
One line: "Run the setup once, then any check below in the same window. Overdue and login figures
are live, so a check run days later can differ slightly."
Then the setup_sql in one ```sql block, copied exactly.
Then, for each check that section 3 refers to (and only those), in the order they appear:
### DB check {id} - {name}
{tables}
```sql
{query_sql copied exactly; for a per-row check, {RowKey} replaced by the row's key, with a
 -- comment line naming the row, e.g. -- Sunil Kamble}
```
If the report shows several rows for the same per-row check, show the query once per named row
the report mentions (at most 5), each with its own comment line.
~~~

Before returning, check yourself: every number in `numbers_on_report` is in section 3; every
formula's arithmetic is right; every "Where to check" label is copied from `report_text`; no id
anywhere; section 3 has no row for a number that is not on the report (ids, codes, coordinates);
every SQL in section 6 is copied exactly from database_checks.

## Language

Simple Indian English, short sentences, everyday words. Say "obligations" (not "instances"),
"people / laws / locations" (not "members" or "rows"), "in this report" (not "scoped").
Field names in backticks are fine in the "Where to check" and "How it is calculated" columns -
testers use them, and so are "DB check" ids. No internal ids (assertion, finding, run), no filler words (robust, holistic, leverage, notably, overall,
underscores), no praise of the report or the models, no closing summary.

Return the Markdown document only.
