# WebView2 Browser — TODO & Roadmap

## ✅ Done
- [x] Core: manual init (bypass WPF control init failure)
- [x] Off-screen controller (-5000,-5000, 1280x800)
- [x] 16 tools: navigate, click, click_at, cursor_move, type, key, select, scroll, hover, wait, screenshot, screenshot_full, screenshot_series, extract, evaluate, console
- [x] Auto-screenshot attachment (FinalizeAsync → MessageMetaStore)
- [x] Cursor overlay (magenta + white border)
- [x] Console interceptor (post-navigation)
- [x] Navigation wait on click (2s max)
- [x] CancellationToken support
- [x] Full-page screenshot (resize viewport)
- [x] Screenshot series (N frames at M ms)
- [x] BrowserView debug tab
- [x] Documentation in code comments

## 🔥 In Progress
- [ ] CDP Input events (trusted mouse/keyboard) — fixes Enter/form submit
- [ ] AddScriptToExecuteOnDocumentCreatedAsync (pre-load injection)
- [ ] Auto-suspend (idle 2min → TrySuspendAsync, tool call → Resume)
- [ ] Network diagnostics (CDP Network.enable → requests, errors, timing)

## 📋 TODO (when needed)
- [ ] **PDF tools**: printToPDF, read PDF, extract text from PDF
- [ ] **Emulation**: setDeviceMetricsOverride (mobile viewport, UA spoofing)
- [ ] **Profile isolation**: separate cookie/storage per agent session
- [ ] **VirtualHost mapping**: test local web apps in browser
- [ ] **Fetch mock**: intercept/modify API requests (CDP Fetch.enable)
- [ ] **Tool sets / skills**: toggleable tool categories per task
- [ ] **Drag & drop**: browser_drag tool
- [ ] **Iframe support**: switch context to iframe
- [ ] **Download handling**: Browser.setDownloadBehavior
- [ ] **Geolocation spoofing**: Emulation.setGeolocationOverride

## 🧠 Ideas
- Network idle detection as a "wait" strategy (better than selector polling)
- Read AJAX/JSON responses directly (bypass DOM parsing)
- Track request timing for performance debugging
- Auto-detect CORS/404/500 errors and report in console tool
- "Page health" diagnostic: console errors + failed requests + JS exceptions

## Architecture Notes
- WebView2 WPF control init FAILS silently → manual CoreWebView2Environment + Controller
- Controller MUST be IsVisible=true (engine pauses if false) → off-screen at (-5000,-5000)
- 1px Grid in MainWindow provides HwndSource
- Fixed 1280x800 viewport (independent of app window size)
- Known risk: multi-monitor at (-5000,-5000) position
