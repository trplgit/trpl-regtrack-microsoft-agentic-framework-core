# Insights.Api

**Source for the RegTrack API — not part of this worker.**

`CLAUDE.md` §6: the Insights worker is private and queue-driven, with no public endpoints and no
ingress. Insights endpoints belong in the RegTrack API, where auth already lives. These files sit
here so that repo has something to take, and are excluded from this project's build.

> Because they are not compiled here, they are **not compile-checked**. If `IFreeDigestRepository`,
> `UnsubscribeToken` or the Contracts DTOs change, nothing breaks until someone copies this across.

---

## Two endpoints

Mapped with one line:

```csharp
app.MapDigestEndpoints();
```

No authentication is applied here — RegTrack already has it, and both of these are reached
without a login for reasons specific to each.

### `GET /api/insights/digest/unsubscribe?c={customerId}&u={userId}&t={token}`

The link at the foot of every digest. Adds the recipient to the permanent suppression list
(§5.4 — durable, and survives tier changes).

Reachable without a login because it is clicked from an email client by someone who may not be
signed in; email clients also cannot POST from a link. The `t` token is what makes that safe — an
HMAC over `(customer, user)` issued when the mail was sent. Without it the URL is enumerable, and
since suppression is durable an enumeration sweep would have to be undone row by row.

| Response | When |
|---|---|
| `200` | Unsubscribed |
| `400` | Bad ids, or the token does not verify |
| `500` | `Email:UnsubscribeSigningKey` is not configured |

### `POST /api/insights/digest/bounce`

Posted by the mail provider when a digest permanently fails to deliver (§10.8: *"hard bounce ⇒
suppress the recipient and flag for ops"*).

Reachable without a login because the provider has no RegTrack account — protect it the way the
host protects its other inbound webhooks.

**Only hard bounces suppress.** A soft bounce is a full mailbox or a temporary outage; suppressing
on one would silently unsubscribe someone whose mail arrives fine next week.

```json
{ "customer_id": 23, "user_id": 357, "bounce_type": "hard", "reason": "550 mailbox unavailable" }
```

> **[VERIFY]** The payload shape is provisional. Check it against the provider's webhook
> documentation before wiring up. If they do not echo custom fields, `customer_id` and `user_id`
> have to be attached to the outbound message as custom headers and read back here.

---

## There is no send-trigger endpoint

By design. §10.3 makes the free digest a **scheduled** job — weekly, staggered by
`hash(tenant_id) % 7`, lowest-priority lane. Nothing calls an API to send it.

Paid-tier work arrives the other way: RegTrack enqueues, the worker dequeues (§6). Neither path
needs an endpoint on this side.

---

## Taking this into the RegTrack repo

Copy `DigestEndpoints.cs`, map it, and reference `Insights.Contracts` and `Insights.Data` —
`SOLUTION_STRUCTURE.md` names those as the two the API consumes, never `Agents` or `Worker`.

The DTO lives in `Insights.Contracts` so it is shared rather than copied; a duplicated wire
contract drifts the moment either side changes.

```jsonc
"ConnectionStrings": { "RegTrack": "" },
"Email": {
  // MUST be byte-identical to the worker's value, or every unsubscribe link fails to verify.
  "UnsubscribeSigningKey": ""
}
```

And in the worker, `Email:UnsubscribeBaseUrl` must point at this host's unsubscribe route — that
is the URL written into every email.
