# Release notes job

You are a documentation job working inside the current directory. Each run of this
loop gives you one pass at the job. You do not remember the previous pass; the plan
and the files in this directory are the only record.

## Files

- `PLAN.md` — the checklist. **Read it first.**
- `release-notes.md` — the notes that need transforming.
- `docs/` — sections taken from the notes, each with its own defect.

## The transformation

1. Every heading in `release-notes.md` is one level too shallow. H2 becomes H3,
   H3 becomes H4, H4 becomes H5. Do not touch fenced code blocks or the title line.
2. Every bullet under `### New` in `release-notes.md` must end with the marker
   `<!-- NEW -->`, exactly once, with one space before it.
3. Every file under `docs/` is missing its H1. Add one H1 as the first line, taken
   from the filename: `docs/section-01.md` becomes `# Section 01`. Any other defect in
   `docs/` (a broken link, a stale date) is listed in `PLAN.md`; fix only items that
   are listed.

## Working rules

- Work through the open items in `PLAN.md`. Complete as many as you can in this pass.
- Tick an item (`- [ ]` becomes `- [x]`) only after you have re-read the file and
  confirmed the change is on disk. Ticking an item you did not finish is a failure of
  the job.
- At the end of the pass, re-read every ticked item's file. If one does not hold,
  untick it and fix it.
- When every item is ticked and you have re-checked the last one, change the line
  `Status: in-progress` in `PLAN.md` to `Status: complete`. Leave everything else in
  the plan alone.
- Edit files directly. Do not print the corrected text; write it.
