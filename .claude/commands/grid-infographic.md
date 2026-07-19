---
name: grid-infographic
description: Generate a premium technical thought-leadership infographic in the Brij-Kishore-Pandey numbered-grid aesthetic with a central hero hub (ratchet/wheel/diagram) and the proven Gemini "Nano Banana Pro" pipeline. Includes blueprint-approval gate to catch content errors BEFORE the API call. Use when the user wants the LinkedIn-architecture-explainer style for a technical concept with multiple distinct sections + a centerpiece diagram.
argument-hint: "<topic + 6-8 sections + central hub concept + optional code snippet + optional triggers>"
allowed-tools: Bash, Read, Write
disable-model-invocation: true
---

# Grid Infographic — Numbered Sections + Hero Centerpiece

Generate a premium technical infographic in the numbered-section aesthetic
popularized by Brij Kishore Pandey (MCP architecture posts) and Anurag Karuparti
(SDLC vs AIDLC). The shape: a header with title + author byline + terminal
prompt; an 8-section numbered grid; a HUGE central "hero" section dominating
the middle (usually a hub diagram, wheel, or comparison visual); a real code
block somewhere as proof; a 2x1 takeaway row at the bottom; and a branded
footer bar.

**Default backend: Gemini Nano Banana Pro (`gemini-3-pro-image`)** at 2K
resolution. Free tier. Renders text more accurately than gpt-image-1.5 for
text-dense layouts and supports the `imageConfig.imageSize: "2K"` parameter
for sharp output (~1696×2528 px).

## Why this style works

It pairs **information density** (numbered grid lets you cram in 8 distinct
ideas) with a **visual hero** (the centerpiece section earns the eye) and
**proof** (the real code block silences "but does it work?"). It's the
canonical "technical thought-leadership" infographic shape on LinkedIn.

## Steps

### 1. Validate

- Check `GEMINI_API_KEY` is set; if not, stop with setup instructions
  (https://aistudio.google.com/apikey — free tier, no credit card needed)
- Check `jq` is available
- If `$ARGUMENTS` is empty, ask the user what to visualize and stop

### 2. Parse the content

From `$ARGUMENTS`, extract:

- **Main title** (5-10 words, the *outcome* — e.g. *"The system behind 'AI
  that codes the way I want'"*)
- **Subtitle** (1 line, ~10 words — the methodology/framing name, e.g.
  *"The Encoding Loop — a method that compounds quality"*)
- **Author byline** (e.g. "By [Name] · [tagline]")
- **Terminal corner snippet** (e.g. `~/claude $`, `~/k8s $`, `~/mcp $` —
  developer-aesthetic touch in the top-left)
- **8 numbered sections** with titles + content. Typical shape:
  - **01 THE PROBLEM** — short text describing the pain
  - **02 [CONTEXT/STAGES]** — small row of icons or a quick visual
  - **03 [THE METHOD/HERO]** — *full-width tall hero section with the
    central diagram (wheel, hub, flowchart, ratchet)*
  - **04 [STRUCTURE]** — list of components or surfaces
  - **05 [LADDER/TIERS]** — colored stacked bars
  - **06 LIVE EXAMPLE** — *full-width row with a real code snippet*
  - **07 [DISCIPLINES/PRINCIPLES]** — two-column list
  - **08 THE TAKEAWAY** — big bold quote + short supporting line
- **Footer tagline** (one line for the navy footer bar)
- **Brand mark** (small badge bottom-right, e.g. "EL · Encoding Loop")
- **Optional: trigger entry points** — labels on the side of the hero feeding
  arrows INTO the central element (proves the method handles multiple inputs)
- **Optional: callouts** — small amber labels around the central spine

If the user's input is sparse, ask 2-3 clarifying questions. Don't invent
content.

### 2.5. Present the blueprint for approval (MANDATORY before any API call)

Before constructing the image prompt, present the parsed content to the user
as a structured blueprint and **explicitly ask for approval**. Hard gate —
do not proceed without affirmative approval.

Why this matters: image-gen costs real money (gpt-image at $0.29/image) OR
free but rate-limited (Gemini), AND models hallucinate when content is
mis-framed. Catching content errors at the *blueprint* stage costs nothing.
Catching them in the rendered output costs another generation. Every
mis-frame the blueprint catches saves a regen.

Blueprint format — render filled in with the parsed content:

```markdown
## Blueprint for review — please verify before I generate

**Main title:** <<TITLE>>
**Subtitle:** <<SUBTITLE>>
**Author byline:** <<BYLINE>>
**Terminal corner:** <<TERMINAL>>

**Sections:**

| # | Title | Content summary |
|---|---|---|
| 01 | <<S01_TITLE>> | <<S01_SUMMARY>> |
| 02 | <<S02_TITLE>> | <<S02_SUMMARY>> |
| 03 | <<S03_TITLE>> *(HERO)* | <<S03_SUMMARY>> |
| 04 | <<S04_TITLE>> | <<S04_SUMMARY>> |
| 05 | <<S05_TITLE>> | <<S05_SUMMARY>> |
| 06 | <<S06_TITLE>> *(CODE)* | <<S06_SUMMARY>> |
| 07 | <<S07_TITLE>> | <<S07_SUMMARY>> |
| 08 | <<S08_TITLE>> | <<S08_SUMMARY>> |

**Hero centerpiece (Section 03) details:**
- Hub title: <<HUB_TITLE>>
- Spine (4 boxes): <<SPINE>>
- Callouts (4): <<CALLOUTS>>
- Triggers feeding in (5, optional): <<TRIGGERS>>
- Signature element: <<SIGNATURE>> (e.g. ratchet teeth ring, flywheel, etc.)

**Code block (Section 06):**
```
<<CODE>>
```

**Footer tagline:** <<FOOTER_TAGLINE>>
**Brand mark:** <<BRAND>>

**Palette:** <<PALETTE>> (default: pure white #FFFFFF + navy #1E40AF + amber #D97706)

**Estimated cost:** Free (Nano Banana Pro, Gemini free tier with rate limits)

---

**Reply:**
- `approve` / `yes` / `ship it` → I'll generate
- `edit <field>: <new value>` → revise and re-show
- `cancel` → no API call
```

Content checks to flag explicitly above the blueprint:

- ⚠ **Fabricated data**: any specific numbers, percentages, weeks, dollar
  amounts, or counts not grounded in the user's input
- ⚠ **Scope mismatches**: a label that implies broader scope than its
  artifact (e.g. "Size budget (500)" implies whole canon when only one file)
- ⚠ **Duplicate callouts**: any label that appears twice in the central hub
  or section grid
- ⚠ **Long text in wrong slot**: any flowchart step or bullet > 4 words, any
  title > 6 words — likely to hallucinate
- ⚠ **Spine reads as bug-fix only**: if Section 03's spine boxes are
  "Finding/Fix/..." they imply purely reactive work. Triggers should live
  OUTSIDE the wheel as arrows feeding IN; the spine inside should be the
  *response* (Pick smallest surface / Encode / Cross-reference / Promote)

On `edit <field>: <value>`: revise just that field, re-display the FULL
blueprint, ask for approval again. Loop until approval or cancel.

### 3. Construct the image prompt

Build a single deeply-detailed prompt (~4000-5500 chars) following this
template. Fill `<<...>>` placeholders with parsed content. Render only EXACT
text strings provided; do not invent labels.

```
Create a premium technical editorial infographic, 1024x1536 portrait
orientation, HIGHEST resolution and SHARPNESS. Background pure white #FFFFFF
(no cream tint). Brij Kishore Pandey aesthetic — numbered sections in clean
bordered boxes with navy number badges, info-dense, premium developer
thought-leadership feel.

IMPORTANT: Use larger, readable body text inside section boxes (~14pt minimum
body, 18pt+ for titles, no tiny illegible text anywhere). Generous
whitespace, but text must be readable at thumbnail scale.

PALETTE:
- Background pure white #FFFFFF
- Primary accent deep navy #1E40AF (numbers, titles, key labels)
- Secondary accent amber/gold #D97706 (signature elements + small accents)
- Text dark charcoal #1E293B
- Box borders thin navy #1E40AF (1.5px)
- Code blocks pale cream #FFF8E7 with dark monospace
- Tier bars: light orange #FED7AA, light blue #DBEAFE, light green #BBF7D0

HEADER (top ~8%):
- Tiny monospace top-left gray: "<<TERMINAL>>"
- MAIN TITLE bold serif display navy: "<<TITLE>>"
- SUBTITLE italic blue smaller: "<<SUBTITLE>>"
- Tiny attribution: "<<BYLINE>>"

ROW 1 (two boxes side by side):
- 01 <<S01_TITLE>> — <<S01_CONTENT>>
- 02 <<S02_TITLE>> — <<S02_CONTENT>>

ROW 2 (HUGE full-width hero, ~30% of canvas):
- 03 <<S03_TITLE>> — large numbered "03" badge top-left.
- Central <<SHAPE>> with title "<<HUB_TITLE>>" and <<SIGNATURE>> ring around it.
- Inside hub: "<<INSIDE_HEADER>>" header, then <<SPINE_DESCRIPTION>>.
- Four callouts around spine, each exactly once: <<CALLOUTS_WITH_POSITIONS>>.
- Bottom of hub: <<HUB_BOTTOM_BOXES>>.
- OUTSIDE wheel, LEFT side, <<N>> TRIGGER ENTRY POINTS with navy arrows
  pointing INTO wheel: <<TRIGGERS_WITH_ICONS>>.
- Italic caption: "<<TRIGGER_CAPTION>>".

ROW 3 (two boxes):
- 04 <<S04_TITLE>> — <<S04_CONTENT>>
- 05 <<S05_TITLE>> — <<S05_CONTENT>>

ROW 4 (full-width):
- 06 <<S06_TITLE>> — title in bold navy ONLY (do not prepend other section names).
  Below, code block in monospace dark text on pale cream #FFF8E7 background:

\`\`\`
<<CODE>>
\`\`\`

Caption: "<<CODE_CAPTION>>"

ROW 5 (two boxes):
- 07 <<S07_TITLE>> — <<S07_CONTENT>>
- 08 <<S08_TITLE>> — <<S08_CONTENT>>

FOOTER (bottom ~5%):
Navy filled bar full width. Small star icon on left. Centered cream bold
readable: "<<FOOTER_TAGLINE>>"
Bottom-right small: "<<BRAND_MARK>>"

CRITICAL: Each section title stays in its own box; no leaking titles between
sections. Render every text element exactly as specified.

FEEL: Premium technical thought-leadership, magazine-cover sharp. White
background. Section 03 with the central hero dominates. Triggers feed in
(if specified). Real code gate proves the mechanical floor. Body text
readable.
```

### 4. Generate via Gemini Nano Banana Pro at 2K resolution

```bash
curl -s -X POST "https://generativelanguage.googleapis.com/v1beta/models/gemini-3-pro-image:generateContent?key=$GEMINI_API_KEY" \
  -H "Content-Type: application/json" \
  -d "$(jq -n --arg p "$PROMPT" '{
    contents: [{parts: [{text: $p}]}],
    generationConfig: {
      responseModalities: ["IMAGE"],
      imageConfig: {imageSize: "2K"}
    }
  }')"
```

Then extract image data from `.candidates[0].content.parts[].inlineData.data`
(base64), decode to JPEG. Output is typically 1696×2528 at ~2.5 MB.

```bash
B64=$(echo "$RESPONSE" | jq -r '.candidates[0].content.parts[] | select(.inlineData) | .inlineData.data' | head -1)
echo "$B64" | base64 --decode > <output>.jpg
```

Default output path: `./<prefix>-hero.jpg`. Accept `--output DIR` and
`--prefix NAME` flag overrides.

### 5. Backend alternatives (rare)

The skill's default is Nano Banana Pro because for text-dense infographics
it's the best free option AND it supports 2K. Alternatives, only if needed:

- `--backend openai` — gpt-image-1.5 at OpenAI. $0.29/image. More
  consistent magazine-cover aesthetic but worse at text accuracy.
  Endpoint: `https://api.openai.com/v1/images/generations`.
- `--backend html` — generates a paired HTML+CSS+SVG file that renders to
  PNG via Playwright (see `.claude/scripts/render-hero.sh`). 100% text
  accuracy, fully editable, deterministic. No API cost. Aesthetic is
  "designed by a developer" not "designed by a designer" — trade-off.
- `--backend recraft` — Recraft V3. 1000-char prompt limit makes this
  unusable for our 4000+ char prompts; documented for completeness only.

### 6. Report

Tell the user:

- The file path (e.g. `./encoding-loop-hero.jpg`)
- Backend used + cost + dimensions
- A summary of what landed (was the spine correct? did all callouts render?
  any visible glitches?)
- Suggestions for refinement if glitches: re-roll free, or `edit <field>:`
  for content change

## Reference example: `docs/encoding-loop-hero.jpg`

This skill produced [docs/encoding-loop-hero.jpg](docs/encoding-loop-hero.jpg)
— a Pandey-style numbered-grid infographic with a central ratchet-wheel
hero, 5 trigger entry points feeding in, a real CI gate code block, and
footer *"Each iteration → Feedback loop → Continuous improvement & drift
prevention"*. Use as the canonical example of the output shape.

The HTML/SVG counterpart at [docs/encoding-loop-hero.html](docs/encoding-loop-hero.html)
is editable via CSS edits and rendered via `.claude/scripts/render-hero.sh`
— that's the "fully deterministic" version for when text accuracy matters
more than designer polish.

### Taxonomy extension notes

The encoding-loop reference example uses a **5-surface** taxonomy (Canon,
PR-time rules, Architecture review, Procedures, Deep context). This is the
canon-shipping taxonomy for *Claude Code* as of 2026. Other agent platforms
add a sixth surface worth knowing about:

6. **Trigger-loaded canon** — instruction sets that auto-fire when a keyword
   appears in the conversation or a path is touched. OpenHands microagents
   (`.openhands/microagents/`) are the canonical example. Claude Code lacks a
   native equivalent but the pattern is buildable via hooks + auto-invoke
   skills.

When generating an infographic for a multi-platform method, consider whether
the surface taxonomy should be 5 or 6. The grid template accommodates either —
6 surfaces fit cleanly into Section 04 as a single column of 6 items, or split
2×3.

See `docs/decisions/2026-07-17-sixth-surface-okl.md`
for the deeper take on trigger-loaded as a *load-timing* dimension.

## When NOT to use this skill

- For a simple single-column infographic — use `/visual-explainer --style infographic`
- For a hand-drawn whiteboard look — use `/visual-explainer --style whiteboard`
- For a mindmap — use `/visual-explainer --style mindmap`
- For content with fewer than 6 distinct sections or no natural centerpiece
  diagram — pick a different layout; this template will fight you

## Cost

- **Nano Banana Pro (default)**: free tier with rate limits, 1696×2528 JPEG
- **OpenAI gpt-image-1.5** (`--backend openai`): ~$0.29/image at 1024×1536 high quality
- **HTML/SVG** (`--backend html`): $0, fully deterministic, editable

## Setup

Set `GEMINI_API_KEY` in `~/.zshrc`:

```bash
export GEMINI_API_KEY="AIza..."  # from aistudio.google.com/apikey
```

`OPENAI_API_KEY` is only needed for `--backend openai`.
