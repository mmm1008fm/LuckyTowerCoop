# Подключаемая библиотека (". tools\devlib.ps1"): быстрые отладочные команды без запуска отдельного pwsh.
function Dev([string]$Command, [int]$Port = 47800, [int]$TimeoutMs = 5000) {
    $udp = [System.Net.Sockets.UdpClient]::new()
    $udp.Client.ReceiveTimeout = $TimeoutMs
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Command)
        [void]$udp.Send($bytes, $bytes.Length, '127.0.0.1', $Port)
        $ep = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
        [Text.Encoding]::UTF8.GetString($udp.Receive([ref]$ep))
    } catch [System.Net.Sockets.SocketException] {
        "NO REPLY ($($_.Exception.SocketErrorCode))"
    } finally { $udp.Dispose() }
}
function DevA([string]$c) { Dev $c 47800 }
function DevB([string]$c) { Dev $c 47801 }

# Позиция из ответа "where": P1/P2 → [x, y]
function Get-Pos([string]$where, [string]$who) {
    $m = [regex]::Match($where, "(?m)^$who [^\r\n]*pos=\(([-\d.]+), ([-\d.]+)")
    if ($m.Success) { , @([double]$m.Groups[1].Value, [double]$m.Groups[2].Value) } else { $null }
}
function Get-Room([string]$where) { [regex]::Match($where, 'room=(\S+)').Groups[1].Value }

# Ошибки из логов обеих копий: сгруппированные исключения/ошибки мода
function Get-Errors([int]$Port, [int]$Lines = 4000) {
    $log = Dev "log $Lines" $Port
    ($log -split "`n") | Where-Object { ($_ -match 'Exception|\[Error|NullReference') -and $_ -notmatch 'Dev\] >' } |
        ForEach-Object { ($_ -replace '^\d+ ', '').Trim() } | Group-Object | Sort-Object Count -Descending
}
