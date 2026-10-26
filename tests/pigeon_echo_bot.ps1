# Pigeon ping-pong etiquette experiment setup:
# 1) registers @probe (scripted echo-bot) on the running server,
# 2) seats it in #ping-pong,
# 3) runs the echo loop: every message mentioning @probe gets one pong reply
#    (mentioning the author back), capped at MaxReplies.
# The real agent side is this session (@a05575a36b92457ebf0aebbe9e01fa74).
# Metrics: how many wake-turns the real agent spends before stopping the chain.
param(
    [string]$Server = "http://127.0.0.1:8199",
    [Parameter(Mandatory = $true)][string]$OwnerToken,
    [int]$MaxReplies = 3,
    [int]$RunSeconds = 180
)

function ApiPost($path, $body, $token) {
    $headers = @{ "Content-Type" = "application/json" }
    if ($token) { $headers["Authorization"] = "Bearer $token" }
    try {
        $r = Invoke-WebRequest -Uri "$Server$path" -Method Post -Headers $headers -Body (ConvertTo-Json $body) -UseBasicParsing -TimeoutSec 10
        return $r.Content
    } catch { Write-Host "POST $path failed: $_"; return $null }
}

# register probe
$join = ApiPost "/api/join" @{ id = "test-probe"; alias = "probe"; kind = "agent"; harness = "echo-bot"; model = "script"; wake = "none" } $null
$probeToken = (ConvertFrom-Json $join).token
Write-Host "probe token: $probeToken"
ApiPost "/api/membership" @{ identity = "test-probe"; channel = "ping-pong" } $OwnerToken | Out-Null

$deadline = (Get-Date).AddSeconds($RunSeconds)
$cursor = 0
$replies = 0
while ((Get-Date) -lt $deadline -and $replies -lt $MaxReplies) {
    try {
        $r = Invoke-WebRequest -Uri "$Server/api/messages?channel=ping-pong&since=$cursor&wait=25" -Headers @{ "Authorization" = "Bearer $probeToken" } -UseBasicParsing -TimeoutSec 30
        $msgs = ConvertFrom-Json $r.Content
        if ($msgs.Count -eq 0) { continue }
        $cursor = ($msgs | Measure-Object -Maximum id).Maximum
        foreach ($m in $msgs) {
            if ($m.author.id -eq "test-probe") { continue }
            if ($m.mentions -contains "test-probe") {
                $replies++
                $thread = if ($m.thread) { $m.thread } else { $m.id }
                ApiPost "/api/messages" @{ channel = "ping-pong"; thread = $thread; text = "pong $replies @$($m.author.alias)" } $probeToken | Out-Null
                Write-Host "probe -> pong $replies (thread $thread)"
                if ($replies -ge $MaxReplies) { break }
            }
        }
    } catch { Write-Host "poll error: $_"; Start-Sleep -Seconds 3 }
}
Write-Host "echo-bot done: $replies replies"
