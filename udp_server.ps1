# Auto-detect current terminal encoding
$currentEncoding = [System.Console]::OutputEncoding

# Bind local IP and port
$endpoint = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any, 9999)
$udpServer = New-Object System.Net.Sockets.UdpClient 9999

Write-Host "[*] UDP Server started, listening on 0.0.0.0:9999 ..." -ForegroundColor Green
Write-Host "[*] Press Ctrl+C to stop the server." -ForegroundColor Gray

while ($true) {
    try {
        # Receive data from client
        $receivedData = $udpServer.Receive([ref]$endpoint)
        
        # Check if data is not null before processing
        if ($receivedData -and $receivedData.Length -gt 0) {
            $message = $currentEncoding.GetString($receivedData)
            Write-Host "[*] Received from $($endpoint.Address):$($endpoint.Port): $message" -ForegroundColor Cyan
            
            # Reply "ok"
            $response = "ok"
            $sendBytes = $currentEncoding.GetBytes($response)
            $udpServer.Send($sendBytes, $sendBytes.Length, $endpoint)
            Write-Host "[*] Replied to $($endpoint.Address):$($endpoint.Port): $response" -ForegroundColor Yellow
        }
    }
    catch {
        # Graceful exit on Ctrl+C, suppress system noise
        if ($_.Exception.Message -notmatch "A request to send or receive data|forcibly terminated") {
            Write-Host "Error occurred: $_" -ForegroundColor Red
        }
        break # Exit the while loop
    }
}

# Clean up resources
$udpServer.Close()
Write-Host "[*] Server safely closed." -ForegroundColor Green