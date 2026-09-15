# Note for the dev team — SQL file encoding

**What happened.** Our `.sql` files were saved as UTF-8, but the tool used to run
them against the database read them as ANSI/Windows-1252. Every multi-byte UTF-8
character was decoded as several separate Latin-1 characters, so `═` became
`â•<0x90>` and `—` became `â€"`. Roughly 2,000 characters were corrupted across
three procedures, and the mangled text was stored in the database — visible in
procedure comments and, worse, inside message strings that surface in output. It
is not limited to our work: the same corruption exists in a pre-existing RegTrack
procedure (`USP_GetEscalationCounts_Mobile_Statutory`), so the deployment path has
had this problem for a while.

**It also got baked into a source file.** `06_freetier_aggregates.sql` was opened
and re-saved by a non-UTF-8-aware editor, which wrote the already-corrupted text
back to disk with a BOM. The file in the repo was corrupt at rest, so every
install from it reproduced the damage. Please replace it with the version we have
regenerated.

**Three things to change.**

1. **Run SQL scripts with an explicit UTF-8 codepage.** For classic `sqlcmd`, add
   `-f 65001`. Better still, switch to `go-sqlcmd` (sqlcmd v1.x), which is UTF-8
   native. If you use DbUp/Flyway/EF or a PowerShell wrapper, make sure the file
   is read with an explicit `Encoding.UTF8` (PowerShell 5.1's default is not
   UTF-8).

2. **Keep SQL source pure ASCII.** All 17 scripts have been converted: `=` for
   box-drawing, `-` for dashes, `->` for arrows, `Sec.` for `§`. This removes the
   entire problem class regardless of what tooling anyone deploys with. Worth
   noting that the scripts already written in pure ASCII were completely immune.

3. **Add this check to the deployment pipeline** and fail the build on any result:

   ```sql
   SELECT o.name
   FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
   WHERE o.name LIKE '%Insights%'
     AND m.definition COLLATE Latin1_General_BIN2
         LIKE N'%[^ -~' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N']%';
   ```

   Zero rows = clean. `COLLATE Latin1_General_BIN2` matters — without it SQL
   Server's default collation is accent-insensitive and the check gives false
   results.

**Current state.** UAT has been repaired: all 36 Insights objects and the
dictionary reference data are now pure ASCII, and the full test suite passes. But
the repair was applied directly to the database, so **the next deployment will
undo it unless the install path is fixed first.**
