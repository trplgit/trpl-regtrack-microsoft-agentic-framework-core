# Free Weekly Digest — Email Template

Two artifacts:
1. **`digest.html`** — the rendered email shell (below)
2. **`digest_fallback.txt`** — the deterministic body used when the LLM is skipped

> **The email never fails to go out.** If the LLM would exceed
> `Budget:FreeDigestTokenCap`, or errors, or returns something the validator
> rejects, the fallback body is substituted and the send proceeds. (§10.5)

---

## Token substitution

| Token | Source |
|---|---|
| `{{RecipientName}}` | `[User].Name` |
| `{{TenantName}}` | `Customer.Name` |
| `{{WeekEnding}}` | anchor date, `d MMM yyyy` |
| `{{Body}}` | LLM prose **or** the fallback below |
| `{{DueNext7}}` … `{{CompletedLast7}}` | the ~15 aggregates from `usp_Insights_FreeDigestAggregates` |
| `{{UnsubscribeUrl}}` | writes the durable per-recipient opt-out |

---

## `digest_fallback.txt` — deterministic body

Used verbatim when the LLM is skipped. Deliberately plain: it must be correct and
useful without being written.

```
Good morning{{#RecipientName}} {{RecipientName}}{{/RecipientName}},

Your compliance calendar for the week ending {{WeekEnding}}:

Due in the next 7 days:      {{DueNext7}}
  of which critical:         {{CriticalDueNext7}}
  carrying personal liability: {{ImprisonmentDueNext7}}

On the horizon (next 30 days):
  Total due:                 {{DueNext30}}
  Carrying personal liability: {{ImprisonmentDueNext30}}
  Licences due to lapse:     {{LicencesLapsingNext30}}

Completed last week:         {{CompletedLast7}}

This digest shows what is coming. RegInsights Pro shows which locations,
which people, and which laws are driving it.
```

> **[TRAP] No ratio appears anywhere, and the word "overdue" is never used.** The
> fallback is generated from the same 15 numbers the LLM receives — none of which
> permit a completion rate. A recent-window ratio would read as catastrophic
> (one tenant showed ~84% "missed") when it is really recency lag. (§10.6)

---

## `digest.html` — shell

Plain, table-based, client-safe. No web fonts, no external CSS, no tracking pixels
(a compliance product should not be tracking opens without consent).

```html
<!DOCTYPE html>
<html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>RegTrack Insights — week ending {{WeekEnding}}</title></head>
<body style="margin:0;padding:0;background:#f4f5f7;">
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f4f5f7;padding:24px 0;">
 <tr><td align="center">
  <table role="presentation" width="600" cellpadding="0" cellspacing="0"
         style="background:#ffffff;border:1px solid #e1e4e8;border-radius:4px;
                font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;
                color:#24292f;font-size:15px;line-height:1.55;">

   <tr><td style="padding:20px 28px;border-bottom:1px solid #e1e4e8;">
     <div style="font-size:13px;color:#57606a;letter-spacing:.04em;text-transform:uppercase;">
       RegTrack Insights</div>
     <div style="font-size:18px;font-weight:600;margin-top:2px;">{{TenantName}}</div>
     <div style="font-size:13px;color:#57606a;">Week ending {{WeekEnding}}</div>
   </td></tr>

   <tr><td style="padding:24px 28px;white-space:pre-line;">{{Body}}</td></tr>

   <tr><td style="padding:0 28px 24px;">
     <a href="{{UpgradeUrl}}"
        style="display:inline-block;padding:10px 18px;background:#0969da;color:#ffffff;
               text-decoration:none;border-radius:4px;font-size:14px;font-weight:500;">
       See what's driving it</a>
   </td></tr>

   <tr><td style="padding:16px 28px;border-top:1px solid #e1e4e8;
                  font-size:12px;color:#57606a;">
     Figures reflect your authorised scope as at {{WeekEnding}}.<br>
     <a href="{{UnsubscribeUrl}}" style="color:#57606a;">Unsubscribe from this digest</a>
   </td></tr>

  </table>
 </td></tr>
</table>
</body></html>
```

---

## Validation before send

Reject the LLM body and fall back if **any** check fails:

1. Contains a `%` adjacent to a completion/closure word (a ratio was invented)
2. Contains the word **"overdue"** (reserved for the paid tier)
3. Contains a location, user, department or Act name (it has no such data — a
   hallucination)
4. Contains a number not present in the 15 aggregates
5. Exceeds ~400 words

> Check 4 is the strongest: the input set is small and closed, so any unmatched
> figure is fabricated by definition. Implement it as a numeric-token diff against
> the aggregate set.

## Deliverability  (§10.8)

SPF, DKIM and DMARC configured before the first send. Warm the sending domain —
600 tenants × N recipients weekly is real volume and a cold domain will land in
spam. Honour bounces: hard bounce ⇒ suppress the recipient and flag for ops.
