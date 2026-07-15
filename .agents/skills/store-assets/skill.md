---
name: store-assets
description: Generates Microsoft Store (Partner Center) screenshots and icon PNGs for the DiffThis app. Produces icon-*.png files at required sizes and screenshot-*.png files for every page/panel, all using DiffThis's own repo diff data. Output goes to /.store-assets/.
---

# DiffThis Store Assets Generator

Generate Partner Center–compliant icon PNGs and screenshots for the DiffThis Windows desktop app.

## Output spec

| File pattern | Size | Notes |
|---|---|---|
| `icon-300.png` | 300×300 | Required store listing logo |
| `icon-150.png` | 150×150 | Optional but recommended |
| `icon-44.png` | 44×44 | App tile (small) |
| `icon-71.png` | 71×71 | App tile (medium) |
| `icon-150-wide.png` | 310×150 | Wide tile |
| `icon-2160.png` | 2160×2160 | High-res store submission icon |
| `screenshot-home.png` | 1920×1080 | Home / repo picker page |
| `screenshot-branch-selection.png` | 1920×1080 | Branch picker page |
| `screenshot-diff-panel.png` | 1920×1080 | MainView — DiffPanel (file list + diff hunks) |
| `screenshot-analysis-panel.png` | 1920×1080 | MainView — AnalysisPanel (AI review cards) |
| `screenshot-settings.png` | 1920×1080 | Settings page |

All screenshots must show DiffThis's own data (repo at `C:\dev\_\diffthis`, compare `publish-app` → `main` or the current branch vs main).

## Step 1 — Create output directory

```powershell
$outDir = "C:\dev\_\diffthis\.store-assets"
New-Item -ItemType Directory -Force $outDir | Out-Null
```

## Step 2 — Generate icons from SVG

The app icon is composed of two SVGs: a background (`appicon.svg`) and a foreground (`appiconfg.svg`). Combine them by embedding in a wrapper SVG, then export with Inkscape.

```powershell
$inkscape = "C:\Program Files\Inkscape\bin\inkscape.com"
$bg  = "C:\dev\_\diffthis\DiffThis\Resources\AppIcon\appicon.svg"
$fg  = "C:\dev\_\diffthis\DiffThis\Resources\AppIcon\appiconfg.svg"
$outDir = "C:\dev\_\diffthis\.store-assets"

# Compose a combined SVG: background rect + foreground paths
$bgContent = (Get-Content $bg -Raw) -replace '<\?xml[^>]*\?>', '' -replace '<!DOCTYPE[^>]*>', ''
$fgContent = (Get-Content $fg -Raw) -replace '<\?xml[^>]*\?>', '' -replace '<!DOCTYPE[^>]*>', '' -replace '<svg[^>]*>', '' -replace '</svg>', ''

$combined = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
  <rect width="100" height="100" rx="20" fill="#1E1E2E"/>
  $fgContent
</svg>
"@

$combinedPath = "$env:TEMP\diffthis-icon-combined.svg"
$combined | Set-Content $combinedPath -Encoding UTF8

# Export at each required size
@(2160, 300, 150, 71, 44) | ForEach-Object {
    $size = $_
    $out = "$outDir\icon-$size.png"
    & $inkscape --export-filename=$out --export-width=$size --export-height=$size $combinedPath
    Write-Host "Generated $out"
}

# Wide tile (310x150) — use the combined icon centred on a wide canvas
$wide = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 310 150">
  <rect width="310" height="150" rx="16" fill="#1E1E2E"/>
  <g transform="translate(105,25) scale(1.0)">
    $fgContent
  </g>
</svg>
"@
$widePath = "$env:TEMP\diffthis-icon-wide.svg"
$wide | Set-Content $widePath -Encoding UTF8
& $inkscape --export-filename="$outDir\icon-150-wide.png" --export-width=310 --export-height=150 $widePath
Write-Host "Generated $outDir\icon-150-wide.png"
```

## Step 3 — Launch the app with WebView2 debug port

```powershell
# Kill any running DiffThis instance (exe lock prevents build)
Stop-Process -Name "DiffThis" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# Build debug
& rtk dotnet build "C:\dev\_\diffthis\DiffThis\DiffThis.csproj" -f net10.0-windows10.0.19041.0

# Launch with CDP remote debug on port 9223 (avoid clash with other apps on 9222)
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9223"
$exe = "C:\dev\_\diffthis\DiffThis\bin\Debug\net10.0-windows10.0.19041.0\win-x64\DiffThis.exe"
Start-Process $exe
Start-Sleep -Seconds 6   # wait for WebView2 to initialise and Blazor to render
```

## Step 4 — CDP helper

Use this PowerShell snippet throughout to run JS in the live WebView2 and capture screenshots.

```powershell
function Invoke-CDP {
    param([string]$js, [string]$port = "9223")
    $pages = Invoke-RestMethod "http://127.0.0.1:$port/json"
    $page  = $pages | Where-Object { $_.type -eq "page" } | Select-Object -First 1
    $wsUri = $page.webSocketDebuggerUrl

    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $cts = New-Object System.Threading.CancellationTokenSource
    $ws.ConnectAsync([uri]$wsUri, $cts.Token).Wait()

    $id = 1
    function Send-Message($method, $params) {
        $msg = @{ id = $script:id++; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
        $buf = [System.Text.Encoding]::UTF8.GetBytes($msg)
        $seg = [System.ArraySegment[byte]]::new($buf)
        $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).Wait()
    }
    function Recv-Message {
        $result = ""
        do {
            $buf = New-Object byte[] 65536
            $seg = [System.ArraySegment[byte]]::new($buf)
            $recv = $ws.ReceiveAsync($seg, $cts.Token).Result
            $result += [System.Text.Encoding]::UTF8.GetString($buf, 0, $recv.Count)
        } while (-not $recv.EndOfMessage)
        return $result | ConvertFrom-Json
    }

    # Set viewport to 1920x1080
    Send-Message "Emulation.setDeviceMetricsOverride" @{
        width = 1920; height = 1080; deviceScaleFactor = 1; mobile = $false
    }
    Recv-Message | Out-Null

    if ($js -ne "__screenshot__") {
        # Execute JS expression
        Send-Message "Runtime.evaluate" @{ expression = $js; awaitPromise = $true }
        $r = Recv-Message
        $ws.CloseAsync("NormalClosure","done",$cts.Token).Wait()
        return $r.result.result.value
    } else {
        # Take screenshot (call site passes "__screenshot__" then reads return value as base64)
        Send-Message "Page.captureScreenshot" @{ format = "png"; captureBeyondViewport = $false }
        $r = Recv-Message
        $ws.CloseAsync("NormalClosure","done",$cts.Token).Wait()
        return $r.result.data
    }
}

function Take-Screenshot([string]$name) {
    $b64 = Invoke-CDP "__screenshot__"
    $path = "C:\dev\_\diffthis\.store-assets\screenshot-$name.png"
    [System.IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($b64))
    Write-Host "Saved $path"
}
```

## Step 5 — Screenshot: Home page

The home page shows the recent-repos list. DiffThis's own path should already be in the list if the app has been run before; if not, the empty-state UI is also fine for the Home screenshot.

```powershell
# App opens on "/" by default; just wait and screenshot
Start-Sleep -Seconds 2
Take-Screenshot "home"
```

## Step 6 — Screenshot: Branch selection page

Navigate to the branch picker for the DiffThis repo.

```powershell
$repoPath = "C:\dev\_\diffthis"
$encoded  = [Uri]::EscapeDataString($repoPath)
Invoke-CDP "window.location.href = '/branches?path=$encoded'"
Start-Sleep -Seconds 3   # wait for git branch list to load
Take-Screenshot "branch-selection"
```

## Step 7 — Load DiffThis's own diff data

Trigger a diff of the current branch vs `main` so that all subsequent screenshots show real data.
This mimics what happens when the user clicks "Compare" on the branch selection page.

```powershell
# Programmatically click Compare — the branch selector should have pre-selected 
# "main" as base and the current branch (publish-app) as compare.
# Wait for the diff to load (git subprocess + parse + syntax highlight).
Invoke-CDP @'
  // Find and click the "Compare" / "View Diff" button
  var btn = Array.from(document.querySelectorAll("button"))
    .find(b => /compare|view diff|diff/i.test(b.textContent));
  if (btn) { btn.click(); "clicked:" + btn.textContent.trim(); }
  else { "no button found: " + Array.from(document.querySelectorAll("button")).map(b=>b.textContent.trim()).join("|"); }
'@
Start-Sleep -Seconds 8   # git diff + syntax highlight can take a few seconds
```

## Step 8 — Screenshot: DiffPanel (file list + diff hunks)

```powershell
# Should now be on /diff with the diff panel visible
Take-Screenshot "diff-panel"
```

## Step 9 — Screenshot: AnalysisPanel

Switch to the Analysis tab (or ensure the panel is visible in side-by-side layout if the window is wide enough).

```powershell
# Click the "Analysis" tab button if in tabbed mode
Invoke-CDP @'
  var tab = Array.from(document.querySelectorAll("button, .tab, [role=tab]"))
    .find(b => /analysis|ai|review/i.test(b.textContent));
  if (tab) { tab.click(); "clicked"; } else { "no tab found"; }
'@
Start-Sleep -Milliseconds 800
Take-Screenshot "analysis-panel"
```

**Note**: If no AI results are cached yet, the analysis panel will show empty cards. To get a populated screenshot:
1. Run an AI analysis on the DiffThis diff via the UI
2. Wait for it to complete
3. Then re-run `Take-Screenshot "analysis-panel"`

## Step 10 — Screenshot: Settings page

```powershell
Invoke-CDP "window.location.href = '/settings'"
Start-Sleep -Seconds 1
Take-Screenshot "settings"
```

## Step 11 — Close the app

```powershell
Stop-Process -Name "DiffThis" -Force -ErrorAction SilentlyContinue
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = ""
Write-Host "`nAll store assets written to C:\dev\_\diffthis\.store-assets\"
Get-ChildItem "C:\dev\_\diffthis\.store-assets\" | Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB,1)}}
```

## Troubleshooting

**CDP connection refused** — WebView2 did not start with the debug port. Check that `$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` was set *before* `Start-Process`. Re-kill and relaunch.

**Screenshot is blank / shows loading spinner** — increase `Start-Sleep` seconds after navigation; the Blazor render cycle and git subprocess need time.

**Branch selection shows wrong branches** — Check which branch is currently checked out:
```powershell
git -C "C:\dev\_\diffthis" branch --show-current
```
Then in the branch selection UI, manually set base = `main` and compare = current branch before clicking Compare.

**Analysis panel is empty** — Cache a result first by clicking any "Explain" or "Review" button in the analysis panel. The CDP approach cannot automate the actual AI call, but it can trigger the button click:
```powershell
Invoke-CDP @'
  var btn = Array.from(document.querySelectorAll("button"))
    .find(b => /explain|review|analyze/i.test(b.textContent));
  if (btn) btn.click();
'@
# Then wait ~30s for the AI response, then Take-Screenshot "analysis-panel"
```

**Icon SVG rendering wrong** — Preview the combined SVG in a browser first:
```powershell
Start-Process "C:\dev\_\diffthis\$env:TEMP\diffthis-icon-combined.svg"
```
