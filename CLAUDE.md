# Cicerone — Jellyfin plugin

Checks that a subtitle track is the language it claims to be and is timed to the
dialogue it sits over. It cuts real audio out of the media file at several planned
moments, has a speech-to-text model say what is in each clip, and measures the
difference between when a word was **heard** and when the subtitle claims it was
**said**.

**The audio is the ground truth; the subtitle file is the claim.** Never the other
way round. Everything the plugin outputs is a difference between two clocks, and the
audio clock is defined by where ffmpeg actually started reading.

**Scope discipline:** Cicerone grades and repairs the subtitles you already have. It
does not download them — Bazarr and the Open Subtitles plugin fetch, this verifies —
and it is not a rules engine. Reject feature requests that amount to "also go and
get subtitles".

---

## Status: COMPLETE BUT NEVER COMPILED

Every file the plugin needs now exists, including the embedded config page. What has
**not** happened is a build: there is no .NET SDK, no ffmpeg and no Jellyfin on the
machine this was written on. Nothing here has been run.

The first job anywhere else is `dotnet build`, and the likeliest failures are the
Jellyfin API calls listed under *What is left* — written from the patterns in the
sibling plugins and never checked against the real assembly.

---

## Development commands

The .NET 9 SDK is installed per-user and is **not on `PATH` by default**:

```bash
export PATH="$HOME/.dotnet:$PATH"     # required first, in every shell

dotnet build Jellyfin.Plugin.Cicerone.sln -c Release
dotnet test  Jellyfin.Plugin.Cicerone.sln -c Release   # no network, no ffmpeg needed
./build/package.sh                                     # artifacts/Cicerone_<version>/
VERSION=0.1.0.0 CHANGELOG="..." ./build/release.sh      # zip + manifest.json entry
```

Target framework is **net9.0** — Jellyfin 10.11.x runs on .NET 9, *not* .NET 8.
Build treats warnings as errors.

## Releasing

`build/release.sh` builds the zip (plugin files at zip **root**), computes the MD5
Jellyfin verifies on install, and inserts the version into `manifest.json`. Then
create a GitHub release tagged `v<VERSION>` and upload that exact zip — rebuilding or
re-zipping changes the checksum and breaks catalogue installs. Users add
`https://raw.githubusercontent.com/nitramivel/jellyfin-cicerone/main/manifest.json`
as a plugin repository.

Plugin GUID: `83004bd9-e2d6-4e13-9f3a-73bb12ef1f96`.

---

## Architecture

```text
Core/          pure, no I/O, no Jellyfin types — all of it unit tested
  Subtitles/   SRT parse and write, cue cleaning, the plain track record
  Sync/        the whole measurement: tokenizing, anchor planning, the offset
               vote, the drift fit, frame rate ratios, the verdict, retiming
  Language/    script and function-word identification, ISO code normalisation
  Audio/       ffmpeg argument construction, audio track choice
  Reports/     the stored per-item report shape and the library-wide tally
  Runs/        run log document shape, and RunEstimate (throughput → time left)
Services/      everything that touches a process, a file, an API or Jellyfin
  FfmpegRunner            process handling, below-normal priority
  AudioSampler            drives ffmpeg, cuts the clips
  SubtitleReader          ISubtitleEncoder → cues
  Transcription/          ITranscriptionProvider, OpenAI-shaped, Google, retry
  CheckService            per-item orchestration — the economy lives here
  RepairWriter            corrected sidecar, never an in-place edit
  ReportStore             one JSON per item, cached in memory
  VerifyRunService        the library walk, budget, one-run-at-a-time
  VerifySubtitlesTask     IScheduledTask, NO default trigger
  Runs/RunLogStore        live run in memory, finished runs on disk, rotated at 20
Api/           CiceroneController — every endpoint admin-only
Configuration/ PluginConfiguration, TranscriptionProfile, configPage.html
```

**The Core/Services split is load-bearing.** There is no Jellyfin server, no ffmpeg
and no transcription endpoint on the dev machine, so everything that can be decided
without them is decided in `Core` and tested there — including the ffmpeg command
lines, which are built as pure strings and asserted on. What is left unverified is
genuinely only process execution and HTTP.

### The test strategy

Synthetic dialogue with a known answer. `Dialogue` in `SyncTests.cs` builds a track
of deliberately rare words, then builds the transcript of a copy shifted and scaled
by a chosen amount, and the test asserts that the measured difference is the one
that was injected. That poses the only thing that actually has to be right — the
arithmetic between a cue's timing and a transcript's — exactly, with no audio
anywhere near it.

---

## Decisions worth not relitigating

**One measurement is worse than none.** A single-point sync check reports a
frame-rate-mismatched file as perfect, because it *is* perfect wherever you happened
to look — and now you believe it. Two anchors is the minimum that can see a slope at
all; five is what makes the slope survive an anchor landing in a silence. This is the
premise of the plugin, not a tuning parameter. `OneAnchorCallsADriftingFilePerfect`
in `SyncTests.cs` is the demonstration; do not delete it.

**The preroll seek is not a micro-optimisation.** `-ss` before `-i` seeks the
container in constant time; `-ss` after `-i` trims by decoding, which is exact.
`AudioPlan` does the coarse seek two seconds early and then discards those two
seconds, so the clip begins at the requested sample and getting there took
milliseconds. A seek that quietly lands on the nearest keyframe does not produce a
*worse* measurement — it produces a **confident** measurement that is wrong by the
same amount at every anchor, which fits a perfectly straight line and reads as a real
constant offset. There is no way to catch that downstream.

**Rebasing happens exactly once**, in `CheckService.ListenAsync`. Providers report
times from the start of the clip they were handed; cues are on the item's clock.
Skip it and every anchor measures its own position in the film as the offset — large,
consistent, and complete fiction.

**Confidence is measured on the raw histogram, not the smoothed one.** The peak is
*located* on a smoothed histogram, because a spike straddling a bin boundary arrives
as two half-height neighbours and would otherwise lose to a solid bin of noise. It is
then *measured* on the raw bins at peak ±1. Smoothing triples the mass it
distributes, so a share read off it caps a flawless match at one half — and every
threshold downstream (`MinConfidenceToRepair` defaults to 0.4) would silently be
calibrated against a scale whose maximum is not one. This was wrong once already.

**`OffsetSearch.MaxOccurrences` is the stopword list.** A word occurring more than
six times on either side of a window carries no positional information and would
outvote the rare words that actually locate it. Dropping by observed frequency works
in every language, which is the point — an English stopword list does nothing for a
Polish track. Do not replace this with a table.

**`AnchorPlanner` clamps the anchor count to `floor(region / window)`.** Without it a
short episode gets overlapping windows, which double-counts the same seconds of audio
as two independent measurements and lets one exchange vote twice in the fit. A short
item gets fewer anchors and the fit reports how many it actually had.

**Snapping to a frame rate ratio re-solves the intercept** at the anchors' centre of
mass. Keeping the fitted intercept would pivot the line about time zero — the one
place no anchor was ever measured — and push the middle of the film out by more than
the snap corrected. `SnappingDoesNotPivotTheFitAboutTimeZero` guards this.

**`verbose_json` is why the default model is `whisper-1`.** Cicerone needs a *timed*
transcript, not a good one. OpenAI's newer and better transcription models
(`gpt-4o-transcribe` and its mini) do not support that response format, so they
return an excellent transcript with no times in it and are useless here. An untimed
transcript is kept but weighted at half, because placing words by assuming an even
speaking rate is good to a few seconds and no better — enough to rule out a gross
offset, never enough to decide the third of a second that separates "in sync" from
"not".

**The language check is free and runs first.** Script plus function words, no model
call, before any audio is paid for. It is also the only check that can *explain* the
fault it finds: a track that is the wrong language entirely would otherwise come back
as "does not match the dialogue", which is true and sends the owner looking for a
sync problem. `LanguageProfile.Contradicts` is deliberately reluctant — the tag has
to be beaten by a clear margin, and an untagged track is never contradicted because
it never made a claim.

**Audio track choice is a language match, not a fallback to default.** A release
carrying a German dub as its default track, checked against an English subtitle file,
shares almost no words with it — so every anchor returns votes and no agreement, and
a perfectly good file is reported as a different cut.

**The transcript is per item, not per track.** `CheckService` groups subtitle tracks
by the audio stream each wants, transcribes once per group, and scores every member
against the same transcript. Three English tracks cost what one costs.

**Cicerone never edits a subtitle file.** A correction is a measurement, measurements
have error, and the failure mode of editing in place is destroying a file somebody
spent an evening timing by hand. Repairs are always a new sidecar
(`Film.en.cicerone.srt`) beside the original, written to a temporary name and moved
into place so an interrupted run cannot leave half a file a player will happily load.
Everything Cicerone writes is identifiable by the marker in the name; nothing here
will ever overwrite a file it did not write. `ReportStore.Clear` deliberately does
**not** remove repaired sidecars — those are files in library folders, and deleting
them is the owner's decision with a file manager, not a side effect of clearing a
cache.

**The scheduled task ships with no default trigger.** Every other task in this family
runs weekly out of the box; this one does not, because it is the only one that spends
money the first time it fires. A plugin that quietly transcribed a thousand films the
night it was installed would be indefensible however useful the answers were.

**Cost is bounded by anchors, not runtime.** Five thirty-second windows is two and a
half minutes of audio whether the item is a twenty-minute episode or a three-hour
film. That is what makes `GET Cicerone/Estimate` exact rather than indicative — the
figure is arithmetic over settings, not a projection — and it is the reason for
anchoring instead of transcribing whole files, which is forty times the bill for an
answer nobody needed.

**Memory answers the progress panel; the file answers history.** `RunLogStore` holds
the live run in memory and serves `Current()` from it, so a two-second poll costs a
lock rather than re-reading a file that grows with every item. Writes are debounced to
five seconds precisely because nothing reads the file during a run. `CurrentItem` is
never persisted. A run whose file still says `running` with no process behind it is
reported as **abandoned**, worked out when the file is read — the one thing a dead
process cannot do is write that it died.

---

## What is left

### 1. Build it

Nothing has been compiled. Run the tests first — they need no network, no ffmpeg and
no server, and they cover every decision the plugin actually makes.

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test Jellyfin.Plugin.Cicerone.sln -c Release
```

### 2. Verify against a live server

The plugin has never been installed. In order: package it, drop it in, open the
settings page, press **Test the default profile** on the Models tab, then
**Try one item** on the Run tab with a single film. That sequence isolates the three
things that can be independently wrong — the plugin loading, the transcriber
answering, and the measurement being right.

A film whose subtitles are known-good and one known to be a PAL-drift file are the
two worth trying first; the second is the one the whole design exists for.

### 3. What is already done

For the avoidance of re-doing it:

- All of `Core/` and `Services/`, the API controller, the scheduled task.
- `Configuration/configPage.html` — 1,465 lines, six tabs, live run polling, the
  coverage table, profile CRUD. Every element id it queries exists.
- `README.md` — 282 lines.
- Six test files: `SubtitleTests`, `SyncTests`, `AnchorAndVerdictTests`,
  `LanguageTests`, `AudioPlanTests`, `StoreAndProviderTests`.
- `build/package.sh`, `build/release.sh`, `manifest.json`, `LICENSE`, `.gitignore`,
  the solution and both project files.

### 4. Unverified Jellyfin API calls

Written from the patterns in Concierge, Curator and Colorist but never compiled
against `Jellyfin.Controller` 10.11.11. Check these first when the build errors:

- `IMediaSourceManager.GetMediaStreams(Guid)` — `CheckService`
- `ISubtitleEncoder.GetSubtitles(item, id, index, "srt", 0, 0, false, ct)` — the
  8-argument form, copied from Concierge's `SubtitleIndexer.cs:462`
- `ILibraryManager.QueueLibraryScan()` — `RepairWriter.Refresh`; it ignores the item
  argument, which is a smell worth revisiting
- `InternalItemsQuery.HasSubtitles` — `VerifyRunService.Eligible`
- `MediaStream.IsHearingImpaired` / `.IsForced` / `.IsTextSubtitleStream`
- `IMediaEncoder.EncoderPath` — `AudioSampler`
- `Policies.RequiresElevation` — `CiceroneController`

### 5. Git and GitHub

Not initialised. Nothing pushed. `gh` is authenticated as `nitramivel` with `repo`
scope.

```bash
git init && git add -A && git commit
gh repo create jellyfin-cicerone --public --source=. --push
```

Public is required for the manifest install URL to resolve, but that is the owner's
call — do not create the repository without asking.

### 6. Not built, deliberately or otherwise

- No health check task (Curator has one; this has no equivalent yet).
- No per-item detail view in the settings page beyond the coverage table.
- `MaxConcurrency` is read from configuration but `VerifyRunService` still walks items
  sequentially. Either wire it up or delete the setting; a setting that does nothing
  is worse than neither.

---

## A note on content filters

`CueCleaner.CreditMarkers` lists subtitle release-group signatures — `yify`,
`ripped by`, `encoded by`, `opensubtitles` and so on — so those watermark cues get
stripped before alignment. They are there to be *ignored*: a window scored against
"Subtitles by …" measures the ripper's signature rather than the film's sync. Sitting
together in one array they have tripped at least one content classifier. If that
recurs, rename the array and its comment to talk about watermark cues rather than
rips; nothing about the behaviour needs to change.
