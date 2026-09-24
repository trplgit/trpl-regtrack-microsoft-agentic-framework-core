# FRONTEND_INSIGHTS_INTEGRATION.md

## 1. What this is

Each user sees one weekly compliance insight card, written by the RegInsights worker every Sunday for the previous week. The card is stored and retrieved by (customer_id, user_id, period_start_date). The frontend only reads it; there is never more than one card per user per week.

The card answers a single question about that week's compliance estate — for example, *which licences lack a renewal in progress?* — with a primary metric, supporting metrics, and two sentences of narrative. The backend rotates the question monthly, so the subject and tone change from week to week.

## 2. The endpoint you call

**Read a specific week:**

```
POST {BASE_URL}/v2/api/ai-report/weekly/get
Headers: Authorization: Bearer {TOKEN}
         Content-Type: application/json
Body: { "customer_id": 23, "user_id": 357, "period_start_date": "2026-08-24" }
```

**Read the most recent card (any week):**

```
Body: { "customer_id": 23, "user_id": 357 }
```

Omit `period_start_date` to fetch the user's latest card without specifying a week.

**period_start_date requirements:**

- Must be a Monday, formatted as "YYYY-MM-DD".
- A non-Monday date returns 400 Bad Request.

**Worker-only endpoint (mentioned for completeness):**

The backend also has `POST /v2/api/ai-report/weekly/upsert` — the worker calls it to store cards. The frontend never does.

## 3. The response

### 200 OK — card found

```json
{
    "success": true,
    "error": "",
    "result": {
        "ai_weekly_report_id": 47,
        "customer_id": 23,
        "user_id": 357,
        "period_start_date": "2026-08-24T00:00:00",
        "report": {
            "insight_id": "ins_23_357_20260824",
            "tier": "free",
            "type": "diagnostic",
            "severity": "high",
            "week_of": "2026-08-24",
            "title": "Where it sits — lapsed licences",
            "headline": "31 licences have no renewal in progress as at 30 Aug 2026.",
            "narrative": "The standing position exposes your organisation to licence-continuity gaps across most of its 37 licences, leaving obligations without an active route to renewal. All 4 locations holding licences include at least one affected licence, so the position is distributed across every location rather than concentrated in one place.",
            "primary_metric": {
                "label": "Licences with no renewal in progress",
                "current": 31,
                "target": 0,
                "unit": "licences",
                "direction": "lower_is_better"
            },
            "supporting_metrics": [
                { "label": "licences", "value": 37, "unit": "licences" },
                { "label": "locations", "value": 4, "unit": "locations" }
            ]
        },
        "model_version": "reginsights-freedigest-2",
        "source_reference": "freedigest-insight-23-2026-08-30",
        "revision_count": 1,
        "created_on_utc": "2026-09-24T07:01:37.638",
        "updated_on_utc": "2026-09-24T07:01:37.638"
    }
}
```

⚠️ **Naming note:** The fields wrapping the `report` object (`ai_weekly_report_id`, `model_version`, `revision_count`) use snake_case. This differs from an older integration document. Trust this sample.

### 404 Not Found — no card for this week

```json
{
    "success": false,
    "error": {
        "code": "REPORT_NOT_FOUND",
        ...
    },
    "result": {}
}
```

This is normal. A 404 means the user has no card for that week; render the empty state, do not treat it as an error. When stepping back through previous weeks (see §6), simply skip any Monday that returns 404.

### 400 Bad Request — invalid input

```json
{
    "errors": [
        { "field": "period_start_date", "message": "Date must be a Monday" }
    ]
}
```

Not wrapped in success/error/result. Check the `errors` array for validation failures (invalid date format, non-Monday, out-of-range customer or user).

### 401 Unauthorized

Missing or invalid bearer token.

## 4. The report object, field by field

| Field | Type | Meaning / values |
|---|---|---|
| `insight_id` | string | Unique per user per week. Format: `ins_{customer_id}_{user_id}_{yyyyMMdd}`. Safe to use as a list key. |
| `tier` | string | Always `"free"` in this release. |
| `type` | string | Exactly one of `"descriptive"`, `"diagnostic"`, `"predictive"`. Capitalise for display (e.g. badge label "Diagnostic"). |
| `severity` | string | Exactly one of `"high"`, `"medium"`, `"low"`. Controls badge colour. |
| `week_of` | string | ISO 8601 date `"YYYY-MM-DD"`, always a Monday. Equals the date part of `period_start_date`. |
| `title` | string | A short line such as "Where it sits — lapsed licences". Never contains a figure. Render above the headline. |
| `headline` | string | One sentence containing the lead figure. Budget roughly 150 characters. |
| `narrative` | string | Exactly two sentences, 40 to 70 words total. Provides context and distribution. |
| `primary_metric` | object | See below. |
| `supporting_metrics` | array | 0 to 2 metric objects. See below. |

**primary_metric:**

| Field | Type | Meaning |
|---|---|---|
| `label` | string | Full description, e.g. "Licences with no renewal in progress". |
| `current` | integer | The actual count or value this week. |
| `target` | integer | The ideal value **implied by direction**. Zero for a count that should not exist; 100 for a percentage that should be complete. **NOT a forecast, agreed target, or calculated value.** Display only as a goal marker (e.g. progress bar reference) or omit it entirely. Do NOT render "31 → 0" as a trend or projection. |
| `unit` | string | The measurement, e.g. "licences". One of: obligations, licences, locations, people, Acts, categories, licence types. |
| `direction` | string | Exactly one of `"lower_is_better"`, `"higher_is_better"`, `"neutral"`. |

**supporting_metrics[]:**

Each object has:

| Field | Type | Meaning |
|---|---|---|
| `label` | string | Short lowercase phrase, at most 34 characters, e.g. "obligations unassigned". |
| `value` | integer | The count. |
| `unit` | string | One of: obligations, licences, locations, people, Acts, categories, licence types. |

**Render supporting_metrics:** Join the array as `{value} {label}` separated by " · " to produce a compact line. Example: "37 licences · 4 locations". The array may be empty on older stored cards, so guard against zero entries.

## 5. Rendering the two card styles

Both styles use the **same `report` object**. Nothing extra is sent for the compact form.

**Hero card ("Insight of the week"):**
- Badge: `type` + `severity` colour.
- Week label: format `week_of` as "w/c 24 August 2026" or similar.
- Title line (from `title`).
- Headline (from `headline`).
- Narrative (from `narrative`).
- Primary metric: show `current` and `unit` (e.g. "31 licences").
- Supporting metrics: render the compact line (e.g. "37 licences · 4 locations").

**Compact card ("Previous weeks"):**
- Badge: `type` only.
- Week label: format `week_of`.
- Headline (from `headline`).
- Supporting metrics: render the compact line.

## 6. Getting previous weeks

The response contains only the current week's card. To populate a "Previous weeks" strip, call `/weekly/get` once per earlier Monday, stepping back seven days at a time:

```
Week 1 (current):    period_start_date: "2026-08-24"
Week 2 (past):       period_start_date: "2026-08-17"
Week 3 (past):       period_start_date: "2026-08-10"
...
```

If a Monday returns 404, no card exists for that week; skip it and move to the next Monday. Do not treat 404 as an error.

## 7. Things that will surprise you

**Everyone in the same scope group receives identical text.** All users in the same customer and (for monthly cycles) the same assignment band receive the same title, headline, and narrative. Only `insight_id` and `user_id` differ per user. This is intentional; do not treat two users with identical headlines as a bug.

**The subject rotates monthly.** The first Sunday of each month covers the overall picture; the second, people and ownership; the third, locations; the fourth, Acts; the fifth, licences. As a result, `type` and the subject matter change from week to week by design, even for the same user.

**Re-posting updates in place.** If the worker re-posts the same week (same `customer_id`, `user_id`, `period_start_date`), it updates the existing row and increments `revision_count` rather than creating a second row.

**Check model_version.** If `model_version` is anything other than `"reginsights-freedigest-2"`, you are reading a card stored before the current schema version. The fields described above may not all be present. (This should be rare; contact the backend team if you encounter it.)

---

**Questions:** Contact the RegInsights worker team.
