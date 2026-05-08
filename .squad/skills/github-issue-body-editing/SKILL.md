---
name: "GitHub issue body editing"
description: "Use --body-file, never string concat. Fixes newline stripping bug."
domain: "github-automation"
confidence: "high"
source: "earned (two failure modes, one successful repair pattern)"
tools:
  - name: "gh issue edit"
    description: "Edit GitHub issue with file-based body"
    when: "Always use --body-file for multi-line body edits"
  - name: "gh issue view"
    description: "Retrieve current issue body"
    when: "When modifying and re-uploading an issue body"
---

## Context

When editing GitHub issue bodies with multi-line content (markdown, code blocks, lists), the gh CLI can corrupt the body by stripping internal newlines. This happens specifically when using gh api with string concatenation via the -f body="..." syntax.

## Patterns

**SAFE PATTERN (ALWAYS USE):**
```powershell
$body = gh issue view {NUMBER} --json body -q .body
# Modify $body preserving newlines (append, prepend, or rewrite)
$tmp = [System.IO.Path]::GetTempFileName()
$body | Set-Content -Path $tmp -Encoding UTF8 -NoNewline
gh issue edit {NUMBER} --body-file $tmp
Remove-Item $tmp
```

This ensures:
- Newlines are preserved during modification
- The full file is passed to gh, not embedded in a command string
- Encoding is explicit (UTF8)
- Temp file is cleaned up

## Examples

### Example 1: Prepend a note to issue body

```powershell
$number = 20
$body = gh issue view $number --json body -q .body
$prefix = "**NOTE:** This issue body was corrupted and has been repaired.`n`n"
$body = "$prefix$body"
$tmp = [System.IO.Path]::GetTempFileName()
$body | Set-Content -Path $tmp -Encoding UTF8 -NoNewline
gh issue edit $number --body-file $tmp
Remove-Item $tmp
```

### Example 2: Batch repair multiple issues

```powershell
$issues = @(20, 21, 22, 23, 25)
foreach ($number in $issues) {
    $body = gh issue view $number --json body -q .body
    $tmp = [System.IO.Path]::GetTempFileName()
    $body | Set-Content -Path $tmp -Encoding UTF8 -NoNewline
    gh issue edit $number --body-file $tmp
    Remove-Item $tmp
}
```

## Anti-Patterns

**NEVER USE:**
```powershell
gh api repos/{owner}/{repo}/issues/{number} -f body="Part of #N`n$body"
```

**Why:** The `-f body="..."` syntax embeds the string in the command line. PowerShell or the shell may expand escape sequences, but more critically, gh's string handling via `-f` does not preserve newlines correctly. The result is a body with all newlines stripped, corrupting markdown formatting.

**ALSO NEVER:**
```powershell
$body = gh issue view 20 --json body -q .body
gh issue edit 20 -f body="$body"
```

This re-corrupts the body by re-embedding it in -f syntax.
