# Pigeon anti-loop test kit: hot-counter verification via synthetic agent messages.
# Requires the pigeon server running on $Server.
param(
    [string]$Server = "http://127.0.0.1:8199",
    [Parameter(Mandatory = $true)][string]$OwnerToken,
    [Parameter(Mandatory = $true)][string]$ProbeToken
)

function ApiPost($path, $body, $token) {
    $headers = @{ "Content-Type" = "application/json" }
    if ($token) { $headers["Authorization"] = "Bearer $token" }
    try {
        $r = Invoke-WebRequest -Uri "$Server$path" -Method Post -Headers $headers -Body (ConvertTo-Json $body) -UseBasicParsing
        return $r.Content
    } catch {
        Write-Host "POST $path failed: $_"
        return $null
    }
}

Write-Host "== 1. Ensure probe is seated in #ping-pong =="
ApiPost "/api/channels/ping-pong/join" @{} $ProbeToken | Out-Null

Write-Host "== 2. Post 7 agent messages in one thread (threshold=6) =="
$root = ApiPost "/api/messages" @{ channel = "ping-pong"; text = "ping #1 @probe (thread root)" } $ProbeToken
$rootId = (ConvertFrom-Json $root).id
Write-Host "thread root id: $rootId"
for ($i = 2; $i -le 7; $i++) {
    $body = @{ channel = "ping-pong"; thread = $rootId; text = "pong #$i @probe" }
    ApiPost "/api/messages" $body $ProbeToken | Out-Null
}

Write-Host "== 3. Check hot flag =="
$headers = @{ "Authorization" = "Bearer $ProbeToken" }
$chan = Invoke-WebRequest -Uri "$Server/api/channels" -Headers $headers -UseBasicParsing
foreach ($c in (ConvertFrom-Json $chan.Content)) {
    Write-Host "channel $($c.id): hot=$($c.hot)"
}
$msgs = Invoke-WebRequest -Uri "$Server/api/messages?channel=ping-pong&since=0" -Headers $headers -UseBasicParsing
foreach ($m in (ConvertFrom-Json $msgs.Content)) {
    Write-Host "[$($m.id)] $($m.author.alias): $($m.text) hot=$($m.hot)"
}

Write-Host "== 4. Human message resets the counter =="
ApiPost "/api/messages" @{ channel = "ping-pong"; thread = $rootId; text = "human check: loop is over" } $OwnerToken | Out-Null
$chan = Invoke-WebRequest -Uri "$Server/api/channels" -Headers $headers -UseBasicParsing
foreach ($c in (ConvertFrom-Json $chan.Content)) {
    Write-Host "after human: channel $($c.id): hot=$($c.hot)"
}
