# Отправка отладочной команды в игру (мод LuckyTowerCoop, Dev.CommandServer=true).
# Пример: pwsh tools/dev.ps1 dump ; pwsh tools/dev.ps1 "hold p2 right 1"
param([Parameter(Mandatory)][string]$Command, [int]$Port = 47800, [int]$TimeoutMs = 5000)
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
