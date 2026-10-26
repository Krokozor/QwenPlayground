# Pigeon MCP adapter test kit (docs/MESSANGER.md 14.1) - raw JSON-RPC over POST /mcp.
# No SDK, no MCP client: this is the wire the clients actually speak.
#
#   .\tests\pigeon_mcp.ps1
#   .\tests\pigeon_mcp.ps1 -Server http://127.0.0.1:8199 -OwnerToken <owner token>
#
# Covers: initialize handshake (legacy revision), 202 on notifications, tools/list,
# tools/call round trip (join -> send -> read -> who), unknown method, GET -> 405,
# cursor advance, and `since` reaching back without moving it.
#
# ASCII only, no BOM: PowerShell 5.1 reads a BOM-less .ps1 as ANSI, where UTF-8 em
# dashes (E2 80 94) decode to 0x94 = a closing quote and eat the rest of the script.
param(
    [string]$Server = "http://127.0.0.1:8199",
    [string]$OwnerToken = "",
    [string]$Channel = "mcp-selftest",
    [string]$Client = "ps-selftest"
)

$script:Id = 0
$script:Fails = 0
$script:Session = ""

function NextId { $script:Id++; return $script:Id }

function Rpc($method, $params, $token) {
    $headers = @{ "Content-Type" = "application/json"; "Accept" = "application/json, text/event-stream" }
    if ($token) { $headers["Authorization"] = "Bearer $token" }
    elseif ($script:Session) { $headers["Mcp-Session-Id"] = $script:Session }
    $body = @{ jsonrpc = "2.0"; id = (NextId); method = $method }
    if ($null -ne $params) { $body["params"] = $params }
    try {
        $r = Invoke-WebRequest -Uri "$Server/mcp" -Method Post -Headers $headers `
            -Body (ConvertTo-Json $body -Depth 10 -Compress) -UseBasicParsing
        # Carry the session forward exactly like a real MCP client: every POST is a fresh
        # HTTP request, so the identity survives only via the Mcp-Session-Id header.
        if ($r.Headers["Mcp-Session-Id"]) { $script:Session = $r.Headers["Mcp-Session-Id"] }
        return @{ Status = $r.StatusCode; Json = ($r.Content | ConvertFrom-Json) }
    } catch {
        # An HTTP error still carries a JSON-RPC error body - read it rather than dropping it.
        $resp = $_.Exception.Response
        $text = ""
        if ($resp) { $text = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd() }
        if ($text) {
            try { return @{ Status = [int]$resp.StatusCode; Json = ($text | ConvertFrom-Json) } }
            catch { return @{ Status = [int]$resp.StatusCode; Json = $null; Raw = $text } }
        }
        return @{ Status = 0; Json = $null; Raw = $_.Exception.Message }
    }
}

function Note($result) {
    if ($result.Json -and $result.Json.result -and $result.Json.result.content) {
        return ($result.Json.result.content | ForEach-Object { $_.text }) -join "`n"
    }
    if ($result.Json -and $result.Json.result -and $result.Json.result.instructions) {
        return $result.Json.result.instructions
    }
    if ($result.Json -and $result.Json.error) { return $result.Json.error.message }
    return $result.Raw
}

function Check($name, $condition, $detail) {
    if ($condition) { Write-Host "  ok   $name" -ForegroundColor Green }
    else {
        Write-Host ("  FAIL " + $name + "`n       " + $detail) -ForegroundColor Red
        $script:Fails++
    }
}

Write-Host "== 1. initialize (legacy, initialize-based revision) =="
$init = Rpc "initialize" @{
    protocolVersion = "2025-11-25"
    capabilities = @{}
    clientInfo = @{ name = $Client; version = "0.0.0-selftest" }
}
Check "initialize answered" ($null -ne $init.Json.result) (Note $init)
Check "protocolVersion echoed" ($init.Json.result.protocolVersion -eq "2025-11-25") (Note $init)
Check "tools capability advertised" ($null -ne $init.Json.result.capabilities.tools) (Note $init)
Check "serverInfo is pigeon" ($init.Json.result.serverInfo.name -eq "pigeon") (Note $init)
Write-Host "  instructions: $($init.Json.result.instructions)"

Write-Host "== 2. notification (no id) -> 202 Accepted, empty body =="
$note = Invoke-WebRequest -Uri "$Server/mcp" -Method Post `
    -Headers @{ "Content-Type" = "application/json" } `
    -Body '{"jsonrpc":"2.0","method":"notifications/initialized"}' -UseBasicParsing
Check "202 on notification" ($note.StatusCode -eq 202) "got $($note.StatusCode)"

Write-Host "== 3. ping + unknown method =="
$ping = Rpc "ping" @{}
Check "ping returns a result" ($null -ne $ping.Json.result) (Note $ping)
$bogus = Rpc "no/such/method" @{}
Check "unknown method -> -32601" ($bogus.Json.error.code -eq -32601) (Note $bogus)

Write-Host "== 4. GET /mcp -> 405 (no GET stream in this revision) =="
try {
    Invoke-WebRequest -Uri "$Server/mcp" -Method Get -UseBasicParsing | Out-Null
    Check "GET rejected" $false "GET succeeded"
} catch {
    Check "GET rejected" ([int]$_.Exception.Response.StatusCode -eq 405) "got $([int]$_.Exception.Response.StatusCode)"
}

Write-Host "== 5. tools/list =="
$tools = Rpc "tools/list" @{}
$names = ($tools.Json.result.tools | ForEach-Object { $_.name }) -join ", "
Write-Host "  catalogue: $names"
foreach ($expected in "who", "channels", "join", "read", "send", "status", "roles") {
    Check "catalogue has $expected" ($tools.Json.result.tools.name -contains $expected) $names
}
$readTool = $tools.Json.result.tools | Where-Object { $_.name -eq "read" }
Check "read has an inputSchema" ($null -ne $readTool.inputSchema.properties) (Note $tools)
$sendTool = $tools.Json.result.tools | Where-Object { $_.name -eq "send" }
Check "send marks text required" ($sendTool.inputSchema.required -contains "text") (Note $tools)
# The long names stay as aliases: a client that already learned pigeon_send must not break
# on a rename. Checked AFTER seating (section 8) - read/send need a channel to work in.
Write-Host "== 6. auto-join: the client exists now, without a token =="
$who = Rpc "tools/call" @{ name = "pigeon_who"; arguments = @{} }
Check "auto-joined as mcp:$Client" ((Note $who) -match "mcp:$Client") (Note $who)
Check "identity is a pull client (wake=none)" ((Note $who) -match "wake=none") (Note $who)

Write-Host "== 7. owner creates the channel and seats the mcp client =="
if ($OwnerToken) {
    $ownerHeaders = @{ "Content-Type" = "application/json"; "Authorization" = "Bearer $OwnerToken" }
    Invoke-WebRequest -Uri "$Server/api/channels" -Method Post -Headers $ownerHeaders `
        -Body (@{ name = $Channel; description = "MCP adapter self-test" } | ConvertTo-Json -Compress) -UseBasicParsing | Out-Null
    Write-Host "  created $Channel"
    Invoke-WebRequest -Uri "$Server/api/membership" -Method Post -Headers $ownerHeaders `
        -Body (@{ identity = "mcp:$Client"; channel = $Channel } | ConvertTo-Json -Compress) -UseBasicParsing | Out-Null
    Write-Host "  seated mcp:$Client"
} else {
    Write-Host "  (skipped - pass -OwnerToken to run the channel half of the test)"
}

Write-Host "== 8. tools/call round trip =="
$join = Rpc "tools/call" @{ name = "pigeon_join"; arguments = @{ channel = $Channel } }
Check "pigeon_join seats the client" ((Note $join) -match "Seated in #$([regex]::Escape($Channel))\b") (Note $join)

$send = Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = "hello from the MCP self-test" } }
Check "pigeon_send stored a message" ((Note $send) -match "Sent \[\d+\] to #$([regex]::Escape($Channel))\b") (Note $send)
$sentId = [int]([regex]::Match((Note $send), 'Sent \[(\d+)\]').Groups[1].Value)

$readBack = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{} }
Check "pigeon_read sees the message" ((Note $readBack) -match "hello from the MCP self-test") (Note $readBack)

$status = Rpc "tools/call" @{ name = "pigeon_status"; arguments = @{ status = "self-test" } }
Check "pigeon_status accepted" ((Note $status) -match "self-test") (Note $status)

Write-Host "== 8b. legacy pigeon_-prefixed names still resolve =="
# The tools were renamed to bare names (the server is already called "pigeon"). Old configs
# may still say pigeon_send, so the long names stay as aliases.
foreach ($alias in "pigeon_who", "pigeon_read", "pigeon_send") {
    $aliasArgs = if ($alias -eq "pigeon_send") { @{ text = "alias check (long name)" } } else { @{} }
    $aliasResult = Rpc "tools/call" @{ name = $alias; arguments = $aliasArgs }
    Check "alias $alias still resolves" ($aliasResult.Json.result.isError -ne $true) (Note $aliasResult)
}
# The alias send moved the cursor target for the checks below; drain it so section 9 still
# starts from a caught-up cursor.
Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{} } | Out-Null

Write-Host "== 9. the cursor advanced - a second read is empty =="
$again = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{} }
Check "cursor advanced" ((Note $again) -match "Nothing new since") (Note $again)

Write-Host "== 10. from=0 replays history without moving the cursor =="
$back = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ from = 0 } }
Check "from=0 replays history" ((Note $back) -match "hello from the MCP self-test") (Note $back)
$still = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{} }
Check "cursor did not move" ((Note $still) -match "Nothing new since") (Note $still)

Write-Host "== 11. mentions resolve server-side =="
$mention = Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = "hey @owner, seen?" } }
Check "mention resolved to an id" ((Note $mention) -match "mentions resolved to 1 id\(s\): owner") (Note $mention)

Write-Host "== 12. identity: connection is recorded and shown =="
$who2 = Rpc "tools/call" @{ name = "pigeon_who"; arguments = @{} }
Check "who reports the connection" ((Note $who2) -match "from mcp#[0-9a-f]+@") (Note $who2)

Write-Host "== 13. a server-named identity stays mentionable =="
# UniqueAlias disambiguates on the id tail. With an IPv4 host that tail contains dots, and
# @tokens stop at '.', so an unsanitized alias could not be addressed at all. Provision two
# ids whose tails differ and check the generated alias survives the mention regex.
if ($OwnerToken) {
    $ownerHeaders = @{ "Content-Type" = "application/json"; "Authorization" = "Bearer $OwnerToken" }
    foreach ($ip in "192.168.1.77", "192.168.1.78") {
        Invoke-WebRequest -Uri "$Server/api/join" -Method Post -Headers $ownerHeaders `
            -Body (@{ id = "mcp:probe@$ip"; alias = "probe"; kind = "agent"; harness = "mcp"; wake = "none" } |
                   ConvertTo-Json -Compress) -UseBasicParsing | Out-Null
    }
    $who3 = Rpc "tools/call" @{ name = "pigeon_who"; arguments = @{} }
    Check "the colliding alias was disambiguated" ((Note $who3) -match "@probe-\S") (Note $who3)
    $generated = ([regex]::Match((Note $who3), "@(probe-[A-Za-z0-9_-]+)")).Groups[1].Value
    Check "generated alias has no dot (mentionable)" (($generated -notmatch "\.") -and $generated.Length -gt 0) "got '$generated'"
    $mention2 = Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = "ping @$generated" } }
    Check "generated alias resolves as a mention" ((Note $mention2) -match "mentions resolved to 1 id\(s\): mcp:probe") (Note $mention2)
} else {
    Write-Host "  (skipped - pass -OwnerToken to run the collision half of the test)"
}

Write-Host "== 14. thread shape is visible on read =="
# thread/replyTo live in the envelope but were not rendered by either reader, so a reply and a
# standalone utterance in the same thread looked identical in the transcript.
$root = Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = "thread root for the self-test" } }
$rootId = [int]([regex]::Match((Note $root), 'Sent \[(\d+)\]').Groups[1].Value)
$reply = Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = "a reply"; thread = $rootId; replyTo = $rootId } }
Check "reply was stored" ((Note $reply) -match "Sent \[\d+\]") (Note $reply)
$interject = Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = "an interjection in the same thread"; thread = $rootId } }
Check "interjection was stored" ((Note $interject) -match "Sent \[\d+\]") (Note $interject)

$threaded = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ from = 0 } }
$view = Note $threaded
Check "thread root is marked" ($view -match "\[thread $rootId\]") $view
Check "reply shows thread and its target" ($view -match "\[thread $rootId, reply->$rootId\]") $view
Check "interjection is distinguishable from a reply" (([regex]::Matches($view, "\[thread $rootId\]")).Count -ge 1) $view

Write-Host "== 15. from/to is an exact window =="
# `from` is EXCLUSIVE, matching the cursor ("read up to id N", so from=N means "everything
# after N"); `to` is inclusive. The reply header prints the id range it actually returned.
for ($i = 1; $i -le 6; $i++) {
    Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = "window filler $i" } } | Out-Null
}
$all = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ from = 0 } }
$ids = [regex]::Matches((Note $all), "\[(\d+)\]") | ForEach-Object { [int]$_.Groups[1].Value }
$lo = $ids[0]; $hi = $ids[$ids.Count - 1]
$window = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ from = $lo; to = ($lo + 3) } }
$got = [regex]::Matches((Note $window), "\[(\d+)\]") | ForEach-Object { [int]$_.Groups[1].Value }
Check "window is [from+1..to]" (($got.Count -eq 3) -and ($got[0] -eq ($lo + 1)) -and ($got[2] -eq ($lo + 3))) "lo=$lo got=$($got -join ',')"
Check "the reply names the range it returned" ((Note $window) -match "ids $($lo + 1)-$($lo + 3)") (Note $window)

Write-Host "== 16. last caps and reports what it trimmed =="
$tail = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ from = 0; last = 3 } }
$tailView = Note $tail
Check "last caps the result" (([regex]::Matches($tailView, "\[\d+\] @")).Count -eq 3) $tailView
Check "the trim is reported" ($tailView -match "skipped \d+-\d+ \(\d+ msgs\)") $tailView
Check "the trim says who spoke" ($tailView -match "@[\w-]+ x\d+") $tailView

Write-Host "== 17. filter: mine needs someone else to filter out =="
# Everything in this channel is authored by the mcp client, so `mine` would legitimately
# return all of it. The owner posts a few so the filter has something to exclude - which
# requires seating the owner first: creating a channel through the API does NOT seat its
# creator, only the web UI's create path does.
$ownerHeaders = @{ "Content-Type" = "application/json"; "Authorization" = "Bearer $OwnerToken" }
Invoke-WebRequest -Uri "$Server/api/membership" -Method Post -Headers $ownerHeaders `
    -Body (@{ identity = "owner"; channel = $Channel } | ConvertTo-Json -Compress) -UseBasicParsing | Out-Null
foreach ($i in 1..3) {
    Invoke-WebRequest -Uri "$Server/api/messages" -Method Post -Headers $ownerHeaders `
        -Body (@{ channel = $Channel; text = "owner note $i" } | ConvertTo-Json -Compress) -UseBasicParsing | Out-Null
}
Check "owner notes landed" (((Invoke-WebRequest -Uri "$Server/api/messages?channel=$Channel&since=0&wait=0" `
        -Headers $ownerHeaders -UseBasicParsing).Content) -match "owner note 1") "owner could not post"
$mine = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ from = 0; filter = "mine" } }
Check "mine drops the owner's messages" ((Note $mine) -notmatch "owner note") (Note $mine)
Check "mine keeps my own" ((Note $mine) -match "window filler") (Note $mine)
$onlyThread = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ from = 0; filter = "thread:$rootId" } }
$threadView = Note $onlyThread
Check "thread filter keeps the thread" ($threadView -match "a reply") $threadView
Check "thread filter drops other messages" ($threadView -notmatch "window filler") $threadView
$badFilter = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ filter = "nonsense" } }
Check "unknown filter is rejected" ((Note $badFilter) -match "Unknown filter") (Note $badFilter)

Write-Host "== 19. a historical read never pulls the cursor backwards =="
$before = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{} }
$cursorBefore = if ((Note $before) -match "Nothing new since #(\d+)") { [int]$Matches[1] } else { -2 }
Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ from = 0; to = 2 } } | Out-Null
$after = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{} }
$cursorAfter = if ((Note $after) -match "Nothing new since #(\d+)") { [int]$Matches[1] } else { -1 }
Check "cursor held at the head" ($cursorAfter -eq $cursorBefore) "before=$cursorBefore after=$cursorAfter"

Write-Host "== 18. mentions of you are called out when they are the ones trimmed =="
# The client cannot mention itself any more (the sender is dropped), so the only way for a
# message to be addressed to it is somebody else writing one.
Invoke-WebRequest -Uri "$Server/api/messages" -Method Post -Headers $ownerHeaders `
    -Body (@{ channel = $Channel; text = "@$Client pay attention, this one matters" } | ConvertTo-Json -Compress) -UseBasicParsing | Out-Null
# Bury it: the digest only warns about what a cap HID, not about what it showed.
foreach ($i in 1..5) {
    Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = "chatter $i" } } | Out-Null
}
$trimmed = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ from = 0; last = 2 } }
$trimView = Note $trimmed
Check "the trim flags messages addressed to you" ($trimView -match "mentioning you") $trimView
Check "but the shown ones are marked inline" (([regex]::Matches($trimView, "\[you were mentioned\]")).Count -ge 0) $trimView

Write-Host "== 20. a client that introduces nothing is named after its connection =="
# Regression: Sanitize("") used to return "client", so the "introduced nothing" branch never
# fired and EVERY anonymous client landed on the single id mcp:client.
# Drop the session first - otherwise this resolves as the seated client and never auto-joins.
$script:Session = ""
$anon = Rpc "tools/call" @{ name = "pigeon_who"; arguments = @{} }
Check "anonymous client is named after its host" ((Note $anon) -match "id=mcp:client-localhost\b") (Note $anon)

Write-Host "== 21. a client coming back reuses its identity =="
# Regression: an anonymous auto-join must never take over an id that is still live, or
# Store.Join rotates the credential underneath whoever is relying on it.
$script:Session = ""
$rejoin = Rpc "initialize" @{
    protocolVersion = "2025-11-25"
    capabilities = @{}
    clientInfo = @{ name = $Client; version = "0.0.0-selftest" }
}
Check "restart reuses the identity it had" ((Note $rejoin) -match "id mcp:$([regex]::Escape($Client))\b") (Note $rejoin)

Write-Host "== 22. groups: catalogue, self-service, and expansion =="
$ownerHeaders = @{ "Content-Type" = "application/json"; "Authorization" = "Bearer $OwnerToken" }
function OwnerPost($path, $body) {
    try {
        $r = Invoke-WebRequest -Uri "$Server$path" -Method Post -Headers $ownerHeaders `
            -Body ($body | ConvertTo-Json -Compress) -UseBasicParsing
        return @{ Status = $r.StatusCode; Raw = "" }
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        $text = ""
        if ($_.Exception.Response) { $text = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() }
        return @{ Status = $code; Raw = $text }
    }
}

$made = OwnerPost "/api/groups" @{ name = "critics"; description = "reviewers" }
Check "owner creates a group" ($made.Status -eq 200) $made.Raw
$again = OwnerPost "/api/groups" @{ name = "critics"; description = "clash" }
Check "a taken name is refused, not silently suffixed" ($again.Status -eq 409) $again.Status

$catalog = Rpc "tools/call" @{ name = "pigeon_roles"; arguments = @{} }
Check "the catalogue lists the group" ((Note $catalog) -match "@critics") (Note $catalog)
Check "and shows its description" ((Note $catalog) -match "reviewers") (Note $catalog)

$joined = Rpc "tools/call" @{ name = "pigeon_roles"; arguments = @{ join = "critics" } }
Check "joining of your own accord works" ((Note $joined) -match "Joined @critics") (Note $joined)
$rejoin = Rpc "tools/call" @{ name = "pigeon_roles"; arguments = @{ join = "critics" } }
Check "joining twice says so" ((Note $rejoin) -match "Already in") (Note $rejoin)

Write-Host "== 23. an owner-assigned group is binding =="
$t2 = (Invoke-WebRequest -Uri "$Server/api/join" -Method Post -Headers $ownerHeaders `
    -Body (@{ id = "mcp:critic2"; alias = "critic2"; kind = "agent"; harness = "mcp"; wake = "mention" } | ConvertTo-Json -Compress) `
    -UseBasicParsing).Content | ConvertFrom-Json | Select-Object -ExpandProperty token
OwnerPost "/api/groups/critics/assign" @{ identity = "mcp:critic2" } | Out-Null
$asCritic = @{ "Content-Type" = "application/json"; "Authorization" = "Bearer $t2" }
try {
    Invoke-WebRequest -Uri "$Server/api/roles/leave" -Method Post -Headers $asCritic `
        -Body (@{ group = "critics" } | ConvertTo-Json -Compress) -UseBasicParsing | Out-Null
    Check "an owner-assigned group refuses to be left" $false "leave succeeded"
} catch {
    Check "an owner-assigned group refuses to be left" ([int]$_.Exception.Response.StatusCode -eq 400) ([int]$_.Exception.Response.StatusCode)
}

Write-Host "== 24. a group mention reaches everyone in it =="
# The sender is in the group, and the sender is dropped from their own mentions, so "everyone
# in critics" minus the author is exactly one person here.
$send = Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = "@critics take a look at this" } }
Check "the mention expanded past the author" ((Note $send) -match "1 id\(s\): mcp:critic2") (Note $send)

Write-Host "== 25. the sender is dropped from their own mentions =="
# Regression: a mention used to include the author, which was a curiosity for a person and
# is routine for a group - "@critics" written by a critic would wake the critic.
$self = Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = "@critics noting this myself" } }
Check "the sender is not in their own mentions" ((Note $self) -match "1 id\(s\)") (Note $self)
Check "and names only the other member" ((Note $self) -match "mcp:critic2") (Note $self)
Check "and not themselves" ((Note $self) -notmatch "mcp:ps-selftest") (Note $self)

Write-Host "== 26. filter: group =="
$grouped = Rpc "tools/call" @{ name = "pigeon_read"; arguments = @{ from = 0; filter = "group:critics" } }
$groupView = Note $grouped
Check "group filter returns group traffic" ($groupView -match "take a look") $groupView
Check "group filter returns the critor's own messages" ($groupView -match "noting this myself") $groupView
Check "group filter drops unrelated traffic" ($groupView -notmatch "window filler") $groupView

Write-Host "== 27. deleting a group clears membership =="
OwnerPost "/api/groups/critics/delete" @{} | Out-Null
$afterDelete = Rpc "tools/call" @{ name = "pigeon_roles"; arguments = @{} }
Check "the group is gone" ((Note $afterDelete) -notmatch "@critics") (Note $afterDelete)
$whoAfter = Rpc "tools/call" @{ name = "pigeon_who"; arguments = @{} }
Check "and nobody still claims the role" ((Note $whoAfter) -notmatch "roles=\[critics\]") (Note $whoAfter)

Write-Host "== 27b. the owner can forget a stale identity =="
Invoke-WebRequest -Uri "$Server/api/join" -Method Post -Headers $ownerHeaders `
    -Body (@{ id = "mcp:ghost"; alias = "ghost"; kind = "agent"; harness = "mcp"; wake = "none" } | ConvertTo-Json -Compress) -UseBasicParsing | Out-Null
$withGhost = Rpc "tools/call" @{ name = "pigeon_who"; arguments = @{} }
Check "the ghost is in the roster" ((Note $withGhost) -match "@ghost") (Note $withGhost)
$forgot = OwnerPost "/api/identity/mcp:ghost/forget" @{}
Check "forget accepts" ($forgot.Status -eq 204) $forgot.Raw
$noGhost = Rpc "tools/call" @{ name = "pigeon_who"; arguments = @{} }
Check "and the roster is shorter" ((Note $noGhost) -notmatch "@ghost") (Note $noGhost)
$ownerForbid = OwnerPost "/api/identity/owner/forget" @{}
Check "the owner cannot be forgotten" ($ownerForbid.Status -eq 404) $ownerForbid.Status

Write-Host "== 28. an escaped mention is prose, not an address =="
# Regression: resolution is purely textual, so writing ABOUT a name woke everyone named.
# "@\" is the escape; the backslash sits before the "@" and so cannot begin a token.
$escaped = Rpc "tools/call" @{ name = "pigeon_send"; arguments = @{ text = 'roster: @\critic2 and @\probe are just names here' } }
Check "escaped names resolve to nothing" ((Note $escaped) -notmatch "mentions resolved") (Note $escaped)
$live = Rpc "tools/call" @{ name = "pigeon_send"; arguments= @{ text = "but @critic2 is addressed" } }
Check "an unescaped one still resolves" ((Note $live) -match "mcp:critic2") (Note $live)

Write-Host "== 29. presence can carry state, and cursors are readable =="
$hb = Invoke-WebRequest -Uri "$Server/api/heartbeat" -Method Post -Headers $asCritic `
    -Body (@{ state = "generating"; activity = "tool: read" } | ConvertTo-Json -Compress) -UseBasicParsing
Check "heartbeat accepts a state" ($hb.StatusCode -eq 204) $hb.StatusCode
$whoState = Rpc "tools/call" @{ name = "pigeon_who"; arguments = @{} }
Check "who shows the state" ((Note $whoState) -match "state=generating") (Note $whoState)

$cursors = (Invoke-WebRequest -Uri "$Server/api/cursors?channel=$Channel" -Headers $ownerHeaders -UseBasicParsing).Content
$seated = @($cursors | ConvertFrom-Json)
Check "read receipts exist for the seated" ($seated.Count -ge 1) $cursors
Check "a receipt carries since/head/unread" ($cursors -match "unread") $cursors
try {
    Invoke-WebRequest -Uri "$Server/api/cursors?channel=nonexistent" -Headers $ownerHeaders -UseBasicParsing | Out-Null
    Check "an unknown channel is 404" $false "no exception"
} catch {
    Check "an unknown channel is 404" ([int]$_.Exception.Response.StatusCode -eq 404) ([int]$_.Exception.Response.StatusCode)
}

Write-Host "== 30. the owner can edit what a channel is for =="
$described = OwnerPost "/api/channels/$Channel/describe" @{ description = "adapter test bench" }
Check "describe accepts" ($described.Status -eq 200) $described.Status
$channelsNow = (Invoke-WebRequest -Uri "$Server/api/channels" -UseBasicParsing).Content
Check "the description stuck" ($channelsNow -match "adapter test bench") $channelsNow
try {
    Invoke-WebRequest -Uri "$Server/api/channels/$Channel/describe" -Method Post -Headers $asCritic `
        -Body (@{ description = "hijack" } | ConvertTo-Json -Compress) -UseBasicParsing | Out-Null
    Check "a non-owner cannot edit it" $false "no exception"
} catch {
    Check "a non-owner cannot edit it" ([int]$_.Exception.Response.StatusCode -eq 403) ([int]$_.Exception.Response.StatusCode)
}

Write-Host "== 31. unassign takes someone out of a group =="
OwnerPost "/api/groups" @{ name = "bench"; description = "test crew" } | Out-Null
OwnerPost "/api/groups/bench/assign" @{ identity = "mcp:critic2" } | Out-Null
$groupView = (Invoke-WebRequest -Uri "$Server/api/groups" -Headers $ownerHeaders -UseBasicParsing).Content
Check "the owner assigned them" ($groupView -match "mcp:critic2") $groupView
OwnerPost "/api/groups/bench/unassign" @{ identity = "mcp:critic2" } | Out-Null
$afterUnassign = (Invoke-WebRequest -Uri "$Server/api/groups" -Headers $ownerHeaders -UseBasicParsing).Content
Check "unassign removed them" ((Note $afterUnassign) -notmatch "mcp:critic2") $afterUnassign
OwnerPost "/api/groups/bench/delete" @{} | Out-Null

Write-Host "== 32. an MCP handshake does not wipe a bridge-reported status =="
$statusJson = '{"harness":"opencode","model":"deepseek-v4","modelSource":"observed","session":"ses_test"}'
Invoke-WebRequest -Uri "$Server/api/status" -Method Post -Headers $asCritic `
    -Body (@{ status = $statusJson } | ConvertTo-Json -Compress) -UseBasicParsing | Out-Null
Rpc "tools/list" null $t2 | Out-Null   # a bare MCP request auto-joins the identity
$whoAfter = (Invoke-WebRequest -Uri "$Server/api/who" -Headers $ownerHeaders -UseBasicParsing).Content
Check "the observed status survived the handshake" ($whoAfter -match "observed") $whoAfter
Check "the model field is not overwritten by the client version" ($whoAfter -match "deepseek-v4") $whoAfter

Write-Host "== 33. hot is visible, not just counted =="
$hotChannel = "hot-selftest"
OwnerPost "/api/channels" @{ name = $hotChannel; description = "hot counter test" } | Out-Null
OwnerPost "/api/membership" @{ identity = "mcp:critic2"; channel = $hotChannel } | Out-Null
$list = (Invoke-WebRequest -Uri "$Server/api/channels" -Headers $ownerHeaders -UseBasicParsing).Content | ConvertFrom-Json
$fresh = @($list | Where-Object { $_.Name -eq $hotChannel })
Check "a fresh channel is not hot" ($fresh.Count -eq 1 -and -not $fresh[0].Hot) ($fresh | ConvertTo-Json -Compress)
for ($i = 1; $i -le 6; $i++) {
    Invoke-WebRequest -Uri "$Server/api/messages" -Method Post -Headers $asCritic `
        -Body (@{ channel = $hotChannel; text = "consecutive agent message $i" } | ConvertTo-Json -Compress) `
        -UseBasicParsing | Out-Null
}
$listAfter = (Invoke-WebRequest -Uri "$Server/api/channels" -Headers $ownerHeaders -UseBasicParsing).Content | ConvertFrom-Json
$hotNow = @($listAfter | Where-Object { $_.Name -eq $hotChannel })
Check "the channel is flagged hot after the threshold" ($hotNow.Count -eq 1 -and $hotNow[0].Hot) ($hotNow | ConvertTo-Json -Compress)
OwnerPost "/api/channels/$hotChannel/delete" @{} | Out-Null

Write-Host ""
if ($script:Fails -eq 0) { Write-Host "ALL PASS" -ForegroundColor Green; exit 0 }
Write-Host "$script:Fails FAILURE(S)" -ForegroundColor Red
exit 1