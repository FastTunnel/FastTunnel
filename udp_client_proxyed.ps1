# Target server address (if testing NAT traversal, change this to your public IP and mapped port)
$serverIP = "127.0.0.1"
$serverPort = 1276

$udpClient = New-Object System.Net.Sockets.UdpClient
$remoteEndpoint = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Parse($serverIP), $serverPort)

Write-Host "[*] UDP client started, sending messages to $serverIP`:$serverPort every 2 seconds..." -ForegroundColor Green

try {
    while ($true) {
        $message = "hello"
        $sendBytes = [System.Text.Encoding]::UTF8.GetBytes($message)
        
        # Send message
        $udpClient.Send($sendBytes, $sendBytes.Length, $remoteEndpoint)
        Write-Host "[*] Sent: $message, waiting for server response..." -ForegroundColor Cyan
        
        # Receive server response (set receive timeout to prevent blocking)
        $udpClient.Client.ReceiveTimeout = 5000
        $receivedData = $udpClient.Receive([ref]$remoteEndpoint)
        $response = [System.Text.Encoding]::UTF8.GetString($receivedData)
        Write-Host "[*] Received server response: $response`n" -ForegroundColor Yellow
        
        # Wait 2 seconds
        Start-Sleep -Seconds 2
    }
}
catch {
    Write-Host "`n[*] Client exited or error occurred: $_" -ForegroundColor Red
}
finally {
    $udpClient.Close()
}
