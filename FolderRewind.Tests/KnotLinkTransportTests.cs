using FolderRewind.Services.KnotLink;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FolderRewind.Tests;

[TestClass]
public sealed class KnotLinkTransportTests
{
    private static readonly byte[] Magic = { 0x4B, 0x4B, 0x00, 0x02 };

    [TestMethod]
    public async Task TcpClient_UsesService20LengthPrefixAndReadsFramedReply()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var replyReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = new KlTcpClient(TimeSpan.FromHours(1));
        client.OnDataReceivedAsync = value =>
        {
            replyReceived.TrySetResult(value);
            return Task.CompletedTask;
        };

        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync("127.0.0.1", port);
        using var serverClient = await acceptTask;
        await using var stream = serverClient.GetStream();

        await client.SendAsync("cmd=PING");
        Assert.AreEqual("cmd=PING", await ReadFrameAsync(stream));

        await WriteFrameAsync(stream, "status=ok;message=PONG");
        Assert.AreEqual(
            "status=ok;message=PONG",
            await replyReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task TcpClient_CanUseSdkMagicV2FramingWhenExplicitlyRequested()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var client = new KlTcpClient(
            TimeSpan.FromHours(1),
            KnotLinkFrameFormat.MagicV2);
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync("127.0.0.1", port);
        using var serverClient = await acceptTask;
        await using var stream = serverClient.GetStream();

        await client.SendAsync("cmd=PING");

        Assert.AreEqual("cmd=PING", await ReadMagicFrameAsync(stream));
    }

    [TestMethod]
    public async Task QueryAdapter_UsesInstanceQuerierAndReturnsReply()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync();
            await using var stream = serverClient.GetStream();
            Assert.AreEqual(
                "0x00000020-0x00000010&*&cmd=PING",
                await ReadFrameAsync(stream));
            await WriteFrameAsync(stream, "status=ok;message=PONG");
        });

        var response = await OpenSocketQueryAdapter.QueryAsync(
            "0x00000020",
            "0x00000010",
            "cmd=PING",
            "127.0.0.1",
            port,
            5000);

        Assert.AreEqual("status=ok;message=PONG", response);
        await serverTask;
    }

    [TestMethod]
    public async Task TcpClient_ReportsStoppedAfterPeerDisconnects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var client = new KlTcpClient(TimeSpan.FromHours(1));
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync("127.0.0.1", port);
        using var serverClient = await acceptTask;
        Assert.IsTrue(client.Running);

        serverClient.Close();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.IsFalse(client.Running);
    }

    [TestMethod]
    public void HostInitializationState_RemainsInitializedUntilReset()
    {
        var state = new KnotLinkInitializationState();

        state.Record(senderInitialized: true, responserInitialized: true);
        Assert.IsTrue(state.IsInitialized);
        Assert.IsTrue(state.SenderInitialized);
        Assert.IsTrue(state.ResponserInitialized);

        state.Reset();
        Assert.IsFalse(state.IsInitialized);
        Assert.IsFalse(state.SenderInitialized);
        Assert.IsFalse(state.ResponserInitialized);
    }

    [TestMethod]
    public void HostInitializationState_PreservesPartialInitializationDetails()
    {
        var state = new KnotLinkInitializationState();

        state.Record(senderInitialized: true, responserInitialized: false);

        Assert.IsFalse(state.IsInitialized);
        Assert.IsTrue(state.SenderInitialized);
        Assert.IsFalse(state.ResponserInitialized);
    }

    [TestMethod]
    public async Task Responser_RegistersAfterCallbackIsInstalled()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync();
            await using var stream = serverClient.GetStream();
            Assert.AreEqual("app-socket", await ReadFrameAsync(stream));
            await WriteFrameAsync(stream, "request-1&*&cmd=PING");
            return await ReadFrameAsync(stream);
        });

        await using var responser = new OpenSocketResponser(
            "app",
            "socket",
            "127.0.0.1",
            port,
            question => Task.FromResult(question == "cmd=PING" ? "status=ok" : "status=error"));

        Assert.AreEqual(
            "request-1&*&status=ok",
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<string> ReadFrameAsync(NetworkStream stream)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header);

        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
        Assert.IsTrue(length > 0 && length <= 16 * 1024 * 1024);
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload);
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<string> ReadMagicFrameAsync(NetworkStream stream)
    {
        var magic = new byte[4];
        await ReadExactlyAsync(stream, magic);
        CollectionAssert.AreEqual(Magic, magic);
        return await ReadFrameAsync(stream);
    }

    private static async Task WriteFrameAsync(NetworkStream stream, string value)
    {
        var payload = Encoding.UTF8.GetBytes(value);
        var header = new byte[4];
        BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length)).CopyTo(header, 0);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
            {
                throw new EndOfStreamException("The peer closed before a complete KnotLink frame was received.");
            }

            offset += read;
        }
    }
}
