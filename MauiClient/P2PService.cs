using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace MauiClient;

public class P2PService
{
    private const string SignalUrl = "http://localhost:5000/signal";
    private const int ChunkSize = 1024 * 1024; // 1MB chunks
    private const string BackupBaseDir = @"C:\backupclient";

    public record FileMetadata(string FileId, string FileName, long TotalSize, int TotalChunks);
    public record FileChunk(string FileId, int ChunkIndex, byte[] Data);

    public async Task<string> GetPublicIpAsync()
    {
        string localIP = string.Empty;
        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                localIP = ip.ToString();
                break;
            }
        }
        return localIP;
    }

    public async Task RegisterAsync(string clientId, string ip, int port)
    {
        using var client = new HttpClient();
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("address", $"{ip}:{port}")
        });
        await client.PostAsync($"{SignalUrl}/register", content);
    }

    public async Task<string?> GetPeerAddressAsync(string peerId)
    {
        using var client = new HttpClient();
        try 
        {
            var json = await client.GetStringAsync($"{SignalUrl}/get?client_id={peerId}");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("address", out var addr))
            {
                return addr.GetString();
            }
        }
        catch { }
        return null;
    }

    public async Task StartListenerAsync(int port, Action<string> onMessageLogged)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        onMessageLogged($"Listening for chunked transfer on port {port}...");

        if (!Directory.Exists(BackupBaseDir))
            Directory.CreateDirectory(BackupBaseDir);

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(async () => await HandleIncomingConnection(client, onMessageLogged));
        }
    }

    private async Task HandleIncomingConnection(TcpClient client, Action<string> onMessageLogged)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new BinaryReader(stream);

            // Read Packet Type (0 = Metadata, 1 = Chunk)
            byte packetType = reader.ReadByte();
            int payloadLength = reader.ReadInt32();
            byte[] payload = reader.ReadBytes(payloadLength);

            if (packetType == 0) // Metadata
            {
                var metadata = JsonSerializer.Deserialize<FileMetadata>(Encoding.UTF8.GetString(payload));
                onMessageLogged($"Received Metadata: {metadata.FileName} ({metadata.TotalSize} bytes, {metadata.TotalChunks} chunks)");
                
                // Initialize temp directory for file
                var tempDir = Path.Combine(BackupBaseDir, "temp", metadata.FileId);
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                
                // Save metadata for reference
                await File.WriteAllTextAsync(Path.Combine(tempDir, "metadata.json"), JsonSerializer.Serialize(metadata));
            }
            else if (packetType == 1) // Chunk
            {
                var chunk = JsonSerializer.Deserialize<FileChunk>(Encoding.UTF8.GetString(payload));
                
                var tempDir = Path.Combine(BackupBaseDir, "temp", chunk.FileId);
                var chunkPath = Path.Combine(tempDir, $"{chunk.ChunkIndex}.part");
                
                await File.WriteAllBytesAsync(chunkPath, chunk.Data);
                onMessageLogged($"Received Chunk {chunk.ChunkIndex} for {chunk.FileId}");

                // Check if all chunks received
                await TryAssembleFile(chunk.FileId, onMessageLogged);
            }
        }
        catch (Exception ex)
        {
            onMessageLogged($"Connection Error: {ex.Message}");
        }
    }

    private async Task TryAssembleFile(string fileId, Action<string> onMessageLogged)
    {
        var tempDir = Path.Combine(BackupBaseDir, "temp", fileId);
        if (!Directory.Exists(tempDir)) return;

        var metadataJson = await File.ReadAllTextAsync(Path.Combine(tempDir, "metadata.json"));
        var metadata = JsonSerializer.Deserialize<FileMetadata>(metadataJson);

        var partFiles = Directory.GetFiles(tempDir, "*.part");
        if (partFiles.Length == metadata.TotalChunks)
        {
            onMessageLogged($"All {metadata.TotalChunks} chunks received. Assembling file...");
            
            var finalPath = Path.Combine(BackupBaseDir, metadata.FileName);
            using (var finalStream = File.Create(finalPath))
            {
                for (int i = 0; i < metadata.TotalChunks; i++)
                {
                    var partPath = Path.Combine(tempDir, $"{i}.part");
                    var partData = await File.ReadAllBytesAsync(partPath);
                    await finalStream.WriteAsync(partData, 0, partData.Length);
                }
            }
            
            onMessageLogged($"File successfully assembled: {finalPath}");
            Directory.Delete(tempDir, true); // Cleanup
        }
    }

    public async Task SendFileChunkedAsync(string ip, int port, string filePath, Action<string> onProgress)
    {
        var fileInfo = new FileInfo(filePath);
        var fileId = Guid.NewGuid().ToString("N");
        var totalChunks = (int)Math.Ceiling((double)fileInfo.Length / ChunkSize);

        var metadata = new FileMetadata(fileId, fileInfo.Name, fileInfo.Length, totalChunks);
        
        // 1. Send Metadata
        onProgress($"Sending metadata for {fileInfo.Name}...");
        await SendPacketAsync(ip, port, 0, JsonSerializer.SerializeToUtf8Bytes(metadata));

        // 2. Send Chunks
        using var fileStream = File.OpenRead(filePath);
        byte[] buffer = new byte[ChunkSize];
        int bytesRead;
        int chunkIndex = 0;

        while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            var chunkData = new byte[bytesRead];
            Array.Copy(buffer, chunkData, bytesRead);
            
            var chunk = new FileChunk(fileId, chunkIndex, chunkData);
            onProgress($"Sending chunk {chunkIndex + 1}/{totalChunks}...");
            
            await SendPacketAsync(ip, port, 1, JsonSerializer.SerializeToUtf8Bytes(chunk));
            chunkIndex++;
        }
        onProgress("All chunks sent!");
    }

    private async Task SendPacketAsync(string ip, int port, byte type, byte[] payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(ip, port);
        using var stream = client.GetStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(type);
        writer.Write(payload.Length);
        writer.Write(payload);
        await stream.FlushAsync();
    }
}
